using System.Buffers.Binary;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Orbit.Core.Documents;

/// <summary>Bounded TAR/PAX archives. Only regular files/directories are materialized;
/// gzip members, TAR checksums, end markers and all decoded bytes are validated before publication.</summary>
public sealed class TarArchiveEngine
{
    public const int MaximumEntries = 10000;
    public const long MaximumSingleEntryBytes = 1024L * 1024 * 1024;
    public const long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024;
    public const long MaximumArchiveBytes = 3L * 1024 * 1024 * 1024;
    private const string Prefix = ".orbit-tar-";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(value =>
    { var crc = (uint)value; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320); return crc; }).ToArray();
    private readonly TimeSpan _timeout;

    public TarArchiveEngine(TimeSpan? operationTimeout = null)
    { _timeout = operationTimeout ?? TimeSpan.FromMinutes(20); if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromHours(24)) throw new ArgumentOutOfRangeException(nameof(operationTimeout)); }

    public async Task<DocumentResult> CreateAsync(IReadOnlyList<string> paths, TarCreateOptions options, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths); ArgumentNullException.ThrowIfNull(options); ct.ThrowIfCancellationRequested();
        if (options.Format is not ("TAR" or "TAR.GZ")) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_0A9AE32EDBBC"));
        var compression = options.Compression switch { "Fastest" => CompressionLevel.Fastest, "Optimal" => CompressionLevel.Optimal, "SmallestSize" => CompressionLevel.SmallestSize, _ => throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_2DB880ED331C")) };
        var requested = paths.ToArray(); using var deadline = Deadline(ct); var token = deadline.Token; string? temporary = null, parent = null;
        try
        {
            // Snapshot before creating the private output, including when custom output is inside a source.
            var plan = await Task.Run(() => ArchiveSourcePlanner.Create(requested, token), token).ConfigureAwait(false); progress?.Report(.05);
            parent = await ParentAsync(plan.Roots[0], options.OutputDirectory, token).ConfigureAwait(false); temporary = await TemporaryAsync(parent, token).ConfigureAwait(false);
            var prepared = Path.Combine(temporary, "archive" + (options.Format == "TAR" ? ".tar" : ".tar.gz"));
            await using (var output = new FileStream(prepared, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await using var bounded = new LimitWriteStream(output, MaximumArchiveBytes);
                await using var gzip = options.Format == "TAR.GZ" ? new GZipStream(bounded, compression, true) : null;
                await using var writer = new TarWriter((Stream?)gzip ?? bounded, TarEntryFormat.Pax, leaveOpen: true);
                long copied = 0; var count = 0;
                foreach (var item in plan.Items)
                {
                    token.ThrowIfCancellationRequested(); ArchiveSourcePlanner.Recheck(item, token);
                    var entry = new PaxTarEntry(item.IsDirectory ? TarEntryType.Directory : TarEntryType.RegularFile, item.EntryName);
                    if (item.IsDirectory) await writer.WriteEntryAsync(entry, token).ConfigureAwait(false);
                    else
                    {
                        await using var input = await OpenSourceItemAsync(item, token).ConfigureAwait(false);
                        entry.DataStream = input; await writer.WriteEntryAsync(entry, token).ConfigureAwait(false); copied = checked(copied + item.Bytes);
                    }
                    count++; progress?.Report(.05 + .75 * (plan.TotalBytes > 0 ? copied / (double)plan.TotalBytes : count / (double)plan.Items.Count));
                }
                if (copied != plan.TotalBytes) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_D487C0AB403D"));
            }
            await using (var check = await OpenArchiveAsync(prepared, token).ConfigureAwait(false))
            { var actual = await ReadPlanAsync(check, options.Format, token).ConfigureAwait(false); if (actual.Count != plan.Items.Count || actual.Sum(item => item.Bytes) != plan.TotalBytes) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_C1C3030657D6")); }
            progress?.Report(.95); token.ThrowIfCancellationRequested(); var root = plan.Roots[0];
            var stem = plan.Roots.Count > 1 ? "Arclo" : plan.Items[0].IsDirectory ? Path.GetFileName(root) : Path.GetFileNameWithoutExtension(root);
            var bytes = new FileInfo(prepared).Length; var target = CommitFile(prepared, parent, Short(stem) + "_archive", options.Format == "TAR" ? ".tar" : ".tar.gz", token);
            progress?.Report(1); return new DocumentResult(target, bytes, options.Format, plan.TotalBytes);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw TimedOut(); }
        catch (Exception exception) when (Expected(exception)) { throw Explain(exception); }
        finally { await CleanupAsync(temporary, parent).ConfigureAwait(false); }
    }

    public async Task<TarArchiveListing> InspectAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var deadline = Deadline(ct);
        try
        {
            var source = NormalizeSource(path); var format = SourceFormat(source); await using var input = await OpenArchiveAsync(source, deadline.Token).ConfigureAwait(false);
            var plan = await ReadPlanAsync(input, format, deadline.Token).ConfigureAwait(false);
            input.Position = 0; var fingerprint = await SHA256.HashDataAsync(input, deadline.Token).ConfigureAwait(false);
            return new TarArchiveListing(source, format, Array.AsReadOnly(plan.ToArray()), plan.Sum(item => item.Bytes), Convert.ToHexString(fingerprint));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw TimedOut(); }
        catch (Exception exception) when (Expected(exception)) { throw Explain(exception); }
    }

    public async Task<DocumentResult> ExtractAsync(string path, string? outputDirectory = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var deadline = Deadline(ct); var token = deadline.Token; string? temporary = null, parent = null;
        try
        {
            var source = NormalizeSource(path); var format = SourceFormat(source); await using var input = await OpenArchiveAsync(source, token).ConfigureAwait(false);
            var plan = await ReadPlanAsync(input, format, token).ConfigureAwait(false); progress?.Report(.1);
            parent = await ParentAsync(source, outputDirectory, token).ConfigureAwait(false); temporary = await TemporaryAsync(parent, token).ConfigureAwait(false);
            var payload = Path.Combine(temporary, "payload"); Directory.CreateDirectory(payload); var root = Path.GetFullPath(payload) + Path.DirectorySeparatorChar;
            input.Position = 0; await using var gzip = format == "TAR.GZ" ? new GZipStream(input, CompressionMode.Decompress, true) : null;
            await using var reader = new TarReader((Stream?)gzip ?? input, true); var index = 0; long copied = 0; var total = plan.Sum(item => item.Bytes);
            while (await reader.GetNextEntryAsync(false, token).ConfigureAwait(false) is { } entry)
            {
                token.ThrowIfCancellationRequested(); if (entry.EntryType == TarEntryType.GlobalExtendedAttributes) continue;
                var actual = ValidateEntry(entry); if (index >= plan.Count || actual != plan[index++]) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_D8F3502C09A2"));
                var target = Path.GetFullPath(Path.Combine(payload, actual.RelativeName.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_76C811E6DE59"));
                SafeDirectory(payload, actual.IsDirectory ? target : Path.GetDirectoryName(target)!, token);
                if (!actual.IsDirectory)
                {
                    await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                    var bytes = entry.DataStream is null ? 0 : await CopyAsync(entry.DataStream, output, actual.Bytes, token).ConfigureAwait(false);
                    if (bytes != actual.Bytes) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_47361398D6AB")); copied += bytes;
                }
                progress?.Report(.1 + .85 * (total > 0 ? copied / (double)total : index / (double)Math.Max(1, plan.Count)));
            }
            if (index != plan.Count || copied != total) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_EA07003ED90E"));
            token.ThrowIfCancellationRequested(); var published = DocumentFiles.CommitDirectory(payload, parent, Short(SourceStem(source)) + "_extracted", token); progress?.Report(1);
            return new DocumentResult(published, copied, "FOLDER", input.Length);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw TimedOut(); }
        catch (Exception exception) when (Expected(exception)) { throw Explain(exception); }
        finally { await CleanupAsync(temporary, parent).ConfigureAwait(false); }
    }

    /// <summary>Extracts exact catalog rows from the same fully validated archive snapshot.
    /// Every unselected member is also read and validated before any output is published.</summary>
    public async Task<DocumentResult> ExtractSelectedAsync(string path, IReadOnlyList<string> selectedNames, string? outputDirectory,
        string expectedFingerprint, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // Capture the UI selection before the first await; mutable caller lists are never retained.
        var requested = ArchiveEntrySelection.Snapshot(selectedNames);
        if (expectedFingerprint is null || expectedFingerprint.Length != 64 || expectedFingerprint.Any(value => value is not (>= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f')))
            throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_2BFE45EDC1CF"));
        var expected = Convert.FromHexString(expectedFingerprint);
        using var deadline = Deadline(ct); var token = deadline.Token; string? temporary = null, parent = null;
        try
        {
            var source = NormalizeSource(path); var format = SourceFormat(source); await using var input = await OpenArchiveAsync(source, token).ConfigureAwait(false);
            var plan = await ReadPlanAsync(input, format, token).ConfigureAwait(false);
            input.Position = 0; var fingerprint = await SHA256.HashDataAsync(input, token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(expected, fingerprint))
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_BC8023BE9BAE"));
            var selected = ArchiveEntrySelection.Create(requested, plan.Select(item => new ArchiveSelectionEntry(item.RelativeName, item.IsDirectory)).ToArray());
            progress?.Report(.1);
            parent = await ParentAsync(source, outputDirectory, token).ConfigureAwait(false); temporary = await TemporaryAsync(parent, token).ConfigureAwait(false);
            var payload = Path.Combine(temporary, "payload"); Directory.CreateDirectory(payload); var root = Path.GetFullPath(payload) + Path.DirectorySeparatorChar;
            input.Position = 0; await using var gzip = format == "TAR.GZ" ? new GZipStream(input, CompressionMode.Decompress, true) : null;
            await using var reader = new TarReader((Stream?)gzip ?? input, true); var index = 0; long processed = 0, written = 0; var total = plan.Sum(item => item.Bytes);
            while (await reader.GetNextEntryAsync(false, token).ConfigureAwait(false) is { } entry)
            {
                token.ThrowIfCancellationRequested(); if (entry.EntryType == TarEntryType.GlobalExtendedAttributes) continue;
                var actual = ValidateEntry(entry); if (index >= plan.Count || actual != plan[index++]) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_D8F3502C09A2"));
                bool include = selected.Contains(actual.RelativeName); string? target = null;
                if (include)
                {
                    target = Path.GetFullPath(Path.Combine(payload, actual.RelativeName.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_76C811E6DE59"));
                    SafeDirectory(payload, actual.IsDirectory ? target : Path.GetDirectoryName(target)!, token);
                }
                if (!actual.IsDirectory)
                {
                    await using var output = include ? new FileStream(target!, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous) : null;
                    var bytes = entry.DataStream is null ? 0 : await CopyAsync(entry.DataStream, (Stream?)output ?? Stream.Null, actual.Bytes, token).ConfigureAwait(false);
                    if (bytes != actual.Bytes) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_47361398D6AB"));
                    processed += bytes; if (include) written += bytes;
                }
                progress?.Report(.1 + .85 * (total > 0 ? processed / (double)total : index / (double)Math.Max(1, plan.Count)));
            }
            if (index != plan.Count || processed != total) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_EA07003ED90E"));
            token.ThrowIfCancellationRequested(); var published = DocumentFiles.CommitDirectory(payload, parent, Short(SourceStem(source)) + "_selected", token); progress?.Report(1);
            return new DocumentResult(published, written, "FOLDER", input.Length);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw TimedOut(); }
        catch (Exception exception) when (Expected(exception)) { throw Explain(exception); }
        finally { await CleanupAsync(temporary, parent).ConfigureAwait(false); }
    }

    public async Task<DocumentResult> WriteListAsync(string path, string? outputDirectory = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var deadline = Deadline(ct); var token = deadline.Token; string? temporary = null, parent = null;
        try
        {
            var source = NormalizeSource(path); var format = SourceFormat(source); await using var input = await OpenArchiveAsync(source, token).ConfigureAwait(false);
            var plan = await ReadPlanAsync(input, format, token).ConfigureAwait(false); parent = await ParentAsync(source, outputDirectory, token).ConfigureAwait(false); temporary = await TemporaryAsync(parent, token).ConfigureAwait(false);
            var prepared = Path.Combine(temporary, "contents.txt");
            await using (var writer = new StreamWriter(prepared, false, new UTF8Encoding(false)))
            {
                await writer.WriteLineAsync((format + " — " + plan.Count.ToString(CultureInfo.InvariantCulture) + global::Orbit.Core.Localization.L.Get("Text_EC508B15B9BD")).AsMemory(), token).ConfigureAwait(false);
                foreach (var entry in plan) await writer.WriteLineAsync(((entry.IsDirectory ? "DIR" : entry.Bytes.ToString(CultureInfo.InvariantCulture)) + "\t" + entry.RelativeName).AsMemory(), token).ConfigureAwait(false);
            }
            var bytes = new FileInfo(prepared).Length; token.ThrowIfCancellationRequested(); var target = CommitFile(prepared, parent, Short(SourceStem(source)) + "_contents", ".txt", token);
            return new DocumentResult(target, bytes, "TXT", input.Length);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw TimedOut(); }
        catch (Exception exception) when (Expected(exception)) { throw Explain(exception); }
        finally { await CleanupAsync(temporary, parent).ConfigureAwait(false); }
    }

    private static async Task<List<TarArchiveEntry>> ReadPlanAsync(FileStream input, string format, CancellationToken token)
    {
        GzipInfo? info = null;
        if (format == "TAR.GZ") info = await ReadGzipInfoAsync(input, token).ConfigureAwait(false);
        input.Position = 0;
        await using (var compressed = format == "TAR.GZ" ? new GzipBoundaryStream(input) : null)
        await using (var gzip = format == "TAR.GZ" ? new GZipStream(compressed!, CompressionMode.Decompress, true) : null)
        await using (var counted = new CountingReadStream((Stream?)gzip ?? input, MaximumArchiveBytes))
        {
            await CheckTarAsync(counted, token).ConfigureAwait(false);
            if (info is not null && (input.Position != input.Length || counted.Bytes != info.Length || counted.Crc != info.Crc)) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_EDE8808A2FE7"));
        }
        input.Position = 0; await using var decoded = format == "TAR.GZ" ? new GZipStream(input, CompressionMode.Decompress, true) : null;
        await using var reader = new TarReader((Stream?)decoded ?? input, true); var entries = new List<TarArchiveEntry>(); long total = 0;
        while (await reader.GetNextEntryAsync(false, token).ConfigureAwait(false) is { } entry)
        {
            token.ThrowIfCancellationRequested(); if (entry.EntryType == TarEntryType.GlobalExtendedAttributes) continue;
            var item = ValidateEntry(entry); if (entries.Count >= MaximumEntries || (total = checked(total + item.Bytes)) > MaximumExpandedBytes) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_424A67F8087B"));
            entries.Add(item); if (entry.DataStream is not null && await CopyAsync(entry.DataStream, Stream.Null, item.Bytes, token).ConfigureAwait(false) != item.Bytes) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_883E58FC1966"));
        }
        ValidateTree(entries, token); return entries;
    }

    private static TarArchiveEntry ValidateEntry(TarEntry entry)
    {
        if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory) || entry.LinkName.Length != 0) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_0F09EBED4FE2"));
        var name = ArchiveEngine.NormalizeEntryName(entry.Name, out var slash); var directory = entry.EntryType == TarEntryType.Directory;
        if (name.Count(character => character == '/') > ArchiveSourcePlanner.MaximumDepth || slash && !directory || entry.Length < 0 || entry.Length > MaximumSingleEntryBytes || directory && entry.Length != 0) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_4BFC37830F11"));
        if (entry is PaxTarEntry pax) foreach (var pair in pax.ExtendedAttributes) CheckPaxKey(pair.Key, pair.Value, false);
        return new TarArchiveEntry(name + (directory ? "/" : ""), directory, entry.Length);
    }
    private static void ValidateTree(List<TarArchiveEntry> entries, CancellationToken token)
    {
        var names = new Dictionary<string, TarArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in entries) if (!names.TryAdd(item.RelativeName.TrimEnd('/'), item)) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_87D075FABD46"));
        var sorted = names.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray(); string? previous = null;
        foreach (var name in sorted)
        {
            token.ThrowIfCancellationRequested();
            for (var slash = name.IndexOf('/'); slash >= 0; slash = name.IndexOf('/', slash + 1))
                if (names.TryGetValue(name[..slash], out var parent) && !parent.IsDirectory) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_7D236BD02EA5"));
            if (previous is not null)
            {
                var left = previous.Split('/'); var right = name.Split('/');
                for (var i = 0; i < Math.Min(left.Length, right.Length) && left[i].Equals(right[i], StringComparison.OrdinalIgnoreCase); i++)
                    if (!left[i].Equals(right[i], StringComparison.Ordinal)) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_4B8D14C52E7F"));
            }
            previous = name;
        }
    }

    private static async Task CheckTarAsync(CountingReadStream input, CancellationToken token)
    {
        var header = new byte[512]; var buffer = new byte[81920]; int records = 0, files = 0; long total = 0, metadata = 0; long? paxSize = null; bool pending = false;
        while (true)
        {
            token.ThrowIfCancellationRequested(); await input.ReadExactlyAsync(header, token).ConfigureAwait(false);
            if (header.All(value => value == 0))
            {
                if (pending) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_B8E3D45E250D"));
                await input.ReadExactlyAsync(header, token).ConfigureAwait(false); if (header.Any(value => value != 0)) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_94382238E921"));
                long tail = 0; int read; while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                { token.ThrowIfCancellationRequested(); tail += read; if (tail > 1024 * 1024 || buffer.AsSpan(0, read).ContainsAnyExcept((byte)0)) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_4FD7BF382EF8")); }
                if (input.Bytes % 512 != 0) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_4121F2C7BC14")); return;
            }
            if (++records > MaximumEntries * 3) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_272CF4A674FC"));
            var checksum = Octal(header.AsSpan(148, 8)); long unsigned = 0, signed = 0;
            for (var i = 0; i < 512; i++) { unsigned += i is >= 148 and < 156 ? 32 : header[i]; signed += i is >= 148 and < 156 ? 32 : (sbyte)header[i]; }
            if (checksum != unsigned && checksum != signed) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_868796611707"));
            var size = Octal(header.AsSpan(124, 12)); var type = header[156];
            if (type is not (0 or (byte)'0' or (byte)'5' or (byte)'x' or (byte)'g' or (byte)'L')) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_1DB5090BD261"));
            if (header.AsSpan(157, 100).ContainsAnyExcept((byte)0)) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_69818DFA209C"));
            var meta = type is (byte)'x' or (byte)'g' or (byte)'L';
            if (meta)
            {
                if (pending || size > 65536 || (metadata = checked(metadata + size)) > 16 * 1024 * 1024) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_8437E58E6124"));
                var body = new byte[(int)size]; await input.ReadExactlyAsync(body, token).ConfigureAwait(false);
                if (type != (byte)'L') paxSize = ReadPax(body, type == (byte)'g');
                else _ = ArchiveEngine.NormalizeEntryName(Utf8.GetString(body).TrimEnd('\0'), out _);
                pending = type != (byte)'g';
            }
            else
            {
                if (paxSize.HasValue && paxSize != size) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_6F421DD93111")); paxSize = null; pending = false;
                if (++files > MaximumEntries || size > MaximumSingleEntryBytes || type == (byte)'5' && size != 0 || (total = checked(total + size)) > MaximumExpandedBytes) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_D37C6F5045FD"));
                await CopyExactAsync(input, Stream.Null, size, token).ConfigureAwait(false);
            }
            var padding = (int)((512 - size % 512) % 512);
            if (padding != 0) { await input.ReadExactlyAsync(buffer.AsMemory(0, padding), token).ConfigureAwait(false); if (buffer.AsSpan(0, padding).ContainsAnyExcept((byte)0)) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_8CC8D65140F0")); }
        }
    }
    private static long? ReadPax(byte[] body, bool global)
    {
        int position = 0; long? size = null; var seen = new HashSet<string>(StringComparer.Ordinal);
        while (position < body.Length)
        {
            var space = Array.IndexOf(body, (byte)' ', position); if (space < position || space - position > 10 || !int.TryParse(Encoding.ASCII.GetString(body, position, space - position), NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                || length < space - position + 4 || length > body.Length - position || body[position + length - 1] != '\n') throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_2663DC87C368"));
            var line = Utf8.GetString(body, space + 1, position + length - space - 2); var equal = line.IndexOf('=');
            if (equal is < 1 or > 256) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_60A1C9DD4BD2")); var key = line[..equal]; var value = line[(equal + 1)..];
            if (!seen.Add(key)) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_F0E73B4595AE")); CheckPaxKey(key, value, global);
            if (key == "size") { if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 0 || parsed > MaximumSingleEntryBytes) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_08BD6C84802A")); size = parsed; }
            position += length;
        }
        return size;
    }
    private static void CheckPaxKey(string key, string value, bool global)
    {
        if (key.Contains("sparse", StringComparison.OrdinalIgnoreCase) || key is "SCHILY.realsize" or "SUN.holesdata" or "linkpath" || key == "SCHILY.filetype" && value != "file") throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_0677777334B8"));
        if (global && key is "size" or "path") throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_543DF4FBB13C"));
        if (key == "path") _ = ArchiveEngine.NormalizeEntryName(value, out _);
    }
    private static long Octal(ReadOnlySpan<byte> field)
    {
        // The supported sizes fit standard octal. Reject base256/negative/overflow encodings.
        long value = 0; bool ended = false, started = false; foreach (var character in field)
        {
            if (character is 0 or 32) { if (started) ended = true; continue; }
            if (character is < (byte)'0' or > (byte)'7' || ended) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_C9E6661404A4")); started = true; value = checked(value * 8 + character - '0');
        }
        return value;
    }

    // BCL performs DEFLATE/gzip checksum validation. Independently verify the actual
    // decoded CRC/length against the final footer and require exact compressed EOF.
    private static async Task<GzipInfo> ReadGzipInfoAsync(FileStream input, CancellationToken token)
    {
        if (input.Length < 20) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_54501436C756"));
        input.Position = 0; var single = new byte[1]; uint crc = uint.MaxValue; var count = 0;
        async ValueTask<int> Header()
        {
            if (++count > 65536 || input.Position >= input.Length - 8) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_C1E27957D625"));
            await input.ReadExactlyAsync(single, token).ConfigureAwait(false); var value = single[0]; crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8); return value;
        }
        if (await Header() != 31 || await Header() != 139 || await Header() != 8) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_90029A345A70"));
        var flags = await Header(); if ((flags & 224) != 0) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_AEE8F907F66D"));
        for (var i = 0; i < 6; i++) _ = await Header();
        if ((flags & 4) != 0) { var extra = await Header() | await Header() << 8; for (var i = 0; i < extra; i++) _ = await Header(); }
        if ((flags & 8) != 0) while (await Header() != 0) { }
        if ((flags & 16) != 0) while (await Header() != 0) { }
        if ((flags & 2) != 0)
        {
            var expected = (ushort)~crc; var bytes = new byte[2]; await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt16LittleEndian(bytes) != expected) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_270B37AD73EC"));
        }
        input.Position = input.Length - 8; var footer = new byte[8]; await input.ReadExactlyAsync(footer, token).ConfigureAwait(false);
        return new GzipInfo(BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(4)), BinaryPrimitives.ReadUInt32LittleEndian(footer));
    }
    private sealed record GzipInfo(long Length, uint Crc);

    // GZipStream may ignore buffered non-gzip trailing bytes. Bulk reads stop before
    // the final 16 bytes, which are supplied singly (at most 16 tiny reads per archive).
    // A genuine final footer must be consumed; an earlier member ending in a bulk read
    // cannot swallow that boundary. Exact EOF + independent CRC/ISIZE reject hidden tails,
    // including junk followed by a copied valid footer, without a second DEFLATE decoder.
    private sealed class GzipBoundaryStream(FileStream source) : Stream
    {
        private int Bound(int requested) => (int)Math.Min(requested, source.Position < source.Length - 16 ? Math.Min(65536, source.Length - 16 - source.Position) : 1);
        public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, Bound(count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => await source.ReadAsync(buffer[..Bound(buffer.Length)], token).ConfigureAwait(false);
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => throw new NotSupportedException();
        public override long Position { get => source.Position; set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<long> CopyAsync(Stream input, Stream output, long maximum, CancellationToken token)
    { var buffer = new byte[81920]; long total = 0; int read; while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0) { total = checked(total + read); if (total > maximum) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_0A8A61F3FB9D")); await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false); } return total; }
    private static async Task CopyExactAsync(Stream input, Stream output, long count, CancellationToken token)
    { var buffer = new byte[81920]; while (count > 0) { var read = (int)Math.Min(count, buffer.Length); await input.ReadExactlyAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false); await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false); count -= read; } }
    private static async Task<FileStream> OpenSourceItemAsync(ArchiveSourceItem item, CancellationToken token)
    {
        var opened = await OpenAsync(item.Path, false, token).ConfigureAwait(false); try { ArchiveSourcePlanner.Recheck(item, token); if (opened.Length != item.Bytes) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_1845A5FA41B1")); return opened; } catch { await opened.DisposeAsync(); throw; }
    }
    private static Task<FileStream> OpenArchiveAsync(string path, CancellationToken token) => OpenAsync(path, true, token);
    private static async Task<FileStream> OpenAsync(string path, bool archive, CancellationToken token)
    {
        var pending = Task.Run(() =>
        {
            ArchiveSourcePlanner.SafeAttributes(path, token); var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            try { ArchiveSourcePlanner.SafeAttributes(path, token); if ((File.GetAttributes(file.SafeFileHandle) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || file.Length < 0 || file.Length > (archive ? MaximumArchiveBytes : MaximumSingleEntryBytes)) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_05B6AB019D0E")); return file; } catch { file.Dispose(); throw; }
        }, CancellationToken.None);
        try { return await pending.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); } catch { _ = pending.ContinueWith(task => { if (task.IsCompletedSuccessfully) task.Result.Dispose(); else _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default); throw; }
    }
    private static string NormalizeSource(string path)
    { ArgumentException.ThrowIfNullOrWhiteSpace(path); var full = Path.GetFullPath(path); if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal) || full.IndexOf(':', full.Length > 1 && full[1] == ':' ? 2 : 0) >= 0) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_D2DA8F5367E2")); _ = SourceFormat(full); return full; }
    private static string SourceFormat(string path) => path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ? "TAR.GZ" : path.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) ? "TAR" : throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_A3D5641BC76F"));
    private static string SourceStem(string path) { var name = Path.GetFileName(path); return name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? name[..^7] : name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) ? name[..^4] : Path.GetFileNameWithoutExtension(name); }
    private static string Short(string value) { if (value.Length <= 180) return value; value = value[..180]; return char.IsHighSurrogate(value[^1]) ? value[..^1] : value; }
    private static string CommitFile(string prepared, string directory, string stem, string suffix, CancellationToken token)
    { for (var index = 1; index <= 10000; index++) { token.ThrowIfCancellationRequested(); var target = Path.Combine(directory, stem + (index == 1 ? "" : " (" + index.ToString(CultureInfo.InvariantCulture) + ")") + suffix); try { File.Move(prepared, target, false); return target; } catch (IOException) when (File.Exists(target) || Directory.Exists(target)) { } } throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_945D1C62D69D")); }
    private static async Task<string> ParentAsync(string source, string? requested, CancellationToken token)
    { var parent = Path.TrimEndingDirectorySeparator(string.IsNullOrWhiteSpace(requested) ? Path.GetDirectoryName(source)! : Path.GetFullPath(requested)); if (!await Task.Run(() => Directory.Exists(parent), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false)) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_F5D6DF7FEC9A")); return parent; }
    private static async Task<string> TemporaryAsync(string parent, CancellationToken token)
    {
        var path = Path.Combine(parent, Prefix + Guid.NewGuid().ToString("N")); var creating = Task.Run(() => Directory.CreateDirectory(path), CancellationToken.None);
        try { await creating.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); return path; }
        catch { _ = creating.ContinueWith(async task => { if (task.IsCompletedSuccessfully) await CleanupAsync(path, parent).ConfigureAwait(false); else _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap(); throw; }
    }
    private static void SafeDirectory(string root, string path, CancellationToken token)
    {
        var relative = Path.GetRelativePath(root, path); var current = root;
        foreach (var part in relative == "." ? Array.Empty<string>() : relative.Split(Path.DirectorySeparatorChar))
        { token.ThrowIfCancellationRequested(); if (part is "." or "..") throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_0E7362FF67B9")); current = Path.Combine(current, part); if (File.Exists(current) || Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_047CE90570C3")); Directory.CreateDirectory(current); if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_EE5BAC95165D")); }
    }
    private static async Task CleanupAsync(string? temporary, string? parent)
    {
        if (temporary is null || parent is null || !string.Equals(Path.GetDirectoryName(temporary), Path.TrimEndingDirectorySeparator(parent), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(temporary).StartsWith(Prefix, StringComparison.Ordinal)) return;
        var deleting = Task.Run(async () => { for (var attempt = 0; attempt < 11; attempt++) { try { if (Directory.Exists(temporary) && (File.GetAttributes(temporary) & FileAttributes.ReparsePoint) == 0) Directory.Delete(temporary, true); return; } catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException) { } if (attempt < 10) await Task.Delay(Math.Min(300, 25 << Math.Min(attempt, 4))).ConfigureAwait(false); } });
        try { await deleting.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (TimeoutException) { _ = deleting.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted); }
    }
    private CancellationTokenSource Deadline(CancellationToken token) { var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(_timeout); return deadline; }
    private static bool Expected(Exception exception) => exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or TimeoutException or OverflowException or System.Security.SecurityException;
    private static DocumentProcessingException Explain(Exception exception) => new(global::Orbit.Core.Localization.L.Get("Text_56929D6BE2BB") + exception.Message, exception);
    private static DocumentProcessingException TimedOut() => new(global::Orbit.Core.Localization.L.Get("Text_353E4BA72630"));

    private sealed class CountingReadStream(Stream input, long limit) : Stream
    {
        private uint _crc = uint.MaxValue; public long Bytes { get; private set; } public uint Crc => ~_crc;
        private void Count(ReadOnlySpan<byte> data) { Bytes = checked(Bytes + data.Length); if (Bytes > limit) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_9B1D235A1048")); foreach (var value in data) _crc = CrcTable[(_crc ^ value) & 255] ^ (_crc >> 8); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) { var read = await input.ReadAsync(buffer, token).ConfigureAwait(false); Count(buffer.Span[..read]); return read; }
        public override int Read(byte[] buffer, int offset, int count) { var read = input.Read(buffer, offset, count); Count(buffer.AsSpan(offset, read)); return read; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => throw new NotSupportedException(); public override long Position { get => Bytes; set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class LimitWriteStream(Stream output, long maximum) : Stream
    {
        private long _bytes;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) { _bytes = checked(_bytes + buffer.Length); if (_bytes > maximum) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_4E298F398EE9")); await output.WriteAsync(buffer, token).ConfigureAwait(false); }
        public override void Write(byte[] buffer, int offset, int count) { _bytes = checked(_bytes + count); if (_bytes > maximum) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_4E298F398EE9")); output.Write(buffer, offset, count); }
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true; public override long Length => _bytes; public override long Position { get => _bytes; set => throw new NotSupportedException(); }
        public override void Flush() => output.Flush(); public override Task FlushAsync(CancellationToken token) => output.FlushAsync(token); public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    }
}
