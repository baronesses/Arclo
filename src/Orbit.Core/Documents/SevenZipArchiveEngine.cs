using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Orbit.Core.Documents;

/// <summary>Local 7z-only operations. Native extraction never receives an output filesystem path.</summary>
public sealed class SevenZipArchiveEngine
{
    public const int MaximumEntries = 10000;
    public const long MaximumSingleEntryBytes = 1024L * 1024 * 1024;
    public const long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024;
    public const long MaximumArchiveBytes = 3L * 1024 * 1024 * 1024;
    private const string Prefix = ".orbit-7zip-";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(value => { uint crc = (uint)value; for (int bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xEDB88320; return crc; }).ToArray();
    private readonly string? _configuredExecutable;
    private readonly TimeSpan _timeout;
    public SevenZipArchiveEngine(string? executablePath = null, TimeSpan? operationTimeout = null)
    {
        _configuredExecutable = executablePath; _timeout = operationTimeout ?? TimeSpan.FromMinutes(20);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromHours(24)) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
    }
    public bool Available => FindExecutable() is not null;
    public string ExecutablePath => FindExecutable() ?? throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_6FF5BEF4D058"));

    public Task<DocumentResult> CreateAsync(IReadOnlyList<string> paths, SevenZipCreateOptions options, IProgress<double>? progress = null, CancellationToken ct = default) => CreateCoreAsync(paths, options, progress, ct);
    public async Task<DocumentResult> CreateWithPasswordAsync(IReadOnlyList<string> paths, SevenZipCreateOptions options, SevenZipPassword password, bool encryptHeaders = true, IProgress<double>? progress = null, CancellationToken ct = default)
    { ArgumentNullException.ThrowIfNull(password); using var owned = password.Clone(); return await CreateCoreAsync(paths, options, progress, ct, owned, encryptHeaders).ConfigureAwait(false); }
    public Task<SevenZipArchiveListing> InspectAsync(string path, CancellationToken ct = default) => InspectCoreAsync(path, ct);
    public async Task<SevenZipArchiveListing> InspectAsync(string path, SevenZipPassword password, CancellationToken ct = default)
    { ArgumentNullException.ThrowIfNull(password); using var owned = password.Clone(); return await InspectCoreAsync(path, ct, owned).ConfigureAwait(false); }
    public Task<DocumentResult> ExtractAsync(string path, string? outputDirectory = null, IProgress<double>? progress = null, CancellationToken ct = default) => ExtractCoreAsync(path, outputDirectory, progress, ct);
    public async Task<DocumentResult> ExtractAsync(string path, string? outputDirectory, SevenZipPassword password, IProgress<double>? progress = null, CancellationToken ct = default)
    { ArgumentNullException.ThrowIfNull(password); using var owned = password.Clone(); return await ExtractCoreAsync(path, outputDirectory, progress, ct, owned).ConfigureAwait(false); }
    public Task<DocumentResult> WriteListAsync(string path, string? outputDirectory = null, CancellationToken ct = default) => WriteListCoreAsync(path, outputDirectory, ct);
    public async Task<DocumentResult> WriteListAsync(string path, string? outputDirectory, SevenZipPassword password, CancellationToken ct = default)
    { ArgumentNullException.ThrowIfNull(password); using var owned = password.Clone(); return await WriteListCoreAsync(path, outputDirectory, ct, owned).ConfigureAwait(false); }
    public Task<DocumentResult> ExtractSelectedAsync(string path, IReadOnlyList<string> names, string? outputDirectory, string expectedFingerprint, IProgress<double>? progress = null, CancellationToken ct = default)
        => ExtractSelectedCoreAsync(path, names, outputDirectory, expectedFingerprint, progress, ct);
    public async Task<DocumentResult> ExtractSelectedAsync(string path, IReadOnlyList<string> names, string? outputDirectory, string expectedFingerprint, SevenZipPassword password, IProgress<double>? progress = null, CancellationToken ct = default)
    { ArgumentNullException.ThrowIfNull(password); using var owned = password.Clone(); return await ExtractSelectedCoreAsync(path, names, outputDirectory, expectedFingerprint, progress, ct, owned).ConfigureAwait(false); }

