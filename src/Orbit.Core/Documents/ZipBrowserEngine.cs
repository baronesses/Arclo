using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Orbit.Core.Documents;

/// <summary>Validated local ZIP browsing and exact, snapshot-checked selective extraction. No entry attributes are applied.</summary>
public sealed class ZipBrowserEngine
{
    public const int MaximumEntries = 10000;
    public const int MaximumSelectedEntries = 1000;
    public const int MaximumSelectionCharacters = 131072;
    public const long MaximumSingleEntryBytes = 1024L * 1024 * 1024;
    public const long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024;
    public const long MaximumArchiveBytes = 4L * 1024 * 1024 * 1024;
    private const int MaximumCentralDirectoryBytes = 64 * 1024 * 1024;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly Encoding Legacy = CodePagesEncodingProvider.Instance.GetEncoding(437)!;
    private static readonly uint[] CrcTable = CreateCrcTable();
    private readonly TimeSpan _operationTimeout;

    public ZipBrowserEngine(TimeSpan? operationTimeout = null)
    {
        _operationTimeout = operationTimeout ?? TimeSpan.FromMinutes(15);
        if (_operationTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
    }

    public async Task<ZipBrowserListing> InspectAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var timeout = Deadline(ct);
        try
        {
            var source = NormalizeSource(path);
            await using var input = await OpenAsync(source, timeout.Token).ConfigureAwait(false);
            var plan = await Task.Run(() => ReadPlan(input, timeout.Token), timeout.Token).ConfigureAwait(false);
            input.Position = 0; var hash = await SHA256.HashDataAsync(input, timeout.Token).ConfigureAwait(false);
            return new ZipBrowserListing(source, Convert.ToHexString(hash), Array.AsReadOnly(plan.Select(item =>
                new ZipBrowserEntry(item.Name, item.IsDirectory, item.Length, item.CompressedLength)).ToArray()), plan.Sum(item => item.Length));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw TimedOut(); }
        catch (Exception exception) when (Expected(exception)) { throw Explain(exception); }
    }

    public Task<DocumentResult> ExtractSelectedAsync(string path, IReadOnlyList<string> selectedNames, string? outputDirectory = null,
        CancellationToken ct = default) => ExtractAsync(path, selectedNames, outputDirectory, null, requireFingerprint: false, ct);

    public Task<DocumentResult> ExtractSelectedAsync(string path, IReadOnlyList<string> selectedNames, string? outputDirectory,
        string? expectedFingerprint, CancellationToken ct = default) => ExtractAsync(path, selectedNames, outputDirectory, expectedFingerprint, requireFingerprint: true, ct);

    private async Task<DocumentResult> ExtractAsync(string path, IReadOnlyList<string> selectedNames, string? outputDirectory,
        string? expectedFingerprint, bool requireFingerprint, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var selection = SnapshotSelection(selectedNames);
        byte[]? expected = null;
        if (requireFingerprint)
        {
            if (expectedFingerprint is null || expectedFingerprint.Length != 64 || expectedFingerprint.Any(character => !Uri.IsHexDigit(character)))
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_CF83FFDA0A43"));
            expected = Convert.FromHexString(expectedFingerprint);
        }
        using var timeout = Deadline(ct); string? temporary = null; string? parent = null;
        try
        {
            var source = NormalizeSource(path);
            await using var input = await OpenAsync(source, timeout.Token).ConfigureAwait(false);
            var plan = await Task.Run(() => ReadPlan(input, timeout.Token), timeout.Token).ConfigureAwait(false);
            input.Position = 0; var actual = await SHA256.HashDataAsync(input, timeout.Token).ConfigureAwait(false);
            if (expected is not null && !CryptographicOperations.FixedTimeEquals(expected, actual))
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_1E9A07474846"));
            var known = plan.ToDictionary(item => item.Name, StringComparer.Ordinal);
            foreach (var name in selection) if (!known.ContainsKey(name))
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_AF9F77A7E220"));
            var selectedDirectories = selection.Where(name => known[name].IsDirectory).ToArray();
            var selected = plan.Where(item => selection.Contains(item.Name) || selectedDirectories.Any(directory => item.Name.StartsWith(directory, StringComparison.Ordinal))).ToHashSet();
            parent = await Task.Run(() => DocumentFiles.OutputDirectory(source, outputDirectory), timeout.Token).ConfigureAwait(false);
            temporary = Path.Combine(parent, ".orbit-document-" + Guid.NewGuid().ToString("N"));
            // Creation is awaited through completion before cleanup; all filesystem work runs off the dispatcher.
            await Task.Run(() => { timeout.Token.ThrowIfCancellationRequested(); Directory.CreateDirectory(temporary); }, CancellationToken.None).ConfigureAwait(false);
            var root = Path.GetFullPath(temporary) + Path.DirectorySeparatorChar; long written = 0;
            foreach (var item in plan)
            {
                timeout.Token.ThrowIfCancellationRequested(); var include = selected.Contains(item);
                string? destination = null;
                if (include)
                {
                    destination = Path.GetFullPath(Path.Combine(temporary, item.Name.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar)));
                    if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_8218D24AF345"));
                    CreateSafeDirectory(temporary, item.IsDirectory ? destination : Path.GetDirectoryName(destination)!, timeout.Token);
                }
                // CRC and decoded sizes are checked for every entry, including unselected payloads.
                // A bad hidden sibling must never turn a selective extract into partial success.
                if (include && !item.IsDirectory)
                {
                    await using var output = new FileStream(destination!, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                    await VerifyAndCopyAsync(input, item, output, timeout.Token).ConfigureAwait(false); written += item.Length;
                }
                else await VerifyAndCopyAsync(input, item, Stream.Null, timeout.Token).ConfigureAwait(false);
            }
            timeout.Token.ThrowIfCancellationRequested();
            var stem = Path.GetFileNameWithoutExtension(source);
            if (stem.Length > 180) { stem = stem[..180]; if (char.IsHighSurrogate(stem[^1])) stem = stem[..^1]; }
            var result = DocumentFiles.CommitDirectory(temporary, parent, stem + "_selected", timeout.Token);
            return new DocumentResult(result, written, "FOLDER", input.Length);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw TimedOut(); }
        catch (Exception exception) when (Expected(exception)) { throw Explain(exception); }
        finally
        {
            if (temporary is not null && parent is not null)
                for (var attempt = 0; attempt < 11; attempt++)
                {
                    DocumentFiles.CleanupTemporaryDirectory(temporary, parent); if (!Directory.Exists(temporary)) break;
                    if (attempt < 10) await Task.Delay(Math.Min(300, 25 << Math.Min(attempt, 4))).ConfigureAwait(false);
                }
        }
    }

    private CancellationTokenSource Deadline(CancellationToken ct)
    { var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(_operationTimeout); return timeout; }