    /// <summary>Checks protection metadata only; successful probing does not certify the file payloads.</summary>
    public async Task<SevenZipProtectionInfo> ProbeProtectionAsync(string path, CancellationToken ct = default)
    {
        using var deadline = Deadline(ct); var token = deadline.Token;
        try
        {
            var source = NormalizeSource(path); await using var lease = await OpenAsync(source, true, token).ConfigureAwait(false);
            var header = await ValidateHeaderAsync(lease, token).ConfigureAwait(false);
            if (SevenZipHeaderProtection.HasEncryptedHeader(header)) return new SevenZipProtectionInfo(source, SevenZipProtection.Headers);
            var entries = Parse(await TextCommandAsync(new[] { "l", "-slt", "-ba", "-sccUTF-8", "-bd", source }, null, token).ConfigureAwait(false), true);
            ValidateTree(entries);
            return new SevenZipProtectionInfo(source, entries.Any(entry => entry.Encrypted) ? SevenZipProtection.Data : SevenZipProtection.None);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw TimedOut(); }
        catch (Exception exception) when (Expected(exception)) { throw Explain(exception); }
    }

    private async Task<DocumentResult> CreateCoreAsync(IReadOnlyList<string> paths, SevenZipCreateOptions options, IProgress<double>? progress, CancellationToken ct, SevenZipPassword? password = null, bool encryptHeaders = true)
    {
        ArgumentNullException.ThrowIfNull(paths); ArgumentNullException.ThrowIfNull(options);
        var snapshot = paths.ToArray(); var compression = options.Compression; var outputDirectory = options.OutputDirectory;
        using var deadline = Deadline(ct); var token = deadline.Token; string? temporary = null;
        try
        {
            var level = compression switch { "Fastest" => "1", "Optimal" => "5", "SmallestSize" => "9", _ => throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_DD585316AC7E")) };
            var plan = await Task.Run(() => ArchiveSourcePlanner.Create(snapshot, token), token).ConfigureAwait(false);
            if (password is not null && !encryptHeaders && plan.TotalBytes == 0) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_C8CC6CD6BB4A"));
            foreach (var item in plan.Items) _ = SafeName(item.EntryName, item.IsDirectory);
            var parent = await ParentAsync(plan.Roots[0], outputDirectory, token).ConfigureAwait(false);
            temporary = await TemporaryAsync(parent, token).ConfigureAwait(false);
            var payload = Path.Combine(temporary, "payload"); Directory.CreateDirectory(payload); long copied = 0;
            foreach (var item in plan.Items)
            {
                token.ThrowIfCancellationRequested(); ArchiveSourcePlanner.Recheck(item, token);
                var target = Path.Combine(payload, item.EntryName.Replace('/', Path.DirectorySeparatorChar));
                if (item.IsDirectory) { SafeDirectory(payload, target, token); continue; }
                SafeDirectory(payload, Path.GetDirectoryName(target)!, token);
                await using var input = await OpenAsync(item.Path, false, token).ConfigureAwait(false);
                ArchiveSourcePlanner.Recheck(item, token);
                if (input.Length != item.Bytes) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_0857468B891B"));
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
                await CopyExactAsync(input, output, item.Bytes, token).ConfigureAwait(false);
                if (await input.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_CE84F03586EE"));
                copied += item.Bytes; progress?.Report(plan.TotalBytes == 0 ? 0.2 : 0.35 * copied / plan.TotalBytes);
            }
            var prepared = Path.Combine(temporary, "prepared.7z");
            var createArguments = new[] { "a", "-t7z", "-mx=" + level, "-md=16m", "-mmt=2", "-ms=off", "-mhc=off", "-sse", "-y", "-bd", "-bsp0", "-sccUTF-8", prepared, "." }.AsEnumerable();
            if (password is not null) createArguments = createArguments.Concat(new[] { "-p", "-mhe=" + (encryptHeaders ? "on" : "off") });
            await TextCommandAsync(createArguments.ToArray(), payload, token, prepared, password).ConfigureAwait(false);
            await using var lease = await OpenAsync(prepared, true, token).ConfigureAwait(false);
            var entries = await CatalogAsync(prepared, lease, temporary, token, password).ConfigureAwait(false);
            if (password is not null)
            {
                var encryptedHeader = SevenZipHeaderProtection.HasEncryptedHeader(await ValidateHeaderAsync(lease, token).ConfigureAwait(false));
                if (encryptedHeader != encryptHeaders || entries.Any(entry => !entry.Directory && entry.Bytes > 0 && !entry.Encrypted))
                    throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_A8F9C98DD4DE"));
            }
            var expected = plan.Items.ToDictionary(item => item.EntryName.TrimEnd('/'), StringComparer.Ordinal);
            if (entries.Count != expected.Count || entries.Any(entry => !expected.TryGetValue(entry.Name.TrimEnd('/'), out var item) || item.IsDirectory != entry.Directory || item.Bytes != entry.Bytes)) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_174B7371EB75"));
            await StreamDataAsync(prepared, entries, null, progress is null ? null : new MappedProgress(progress), token, password).ConfigureAwait(false);
            var bytes = lease.Length; await lease.DisposeAsync().ConfigureAwait(false);
            var stem = plan.Roots.Count == 1 ? ((File.GetAttributes(plan.Roots[0]) & FileAttributes.Directory) != 0 ? Path.GetFileName(plan.Roots[0]) : Path.GetFileNameWithoutExtension(plan.Roots[0])) : "Arclo";
            var published = DocumentFiles.CommitFile(prepared, parent, Short(stem) + "_archive.7z", token);
            progress?.Report(1); return new DocumentResult(published, bytes, "7Z", plan.TotalBytes);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw TimedOut(); }
        catch (Exception exception) when (Expected(exception)) { throw Explain(exception, password is not null); }
        finally { await CleanupAsync(temporary).ConfigureAwait(false); }
    }

    private async Task<SevenZipArchiveListing> InspectCoreAsync(string path, CancellationToken ct, SevenZipPassword? password = null)
    {
        using var deadline = Deadline(ct); var token = deadline.Token; string? temporary = null;
        try
        {
            var source = NormalizeSource(path); await using var lease = await OpenAsync(source, true, token).ConfigureAwait(false);
            temporary = await TemporaryAsync(Path.GetTempPath(), token).ConfigureAwait(false);
            var entries = await CatalogAsync(source, lease, temporary, token, password).ConfigureAwait(false);
            await StreamDataAsync(source, entries, null, null, token, password).ConfigureAwait(false);
            return Listing(source, entries, await FingerprintAsync(lease, token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw TimedOut(); }
        catch (Exception exception) when (Expected(exception)) { throw Explain(exception, password is not null); }
        finally { await CleanupAsync(temporary).ConfigureAwait(false); }
    }

    private async Task<DocumentResult> ExtractSelectedCoreAsync(string path, IReadOnlyList<string> names, string? outputDirectory, string expectedFingerprint, IProgress<double>? progress, CancellationToken ct, SevenZipPassword? password = null)
    {
        var selectedSnapshot = ArchiveEntrySelection.Snapshot(names);
        if (expectedFingerprint is null || expectedFingerprint.Length != 64 || !expectedFingerprint.All(char.IsAsciiHexDigit))
            throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_2906CECD6081"));
        using var deadline = Deadline(ct); var token = deadline.Token; string? temporary = null;
        try
        {
            var source = NormalizeSource(path); await using var lease = await OpenAsync(source, true, token).ConfigureAwait(false);
            var actualFingerprint = await FingerprintAsync(lease, token).ConfigureAwait(false);
            if (!actualFingerprint.Equals(expectedFingerprint, StringComparison.OrdinalIgnoreCase))
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_77BDEFF02D1A"));
            var parent = await ParentAsync(source, outputDirectory, token).ConfigureAwait(false);
            temporary = await TemporaryAsync(parent, token).ConfigureAwait(false);
            var entries = await CatalogAsync(source, lease, temporary, token, password).ConfigureAwait(false);
            var selected = ArchiveEntrySelection.Create(selectedSnapshot, entries.Select(entry => new ArchiveSelectionEntry(entry.Name, entry.Directory)).ToArray());
            var payload = Path.Combine(temporary, "payload"); Directory.CreateDirectory(payload);
            // Every native member is consumed and CRC-checked; unselected bytes never reach the filesystem.
            await StreamDataAsync(source, entries, payload, progress, token, password, selected).ConfigureAwait(false);
            var published = DocumentFiles.CommitDirectory(payload, parent, Short(Path.GetFileNameWithoutExtension(source)) + "_selected", token);
            progress?.Report(1);
            return new DocumentResult(published, entries.Where(entry => selected.Contains(entry.Name)).Sum(entry => entry.Bytes), "FOLDER", lease.Length);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw TimedOut(); }
        catch (Exception exception) when (Expected(exception)) { throw Explain(exception, password is not null); }
        finally { await CleanupAsync(temporary).ConfigureAwait(false); }
    }

    private async Task<DocumentResult> ExtractCoreAsync(string path, string? outputDirectory, IProgress<double>? progress, CancellationToken ct, SevenZipPassword? password = null)
    {
        using var deadline = Deadline(ct); var token = deadline.Token; string? temporary = null;
        try
        {
            var source = NormalizeSource(path); await using var lease = await OpenAsync(source, true, token).ConfigureAwait(false);
            var parent = await ParentAsync(source, outputDirectory, token).ConfigureAwait(false);
            temporary = await TemporaryAsync(parent, token).ConfigureAwait(false);
            var entries = await CatalogAsync(source, lease, temporary, token, password).ConfigureAwait(false);
            var payload = Path.Combine(temporary, "payload"); Directory.CreateDirectory(payload);
            await StreamDataAsync(source, entries, payload, progress, token, password).ConfigureAwait(false);
            var published = DocumentFiles.CommitDirectory(payload, parent, Short(Path.GetFileNameWithoutExtension(source)) + "_extracted", token);
            progress?.Report(1); return new DocumentResult(published, entries.Sum(entry => entry.Bytes), "FOLDER", lease.Length);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw TimedOut(); }
        catch (Exception exception) when (Expected(exception)) { throw Explain(exception, password is not null); }
        finally { await CleanupAsync(temporary).ConfigureAwait(false); }
    }

    private async Task<DocumentResult> WriteListCoreAsync(string path, string? outputDirectory, CancellationToken ct, SevenZipPassword? password = null)
    {
        using var deadline = Deadline(ct); var token = deadline.Token; string? temporary = null;
        try
        {
            var source = NormalizeSource(path); await using var lease = await OpenAsync(source, true, token).ConfigureAwait(false);
            var parent = await ParentAsync(source, outputDirectory, token).ConfigureAwait(false); temporary = await TemporaryAsync(parent, token).ConfigureAwait(false);
            var entries = await CatalogAsync(source, lease, temporary, token, password).ConfigureAwait(false);
            await StreamDataAsync(source, entries, null, null, token, password).ConfigureAwait(false);
            var text = new StringBuilder("7z: ").Append(Path.GetFileName(source)).AppendLine().Append(global::Orbit.Core.Localization.L.Get("Text_B0FE05415138")).Append(entries.Count).Append(global::Orbit.Core.Localization.L.Get("Text_3447400F0810")).Append(entries.Sum(entry => entry.Bytes)).AppendLine().AppendLine();
            foreach (var entry in entries) text.Append(entry.Directory ? global::Orbit.Core.Localization.L.Get("Text_83B5A6ECB156") : global::Orbit.Core.Localization.L.Get("Text_B2C042CAF230")).Append(entry.Name).Append(entry.Directory ? "" : "\t" + entry.Bytes.ToString(CultureInfo.InvariantCulture)).AppendLine();
            var prepared = Path.Combine(temporary, "contents.txt"); await File.WriteAllTextAsync(prepared, text.ToString(), Utf8, token).ConfigureAwait(false);
            var bytes = new FileInfo(prepared).Length; var published = DocumentFiles.CommitFile(prepared, parent, Short(Path.GetFileNameWithoutExtension(source)) + "_contents.txt", token);
            return new DocumentResult(published, bytes, "TXT", lease.Length);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw TimedOut(); }
        catch (Exception exception) when (Expected(exception)) { throw Explain(exception, password is not null); }
        finally { await CleanupAsync(temporary).ConfigureAwait(false); }
    }

    private async Task<List<Entry>> CatalogAsync(string source, FileStream lease, string temporary, CancellationToken token, SevenZipPassword? password = null)
    {
        var header = await ValidateHeaderAsync(lease, token).ConfigureAwait(false);
        var encryptedHeader = SevenZipHeaderProtection.HasEncryptedHeader(header);
        if (encryptedHeader && password is null) throw new SevenZipPasswordRequiredException();
        var common = new[] { "l", "-slt", "-ba", "-sccUTF-8", "-bd" };
        var entries = Parse(await TextCommandAsync(common.Append(source).ToArray(), null, token, password: encryptedHeader ? password : null).ConfigureAwait(false), password is not null);
        ValidateTree(entries);
        // -slt sanitizes CR/LF in names. An exact leaf filter proves the printed names really exist.
        var sorted = entries.Select(entry => entry.Name.TrimEnd('/')).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var leaves = entries.Where(entry => !entry.Directory || !HasDescendant(sorted, entry.Name.TrimEnd('/'))).ToArray();
        if (leaves.Length != 0)
        {
            var list = Path.Combine(temporary, "exact-names.txt");
            await File.WriteAllLinesAsync(list, leaves.Select(entry => entry.RawName + (entry.Directory ? "/" : "")), Utf8, token).ConfigureAwait(false);
            var filtered = Parse(await TextCommandAsync(common.Concat(new[] { "-scsUTF-8", "-ssc", "-spd", "-r-", "-i@" + list, source }).ToArray(), null, token, password: encryptedHeader ? password : null).ConfigureAwait(false), password is not null);
            if (filtered.Count != leaves.Length || !filtered.Select(Key).Order(StringComparer.Ordinal).SequenceEqual(leaves.Select(Key).Order(StringComparer.Ordinal), StringComparer.Ordinal)) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_F824ACE8C964"));
        }
        return entries;
    }
    private static string Key(Entry entry) => entry.RawName + "\0" + entry.Directory + "\0" + entry.Bytes.ToString(CultureInfo.InvariantCulture) + "\0" + entry.Crc;
    private static bool HasDescendant(string[] sorted, string name)
    {
        var prefix = name + "/"; var index = Array.BinarySearch(sorted, prefix, StringComparer.Ordinal); if (index < 0) index = ~index;
        return index < sorted.Length && sorted[index].StartsWith(prefix, StringComparison.Ordinal);
    }
    private static List<Entry> Parse(string text, bool allowEncrypted = false)
    {
        var result = new List<Entry>(); Dictionary<string, string>? row = null;
        void Finish()
        {
            if (row is null) return;
            if (result.Count >= MaximumEntries) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_04ED55E82B14"));
            string Value(string name) => row.TryGetValue(name, out var value) ? value : "";
            foreach (var property in new[] { "Symbolic Link", "Hard Link", "Copy Link", "Alternate Stream", "Deleted", "Anti", "Sparse" }) if (Value(property) is not ("" or "-")) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_8777BBA54CBF"));
            if (Value("Encrypted") is not ("-" or "+")) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_2E72DFF2E357"));
            if (!allowEncrypted && Value("Encrypted") == "+") throw new SevenZipPasswordRequiredException();
            var attributes = Value("Attributes"); var unix = attributes.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(part => part.Length == 10 && part.Skip(1).All(c => c is 'r' or 'w' or 'x' or 's' or 'S' or 't' or 'T' or '-'));
            if (attributes.Contains('L') || unix is not null && unix[0] is not ('-' or 'd')) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_F88B28128994"));
            var directory = attributes.Split(' ')[0].Contains('D') || unix?.StartsWith('d') == true || Value("Folder") == "+";
            if (!long.TryParse(Value("Size"), NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) || bytes < 0 || bytes > MaximumSingleEntryBytes || directory && bytes != 0) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_BC3D72225EA4"));
            var crcText = Value("CRC"); uint? crc = null;
            if (crcText.Length != 0) { if (crcText.Length != 8 || !uint.TryParse(crcText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_8122EC9A34D9")); crc = value; }
            if (!directory && bytes > 0 && crc is null) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_75D187788E2B"));
            var raw = Value("Path"); var name = SafeName(raw, directory);
            result.Add(new Entry(raw, name, directory, bytes, crc, Value("Encrypted") == "+")); row = null;
        }
        foreach (var line in text.Replace("\r", "").Split('\n'))
        {
            if (line.Length == 0) { Finish(); continue; }
            var separator = line.IndexOf(" = ", StringComparison.Ordinal); if (separator < 1) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_52A30C737794"));
            var key = line[..separator]; var value = line[(separator + 3)..];
            if (key == "Path") { Finish(); row = new(StringComparer.Ordinal); }
            if (row is null || !row.TryAdd(key, value)) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_FF2E3050F8F8"));
        }
        Finish(); if (result.Sum(entry => entry.Bytes) > MaximumExpandedBytes) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_618471B4175A")); return result;
    }
    private static string SafeName(string raw, bool directory)
    {
        for (int index = 0; index < raw.Length; index++)
        {
            var c = raw[index]; if (char.IsControl(c) || c is '\u2028' or '\u2029' || char.IsLowSurrogate(c) || char.IsHighSurrogate(c) && (index + 1 >= raw.Length || !char.IsLowSurrogate(raw[++index]))) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_4FC2034AA92B"));
        }
        var normalized = ArchiveEngine.NormalizeEntryName(raw, out _).TrimEnd('/');
        if (normalized.Count(c => c == '/') > 128) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_50A8EC882C12"));
        return directory ? normalized + "/" : normalized;
    }
    private static void ValidateTree(List<Entry> entries)
    {
        var names = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries) if (!names.TryAdd(entry.Name.TrimEnd('/'), entry)) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_D768D606377A"));
        foreach (var entry in entries)
        {
            var name = entry.Name.TrimEnd('/'); var slash = name.LastIndexOf('/');
            while (slash >= 0) { if (names.TryGetValue(name[..slash], out var parent) && !parent.Directory) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_CB2FB4C56876")); slash = name.LastIndexOf('/', slash - 1); }
        }
        // Adjacent full names expose ambiguous implicit parents without retaining every ancestor.
        var ordered = entries.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        for (int index = 1; index < ordered.Length; index++)
        {
            var left = ordered[index - 1].Name; var right = ordered[index].Name; int start = 0;
            while (start < left.Length && start < right.Length)
            {
                var aEnd = left.IndexOf('/', start); var bEnd = right.IndexOf('/', start); if (aEnd < 0 || bEnd < 0) break;
                var a = left.AsSpan(start, aEnd - start); var b = right.AsSpan(start, bEnd - start);
                if (!a.Equals(b, StringComparison.OrdinalIgnoreCase)) break;
                if (!a.SequenceEqual(b)) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_659D4846A6A3"));
                start = aEnd + 1;
            }
        }
    }

    private async Task StreamDataAsync(string source, List<Entry> entries, string? payload, IProgress<double>? progress, CancellationToken token, SevenZipPassword? password = null, IReadOnlySet<string>? selectedNames = null)
    {
        await using var native = await SevenZipNativeProcess.StartAsync(ExecutablePath, new[] { "x", "-so", "-bso0", "-bsp0", "-sccUTF-8", "-y", source }, null, token, password is not null).ConfigureAwait(false);
        var errors = SevenZipNativeProcess.ReadBoundedAsync(native.Error, 1024 * 1024, token);
        var copying = Task.Run(async () =>
        {
            var buffer = new byte[65536]; long completed = 0; var total = entries.Sum(entry => entry.Bytes);
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                var selected = selectedNames is null || selectedNames.Contains(entry.Name);
                if (entry.Directory) { if (payload is not null && selected) SafeDirectory(payload, Path.Combine(payload, entry.Name.Replace('/', Path.DirectorySeparatorChar)), token); continue; }
                FileStream? file = null;
                try
                {
                    if (payload is not null && selected) { var target = Path.Combine(payload, entry.Name.Replace('/', Path.DirectorySeparatorChar)); SafeDirectory(payload, Path.GetDirectoryName(target)!, token); file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous); }
                    long remaining = entry.Bytes; uint crc = uint.MaxValue;
                    while (remaining > 0)
                    {
                        var count = await native.Output.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
                        if (count == 0) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_274FF459E524"));
                        for (var index = 0; index < count; index++) crc = CrcTable[(crc ^ buffer[index]) & 255] ^ (crc >> 8);
                        if (file is not null) await file.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                        remaining -= count; completed += count; progress?.Report(total == 0 ? 0 : Math.Min(0.95, (double)completed / total));
                    }
                    if (entry.Crc.HasValue && ~crc != entry.Crc.Value) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_CC4BAE3671B3"));
                }
                finally { if (file is not null) await file.DisposeAsync().ConfigureAwait(false); }
            }
            if (await native.Output.ReadAsync(buffer.AsMemory(0, 1), token).ConfigureAwait(false) != 0) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_F50E7AB1458F"));
        }, CancellationToken.None);
        var waiting = native.WaitAsync(token);
        try
        {
            if (password is not null) await native.SendPasswordAsync(password, token).ConfigureAwait(false);
            await GuardTasksAsync(native, token, copying, errors, waiting).ConfigureAwait(false);
            if (await waiting.ConfigureAwait(false) != 0) throw NativeError(Utf8.GetString(await errors.ConfigureAwait(false)), password is not null);
        }
        catch (InvalidDataException) when (password is not null && entries.Any(entry => entry.Encrypted)) { throw new SevenZipPasswordRejectedException(); }
        finally { Observe(copying); Observe(errors); Observe(waiting); }
    }
    private async Task<string> TextCommandAsync(IReadOnlyList<string> args, string? workingDirectory, CancellationToken token, string? outputFile = null, SevenZipPassword? password = null)
    {
        await using var native = await SevenZipNativeProcess.StartAsync(ExecutablePath, args, workingDirectory, token, password is not null).ConfigureAwait(false);
        var output = SevenZipNativeProcess.ReadBoundedAsync(native.Output, 32 * 1024 * 1024, token);
        var errors = SevenZipNativeProcess.ReadBoundedAsync(native.Error, 1024 * 1024, token); var waiting = native.WaitAsync(token);
        try
        {
            if (password is not null) await native.SendPasswordAsync(password, token).ConfigureAwait(false);
            var all = Task.WhenAll(output, errors, waiting.ContinueWith(task => Array.Empty<byte>(), token));
            while (!all.IsCompleted)
            {
                token.ThrowIfCancellationRequested();
                if (output.IsFaulted || errors.IsFaulted || waiting.IsFaulted) break;
                if (outputFile is not null && File.Exists(outputFile) && new FileInfo(outputFile).Length > MaximumArchiveBytes) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_FA3D3A789873"));
                await Task.WhenAny(all, Task.Delay(50, token)).ConfigureAwait(false);
            }
            await GuardTasksAsync(native, token, output, errors, waiting).ConfigureAwait(false);
            if (await waiting.ConfigureAwait(false) != 0) throw NativeError(Utf8.GetString(await errors.ConfigureAwait(false)) + " " + Utf8.GetString(await output.ConfigureAwait(false)), password is not null);
            var text = Utf8.GetString(await output.ConfigureAwait(false));
            const string prompt = "\r\nEnter password (will not be echoed):\r\n";
            if (password is not null && text.StartsWith(prompt, StringComparison.Ordinal)) text = text[prompt.Length..];
            return text;
        }
        finally { Observe(output); Observe(errors); Observe(waiting); }
    }
    private static async Task GuardTasksAsync(SevenZipNativeProcess native, CancellationToken token, params Task[] tasks)
    {
        var pending = tasks.ToList();
        try { while (pending.Count != 0) { var completed = await Task.WhenAny(pending).WaitAsync(token).ConfigureAwait(false); await completed.ConfigureAwait(false); pending.Remove(completed); } }
        catch { await native.DisposeAsync().ConfigureAwait(false); throw; }
    }
    private static void Observe(Task task) => _ = task.ContinueWith(completed => _ = completed.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private static Exception NativeError(string text, bool suppliedPassword) => text.Contains("password", StringComparison.OrdinalIgnoreCase) || text.Contains("encrypted", StringComparison.OrdinalIgnoreCase)
        ? suppliedPassword ? new SevenZipPasswordRejectedException() : new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_7606E3E68E88"))
        : new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_C6E3B409CF25"));

    private static async Task<byte[]> ValidateHeaderAsync(FileStream input, CancellationToken token)
    {
        if (input.Length < 32 || input.Length > MaximumArchiveBytes) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_94AB6D76E71D"));
        input.Position = 0; var header = new byte[32]; await input.ReadExactlyAsync(header, token).ConfigureAwait(false);
        if (!header.AsSpan(0, 6).SequenceEqual(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }) || header[6] != 0) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_E86A2741024E"));
        if (Crc(header.AsSpan(12)) != BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8))) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_CA92FC041E4D"));
        var offset = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(12)); var size = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(20));
        if (size > 64 * 1024 * 1024 || offset > (ulong)input.Length || offset + size > (ulong)input.Length || 32UL + offset + size != (ulong)input.Length) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_0FC1DF551CD2"));
        input.Position = checked(32 + (long)offset); var nextHeader = new byte[checked((int)size)];
        await input.ReadExactlyAsync(nextHeader, token).ConfigureAwait(false);
        if (Crc(nextHeader) != BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(28))) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_452AF2F532ED"));
        return nextHeader;
    }
    private static uint Crc(ReadOnlySpan<byte> bytes) { uint crc = uint.MaxValue; foreach (var value in bytes) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8); return ~crc; }
    private static SevenZipArchiveListing Listing(string source, List<Entry> entries, string fingerprint = "") => new(source, "7Z", entries.Select(entry => new SevenZipArchiveEntry(entry.Name, entry.Directory, entry.Bytes)).ToList().AsReadOnly(), entries.Sum(entry => entry.Bytes), fingerprint);
    private static async Task<string> FingerprintAsync(FileStream source, CancellationToken token)
    { source.Position = 0; return Convert.ToHexString(await SHA256.HashDataAsync(source, token).ConfigureAwait(false)); }
    private sealed record Entry(string RawName, string Name, bool Directory, long Bytes, uint? Crc, bool Encrypted);
    private sealed class MappedProgress(IProgress<double> target) : IProgress<double> { public void Report(double value) => target.Report(0.35 + 0.6 * value); }
    private static string NormalizeSource(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path); var full = Path.GetFullPath(path);
        if (!full.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) || full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal) || full.IndexOf(':', full.Length > 1 && full[1] == ':' ? 2 : 0) >= 0) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_0E43AC64D5B8"));
        return full;
    }
    private static async Task<FileStream> OpenAsync(string path, bool archive, CancellationToken token)
    {
        var pending = Task.Run(() =>
        {
            ArchiveSourcePlanner.SafeAttributes(path, token); var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            try { ArchiveSourcePlanner.SafeAttributes(path, token); if ((File.GetAttributes(file.SafeFileHandle) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || file.Length > (archive ? MaximumArchiveBytes : MaximumSingleEntryBytes)) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_020ACF586929")); return file; } catch { file.Dispose(); throw; }
        }, CancellationToken.None);
        try { return await pending.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); }
        catch { _ = pending.ContinueWith(task => { if (task.IsCompletedSuccessfully) task.Result.Dispose(); else _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default); throw; }
    }
    private static async Task CopyExactAsync(Stream input, Stream output, long bytes, CancellationToken token)
    { var buffer = new byte[65536]; while (bytes > 0) { var count = (int)Math.Min(buffer.Length, bytes); await input.ReadExactlyAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false); await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false); bytes -= count; } }
    private static string Short(string value) { if (value.Length <= 180) return value; value = value[..180]; return char.IsHighSurrogate(value[^1]) ? value[..^1] : value; }
    private static async Task<string> ParentAsync(string source, string? requested, CancellationToken token)
    { var parent = Path.TrimEndingDirectorySeparator(string.IsNullOrWhiteSpace(requested) ? Path.GetDirectoryName(source)! : Path.GetFullPath(requested)); if (!await Task.Run(() => Directory.Exists(parent), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false)) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_F5D6DF7FEC9A")); ArchiveSourcePlanner.SafeAttributes(parent, token); return parent; }
    private static async Task<string> TemporaryAsync(string parent, CancellationToken token)
    {
        var path = Path.Combine(parent, Prefix + Guid.NewGuid().ToString("N")); var pending = Task.Run(() => Directory.CreateDirectory(path), CancellationToken.None);
        try { await pending.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); return path; }
        catch { _ = pending.ContinueWith(async task => { if (task.IsCompletedSuccessfully) await CleanupAsync(path).ConfigureAwait(false); else _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap(); throw; }
    }
    private static void SafeDirectory(string root, string path, CancellationToken token)
    {
        var relative = Path.GetRelativePath(root, path); var current = root;
        foreach (var part in relative == "." ? Array.Empty<string>() : relative.Split(Path.DirectorySeparatorChar))
        { token.ThrowIfCancellationRequested(); if (part is "." or "..") throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_1EB9C4E694CC")); current = Path.Combine(current, part); if (File.Exists(current) || Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_CD90E6F56E35")); Directory.CreateDirectory(current); if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_A1C1119A1332")); }
    }
    private static async Task CleanupAsync(string? temporary)
    {
        if (temporary is null || !Path.GetFileName(temporary).StartsWith(Prefix, StringComparison.Ordinal)) return;
        var pending = Task.Run(async () => { for (int attempt = 0; attempt < 11; attempt++) { try { if (Directory.Exists(temporary) && (File.GetAttributes(temporary) & FileAttributes.ReparsePoint) == 0) Directory.Delete(temporary, true); return; } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { } if (attempt < 10) await Task.Delay(Math.Min(300, 25 << Math.Min(attempt, 4))).ConfigureAwait(false); } });
        try { await pending.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (TimeoutException) { Observe(pending); }
    }
    private CancellationTokenSource Deadline(CancellationToken token) { var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(_timeout); return deadline; }
    private static bool Expected(Exception exception) => exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or TimeoutException or OverflowException or Win32Exception;
    private static DocumentProcessingException Explain(Exception exception, bool protectedOperation = false) => protectedOperation ? new(global::Orbit.Core.Localization.L.Get("Text_701543ACB0C4")) : new(global::Orbit.Core.Localization.L.Get("Text_43D48FC7D9BB") + exception.Message, exception);
    private static DocumentProcessingException TimedOut() => new(global::Orbit.Core.Localization.L.Get("Text_6FFA86BCDFEF"));
    private string? FindExecutable()
    {
        var name = OperatingSystem.IsWindows() ? "7zr.exe" : "7zr";
        var candidates = new[] { _configuredExecutable, Path.Combine(AppContext.BaseDirectory, "tools", "7zip", name), Environment.GetEnvironmentVariable("ORBIT_7ZIP") };
        foreach (var candidate in candidates.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)))
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try { var value = candidate.Trim('"'); if (Directory.Exists(value)) value = Path.Combine(value, name); if (File.Exists(value) && Path.GetFileName(value).Equals(name, StringComparison.OrdinalIgnoreCase)) return Path.GetFullPath(value); }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { }
        }
        return null;
    }
}