    private static HashSet<string> SnapshotSelection(IReadOnlyList<string> names)
    {
        if (names is null || names.Count is < 1 or > MaximumSelectedEntries)
            throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_5554CA852C5A"));
        var result = new HashSet<string>(StringComparer.Ordinal); var length = 0;
        foreach (var name in names)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 2001 || (length = checked(length + name.Length)) > MaximumSelectionCharacters)
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_DE140EDE8CEB"));
            // IDs come from the validated listing; do not silently normalize an unrelated caller path into one.
            string normalized;
            try { normalized = ArchiveEngine.NormalizeEntryName(name, out var directory) + (directory ? "/" : ""); }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
            { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_6DDB46C12F25"), exception); }
            if (!string.Equals(name, normalized, StringComparison.Ordinal)) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_10AE73103FF2"));
            result.Add(name);
        }
        return result;
    }

    private static string NormalizeSource(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path); var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal)
            || full.IndexOf(':', full.Length > 1 && full[1] == ':' ? 2 : 0) >= 0) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_16D488ECE1F8"));
        return full;
    }

    private static async Task<FileStream> OpenAsync(string path, CancellationToken ct)
    {
        var opening = Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested(); RecheckSource(path, ct);
            var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            try
            {
                ct.ThrowIfCancellationRequested(); RecheckSource(path, ct);
                if ((File.GetAttributes(input.SafeFileHandle) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                    throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_9784B3C3CC94"));
                if (input.Length is < 22 or > MaximumArchiveBytes) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_B707680ABF6F"));
                return input;
            }
            catch { input.Dispose(); throw; }
        }, CancellationToken.None);
        try { return await opening.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); }
        catch
        {
            _ = opening.ContinueWith(task => { if (task.IsCompletedSuccessfully) task.Result.Dispose(); else _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default); throw;
        }
    }

    private static void RecheckSource(string path, CancellationToken ct)
    {
        var depth = 0;
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            ct.ThrowIfCancellationRequested(); if (++depth > 256) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_244031F4311B"));
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_B7D8A6EB402F"));
            if (depth == 1 && (attributes & FileAttributes.Directory) != 0) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_F9C66EFC4CD2"));
        }
    }

    private static List<Entry> ReadPlan(FileStream input, CancellationToken ct)
    {
        var tailLength = (int)Math.Min(input.Length, 65557); var tail = Read(input, input.Length - tailLength, tailLength); var end = -1;
        for (var offset = tail.Length - 22; offset >= 0; offset--)
            if (U32(tail, offset) == 0x06054b50 && offset + 22 + U16(tail, offset + 20) == tail.Length) { end = offset; break; }
        if (end < 0) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_565EA3C6C6F4"));
        var endOffset = input.Length - tailLength + end;
        if (U16(tail, end + 4) != 0 || U16(tail, end + 6) != 0 || U16(tail, end + 8) != U16(tail, end + 10))
            throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_DC835547B450"));
        long count = U16(tail, end + 10), centralLength = U32(tail, end + 12), centralOffset = U32(tail, end + 16), centralEnd = endOffset;
        if (count == ushort.MaxValue || centralLength == uint.MaxValue || centralOffset == uint.MaxValue)
        {
            var locator = Read(input, endOffset - 20, 20);
            if (U32(locator, 0) != 0x07064b50 || U32(locator, 4) != 0 || U32(locator, 16) != 1) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_68BDE6D4D863"));
            var recordOffset = Long(locator, 8); var record = Read(input, recordOffset, 56); var recordSize = Long(record, 4);
            if (U32(record, 0) != 0x06064b50 || recordSize is < 44 or > 1048576 || recordOffset + 12 + recordSize != endOffset - 20
                || U32(record, 16) != 0 || U32(record, 20) != 0 || Long(record, 24) != Long(record, 32)) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_064F8A7B5379"));
            count = Long(record, 32); centralLength = Long(record, 40); centralOffset = Long(record, 48); centralEnd = recordOffset;
        }
        if (count > MaximumEntries || centralLength > MaximumCentralDirectoryBytes || centralOffset < 0 || centralOffset + centralLength != centralEnd)
            throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_81E8EEF63AE2"));
        var signature = Read(input, 0, 4);
        if (U32(signature, 0) != (count == 0 ? 0x06054b50u : 0x04034b50u)) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_82A149C60CDE"));
        var entries = new List<Entry>((int)count); var names = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase); long position = centralOffset, total = 0;
        for (var index = 0; index < count; index++)
        {
            ct.ThrowIfCancellationRequested(); var header = Read(input, position, 46);
            if (U32(header, 0) != 0x02014b50) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_28C0935E6F75"));
            var flags = U16(header, 8); var method = U16(header, 10); ValidateFlags(flags, method);
            var nameLength = U16(header, 28); var extraLength = U16(header, 30); var commentLength = U16(header, 32);
            if (nameLength is 0 or > 8192 || U16(header, 34) != 0 || position + 46 + nameLength + extraLength + commentLength > centralOffset + centralLength)
                throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_DEEB0C84EAF7"));
            var nameBytes = Read(input, position + 46, nameLength); var extra = Read(input, position + 46 + nameLength, extraLength);
            var raw = ((flags & 0x800) != 0 ? Utf8 : Legacy).GetString(nameBytes);
            var normalized = ArchiveEngine.NormalizeEntryName(raw, out var directorySuffix);
            if (normalized.Count(character => character == '/') >= 128) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_AEFC2C7068DE"));
            var attributes = U32(header, 38); var unix = (attributes >> 16) & 0xf000;
            if (unix is not (0 or 0x8000 or 0x4000) || (attributes & ((uint)FileAttributes.ReparsePoint | 8)) != 0)
                throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_8871E4230055"));
            var directory = directorySuffix || unix == 0x4000 || (attributes & (uint)FileAttributes.Directory) != 0;
            if (directory && unix == 0x8000) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_12ACB0AD59B5"));
            long length = U32(header, 24), compressed = U32(header, 20), local = U32(header, 42);
            var zip64 = length == uint.MaxValue || compressed == uint.MaxValue || local == uint.MaxValue;
            ReadExtra(extra, nameBytes, raw, ref length, ref compressed, ref local);
            if (length > MaximumSingleEntryBytes || (total = checked(total + length)) > MaximumExpandedBytes || compressed > MaximumArchiveBytes
                || (directory && length != 0) || (method == 0 && length != compressed)) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_0D904E4A66EF"));
            if (length > 64L * 1024 * 1024 && length / Math.Max(1d, compressed) > 2000) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_052D3F695202"));
            var entry = new Entry(normalized + (directory ? "/" : ""), directory, length, compressed, U32(header, 16), flags, method, local, 0, 0, nameBytes, zip64);
            if (!names.TryAdd(normalized, entry)) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_4550E735A5CA"));
            entries.Add(entry); position += 46 + nameLength + extraLength + commentLength;
        }
        if (position != centralOffset + centralLength) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_9B47F7D6FBE9"));
        ValidateTree(entries, names, ct);
        for (var index = 0; index < entries.Count; index++) entries[index] = ReadLocal(input, entries[index], centralOffset, ct);
        var ranges = entries.OrderBy(entry => entry.LocalOffset).ToArray();
        for (var index = 1; index < ranges.Length; index++) if (ranges[index].LocalOffset < ranges[index - 1].RecordEnd)
            throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_C6A67BAA435C"));
        return entries;
    }

    private static Entry ReadLocal(FileStream input, Entry entry, long centralOffset, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var header = Read(input, entry.LocalOffset, 30);
        if (entry.LocalOffset >= centralOffset || U32(header, 0) != 0x04034b50 || U16(header, 6) != entry.Flags || U16(header, 8) != entry.Method)
            throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_5D004214C9F4"));
        var nameLength = U16(header, 26); var extraLength = U16(header, 28);
        if (nameLength != entry.RawName.Length) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_299F2841B5E8"));
        var name = Read(input, entry.LocalOffset + 30, nameLength);
        if (!name.AsSpan().SequenceEqual(entry.RawName)) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_299F2841B5E8"));
        var extra = Read(input, entry.LocalOffset + 30 + nameLength, extraLength);
        long length = U32(header, 22), compressed = U32(header, 18), unusedOffset = 0;
        var zip64 = entry.Zip64 || length == uint.MaxValue || compressed == uint.MaxValue;
        ReadExtra(extra, name, ((entry.Flags & 0x800) != 0 ? Utf8 : Legacy).GetString(name), ref length, ref compressed, ref unusedOffset);
        var data = entry.LocalOffset + 30 + nameLength + extraLength; var end = checked(data + entry.CompressedLength);
        if (end > centralOffset) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_258A6AED4F8B"));
        if ((entry.Flags & 8) == 0)
        {
            if (U32(header, 14) != entry.Crc || length != entry.Length || compressed != entry.CompressedLength)
                throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_55B47E0B478C"));
        }
        else
        {
            if ((U32(header, 14) != 0 && U32(header, 14) != entry.Crc) || (length != 0 && length != entry.Length) || (compressed != 0 && compressed != entry.CompressedLength))
                throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_3096B59BA126"));
            var descriptorLength = zip64 ? 20 : 12;
            var descriptor = Read(input, end, (int)Math.Min(centralOffset - end, descriptorLength + 4)); var matches = new List<int>();
            foreach (var start in new[] { 0, 4 })
                if (descriptor.Length >= start + descriptorLength && (start == 0 || U32(descriptor, 0) == 0x08074b50)
                    && U32(descriptor, start) == entry.Crc
                    && (zip64 ? Long(descriptor, start + 4) : U32(descriptor, start + 4)) == entry.CompressedLength
                    && (zip64 ? Long(descriptor, start + 12) : U32(descriptor, start + 8)) == entry.Length) matches.Add(start + descriptorLength);
            if (matches.Count != 1) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_AC870291C078"));
            end += matches[0];
        }
        return entry with { DataOffset = data, RecordEnd = end };
    }

    private static void ValidateFlags(ushort flags, ushort method)
    {
        if ((flags & (1 | 0x40 | 0x2000)) != 0) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_ECBF3D2FCCD0"));
        if (method is not (0 or 8) || (flags & ~0x080e) != 0 || (method == 0 && (flags & 6) != 0))
            throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_FE80BCF35DDD"));
    }

    private static void ReadExtra(byte[] bytes, byte[] nameBytes, string name, ref long length, ref long compressed, ref long local)
    {
        var seen = new HashSet<ushort>(); var zip64Seen = false;
        for (var index = 0; index < bytes.Length;)
        {
            if (bytes.Length - index < 4) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_36E5EEC4065F"));
            var id = U16(bytes, index); var size = U16(bytes, index + 2); index += 4;
            if (index + size > bytes.Length || !seen.Add(id)) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_C35165C7119F"));
            if (id is 0x9901 or 0x0017) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_529BCF1D95D6"));
            if (id == 1)
            {
                zip64Seen = true; var cursor = index;
                long Next() { if (cursor + 8 > index + size) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_817FCDC20A6E")); var value = Long(bytes, cursor); cursor += 8; return value; }
                if (length == uint.MaxValue) length = Next(); if (compressed == uint.MaxValue) compressed = Next(); if (local == uint.MaxValue) local = Next();
            }
            if (id == 0x7075)
            {
                if (size < 5 || bytes[index] != 1 || U32(bytes, index + 1) != Crc(nameBytes)
                    || !string.Equals(Utf8.GetString(bytes, index + 5, size - 5), name, StringComparison.Ordinal))
                    throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_7EFBEA989496"));
            }
            if (id == 0x756e && (size < 6 || ((U16(bytes, index + 4) & 0xf000) is not (0 or 0x8000 or 0x4000))))
                throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_43DC3DB7B07E"));
            index += size;
        }
        if ((length == uint.MaxValue || compressed == uint.MaxValue || local == uint.MaxValue) && !zip64Seen) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_9F0BDCF6E1EF"));
    }

    private static void ValidateTree(List<Entry> entries, Dictionary<string, Entry> names, CancellationToken ct)
    {
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var path = entry.Name.TrimEnd('/');
            var slash = path.LastIndexOf('/');
            while (slash >= 0)
            {
                ct.ThrowIfCancellationRequested();
                var parent = path[..slash];
                if (names.TryGetValue(parent, out var item) && !item.IsDirectory) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_8BFD79676995"));
                path = parent; slash = path.LastIndexOf('/');
            }
        }
        // Retain only the at-most-10000 full paths. Expanding every implicit ancestor into a
        // dictionary could turn a small deeply nested catalog into gigabytes of live strings.
        // Entries sharing a case-insensitive directory prefix are adjacent in this ordering.
        var ordered = entries.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            ct.ThrowIfCancellationRequested(); var left = ordered[index - 1].Name; var right = ordered[index].Name; var start = 0;
            while (start < left.Length && start < right.Length)
            {
                var leftEnd = left.IndexOf('/', start); var rightEnd = right.IndexOf('/', start);
                if (leftEnd < 0 || rightEnd < 0) break;
                var a = left.AsSpan(start, leftEnd - start); var b = right.AsSpan(start, rightEnd - start);
                if (!a.Equals(b, StringComparison.OrdinalIgnoreCase)) break;
                if (!a.SequenceEqual(b)) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_9B5DEB539EFE"));
                start = leftEnd + 1;
            }
        }
    }

    private static async Task VerifyAndCopyAsync(FileStream input, Entry entry, Stream output, CancellationToken ct)
    {
        input.Position = entry.DataOffset; using var slice = new Slice(input, entry.CompressedLength);
        using var inflater = entry.Method == 8 ? new DeflateStream(slice, CompressionMode.Decompress, leaveOpen: true) : null;
        Stream decoded = inflater is null ? slice : inflater;
        var buffer = new byte[81920]; long copied = 0; uint crc = 0xffffffff; int count;
        while ((count = await decoded.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            ct.ThrowIfCancellationRequested(); copied += count;
            if (copied > entry.Length || copied > MaximumSingleEntryBytes) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_852267FCFCB0"));
            for (var index = 0; index < count; index++) crc = CrcTable[(crc ^ buffer[index]) & 255] ^ (crc >> 8);
            await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
        }
        if (copied != entry.Length || ~crc != entry.Crc || slice.Remaining != 0) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_8D016FC5AA39"));
    }

    private static void CreateSafeDirectory(string root, string target, CancellationToken ct)
    {
        var current = root;
        foreach (var segment in Path.GetRelativePath(root, target).Split(Path.DirectorySeparatorChar))
        {
            ct.ThrowIfCancellationRequested(); if (segment == ".") continue; current = Path.Combine(current, segment);
            Directory.CreateDirectory(current);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_41AF0AA212A8"));
        }
    }

    private static byte[] Read(FileStream input, long position, int count)
    {
        if (position < 0 || count < 0 || position > input.Length - count) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_7E0E152B82BF"));
        input.Position = position; var bytes = new byte[count]; input.ReadExactly(bytes); return bytes;
    }
    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static long Long(byte[] bytes, int offset)
    { var value = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8)); if (value > long.MaxValue) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_4AC7A482F8F8")); return (long)value; }
    private static uint[] CreateCrcTable()
    {
        var table = new uint[256]; for (uint index = 0; index < table.Length; index++)
        { var value = index; for (var bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) != 0 ? 0xedb88320u : 0); table[index] = value; } return table;
    }
    private static uint Crc(byte[] bytes) { uint value = 0xffffffff; foreach (var item in bytes) value = CrcTable[(value ^ item) & 255] ^ (value >> 8); return ~value; }
    private static bool Expected(Exception exception) => exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or TimeoutException or OverflowException;
    private static DocumentProcessingException TimedOut() => new(global::Orbit.Core.Localization.L.Get("Text_EEE83E9B1B7E"));
    private static DocumentProcessingException Explain(Exception exception) => new(exception switch
    {
        FileNotFoundException or DirectoryNotFoundException => global::Orbit.Core.Localization.L.Get("Text_B525460E1639"),
        UnauthorizedAccessException => global::Orbit.Core.Localization.L.Get("Text_30F9576ECD68"),
        InvalidDataException => global::Orbit.Core.Localization.L.Get("Text_DA3253F8A64F") + exception.Message,
        TimeoutException => global::Orbit.Core.Localization.L.Get("Text_2746493A5140"),
        _ => global::Orbit.Core.Localization.L.Get("Text_C04F57BAB128")
    }, exception);
    private sealed record Entry(string Name, bool IsDirectory, long Length, long CompressedLength, uint Crc,
        ushort Flags, ushort Method, long LocalOffset, long DataOffset, long RecordEnd, byte[] RawName, bool Zip64);
    private sealed class Slice : Stream
    {
        private readonly Stream _source;
        private readonly long _length;
        public Slice(Stream source, long length) { _source = source; _length = length; Remaining = length; }
        public long Remaining { get; private set; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => _length; public override long Position { get => _length - Remaining; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { var read = _source.Read(buffer, offset, (int)Math.Min(count, Remaining)); Remaining -= read; return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { var read = await _source.ReadAsync(buffer[..(int)Math.Min(buffer.Length, Remaining)], ct).ConfigureAwait(false); Remaining -= read; return read; }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
