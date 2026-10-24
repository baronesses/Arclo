using System.IO.Compression;
using System.Text;

namespace Orbit.Core.Documents;

/// <summary>Creates ZIPs from files and folders and extracts into a new, atomically published folder.</summary>
public sealed class ArchiveEngine
{
    public const int MaximumEntries = 10000;
    public const long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024;
    public const long MaximumSingleEntryBytes = 1024L * 1024 * 1024;

    public async Task<DocumentResult> CreateZipAsync(IReadOnlyList<string> paths, string? outputDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        cancellationToken.ThrowIfCancellationRequested();
        // Complete the bounded snapshot off the UI thread and before any temporary/output file is created.
        var plan = await Task.Run(() => ArchiveSourcePlanner.Create(paths, cancellationToken), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var parent = DocumentFiles.OutputDirectory(plan.Roots[0], outputDirectory);
        string? temporary = null;
        try
        {
            temporary = DocumentFiles.CreateTemporaryDirectory(parent);
            var zipPath = Path.Combine(temporary, "archive.zip");
            await using (var output = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                long copied = 0;
                foreach (var source in plan.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ArchiveSourcePlanner.Recheck(source, cancellationToken);
                    if (source.IsDirectory)
                    {
                        var directoryEntry = archive.CreateEntry(source.EntryName);
                        directoryEntry.ExternalAttributes = (0x41ED << 16) | (int)FileAttributes.Directory;
                        var directoryModified = Directory.GetLastWriteTime(source.Path);
                        if (directoryModified.Year is >= 1980 and <= 2107) directoryEntry.LastWriteTime = directoryModified;
                        continue;
                    }
                    await using var input = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                    ArchiveSourcePlanner.Recheck(source, cancellationToken);
                    if ((File.GetAttributes(input.SafeFileHandle) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                        throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_6747D0F846D4"));
                    if (input.Length != source.Bytes)
                        throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_502033B43D28"));
                    var entry = archive.CreateEntry(source.EntryName, CompressionLevel.Optimal);
                    var modified = File.GetLastWriteTime(source.Path);
                    if (modified.Year is >= 1980 and <= 2107) entry.LastWriteTime = modified;
                    await using var destination = entry.Open();
                    var entryBytes = await CopyBoundedAsync(input, destination, Math.Min(source.Bytes, MaximumSingleEntryBytes), cancellationToken).ConfigureAwait(false);
                    if (entryBytes != source.Bytes) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_17750BF28EC7"));
                    copied += entryBytes;
                    if (copied > MaximumExpandedBytes) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_D0A4F7DC7E15"));
                }
                if (copied != plan.TotalBytes) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_1C3A1D0273FC"));
            }
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = new FileInfo(zipPath).Length;
            var rootName = plan.Items[0].IsDirectory ? Path.GetFileName(plan.Roots[0]) : Path.GetFileNameWithoutExtension(plan.Roots[0]);
            var name = plan.Roots.Count == 1 ? rootName + "_archive.zip" : "Arclo_archive.zip";
            var target = DocumentFiles.CommitFile(zipPath, parent, name, cancellationToken);
            return new DocumentResult(target, bytes, "ZIP", plan.TotalBytes);
        }
        catch (UnauthorizedAccessException exception)
        { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_4673BCEEF860"), exception); }
        catch (InvalidDataException exception)
        { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_7C13418ABF98") + exception.Message, exception); }
        catch (IOException exception)
        { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_7F1AF320A6F7"), exception); }
        finally { DocumentFiles.CleanupTemporaryDirectory(temporary, parent); }
    }

    public async Task<ZipMetadata> InspectZipAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = DocumentFiles.ExistingFile(path);
        try
        {
            await using var stream = await OpenZipAsync(fullPath, cancellationToken).ConfigureAwait(false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var plan = ValidateEntries(archive, cancellationToken);
            return new ZipMetadata(fullPath, plan.Count, stream.Length, plan.Sum(item => item.IsDirectory ? 0L : item.Entry.Length));
        }
        catch (InvalidDataException exception)
        { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_0D596C3AB016") + exception.Message, exception); }
    }

    public async Task<DocumentResult> ExtractZipAsync(string path, string? outputDirectory = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = DocumentFiles.ExistingFile(path);
        var sourceBytes = new FileInfo(source).Length;
        var parent = DocumentFiles.OutputDirectory(source, outputDirectory);
        string? temporary = null;
        try
        {
            // Validate every central-directory name before creating even the private extraction folder.
            await using var input = await OpenZipAsync(source, cancellationToken).ConfigureAwait(false);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            var plan = ValidateEntries(archive, cancellationToken);
            temporary = DocumentFiles.CreateTemporaryDirectory(parent);
            var root = Path.GetFullPath(temporary) + Path.DirectorySeparatorChar;
            long totalBytes = 0;
            foreach (var item in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destinationPath = Path.GetFullPath(Path.Combine(temporary, item.RelativePath));
                if (!destinationPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_8218D24AF345"));
                if (item.IsDirectory)
                {
                    Directory.CreateDirectory(destinationPath);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                await using var entryStream = item.Entry.Open();
                await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var copied = await CopyBoundedAsync(entryStream, destination, Math.Min(item.Entry.Length, MaximumSingleEntryBytes), cancellationToken).ConfigureAwait(false);
                if (copied != item.Entry.Length) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_305181D90C43"));
                totalBytes = checked(totalBytes + copied);
                if (totalBytes > MaximumExpandedBytes) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_D046D9BB749B"));
            }
            cancellationToken.ThrowIfCancellationRequested();
            var folder = DocumentFiles.CommitDirectory(temporary, parent, Path.GetFileNameWithoutExtension(source) + "_extracted", cancellationToken);
            return new DocumentResult(folder, totalBytes, "FOLDER", sourceBytes);
        }
        catch (InvalidDataException exception)
        { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_E099E3C452B1") + exception.Message, exception); }
        catch (UnauthorizedAccessException exception)
        { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_A2D84DFD929D"), exception); }
        catch (IOException exception)
        { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_04344915223C"), exception); }
        finally { DocumentFiles.CleanupTemporaryDirectory(temporary, parent); }
    }

    private static async Task<FileStream> OpenZipAsync(string path, CancellationToken cancellationToken)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        try
        {
            var signature = new byte[4];
            if (await stream.ReadAsync(signature, cancellationToken).ConfigureAwait(false) != signature.Length
                || signature[0] != 'P' || signature[1] != 'K'
                || !((signature[2] == 3 && signature[3] == 4) || (signature[2] == 5 && signature[3] == 6)))
                throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_82A149C60CDE"));
            stream.Position = 0;
            return stream;
        }
        catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private static List<ExtractionEntry> ValidateEntries(ZipArchive archive, CancellationToken cancellationToken)
    {
        if (archive.Entries.Count > MaximumEntries) throw new InvalidDataException(global::Orbit.Core.Localization.L.Format("Text_8249CEE0DC4D",(object?)(MaximumEntries)));
        var plan = new List<ExtractionEntry>();
        var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_E9B7AF9882A1"));
            var normalized = NormalizeEntryName(entry.FullName, out var directorySuffix);
            var isDirectory = directorySuffix || unixType == 0x4000 || (entry.ExternalAttributes & (int)FileAttributes.Directory) != 0;
            if (!paths.TryAdd(normalized, isDirectory)) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_9C26FE297E72"));
            if (isDirectory && entry.Length != 0) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_B36ADA217D36"));
            if (entry.Length < 0 || entry.Length > MaximumSingleEntryBytes || (expanded = checked(expanded + entry.Length)) > MaximumExpandedBytes)
                throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_196577475AE9"));
            // Large, extreme-ratio entries are rejected before opening a decompression stream.
            if (entry.Length > 64L * 1024 * 1024 && entry.Length / Math.Max(1d, entry.CompressedLength) > 2000)
                throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_052D3F695202"));
            plan.Add(new ExtractionEntry(entry, normalized.Replace('/', Path.DirectorySeparatorChar), isDirectory));
        }
        foreach (var path in paths.Keys)
        {
            var slash = path.LastIndexOf('/');
            while (slash >= 0)
            {
                var ancestor = path[..slash];
                if (paths.TryGetValue(ancestor, out var isDirectory) && !isDirectory)
                    throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_8BFD79676995"));
                slash = ancestor.LastIndexOf('/');
            }
        }
        return plan;
    }

    internal static string NormalizeEntryName(string name, out bool directory)
    {
        var normalized = name.Replace('\\', '/').Normalize(NormalizationForm.FormC);
        directory = normalized.EndsWith('/');
        if (directory) normalized = normalized[..^1];
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 2000 || normalized.StartsWith('/') || normalized.Contains(':'))
            throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_9C58719822DC"));
        foreach (var segment in normalized.Split('/'))
        {
            if (segment.Length is 0 or > 240 || segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.')
                || segment.Any(character => character < 32 || "<>:\"|?*".Contains(character)))
                throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_8F3B7A5B372B"));
            var stem = segment.Split('.')[0].TrimEnd(' ');
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                    && (stem[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3')))
                throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_14279234AC8C"));
        }
        return normalized;
    }

    private static async Task<long> CopyBoundedAsync(Stream input, Stream output, long maximumBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long copied = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            copied += count;
            if (copied > maximumBytes) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_C829F1FD51F9"));
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
        return copied;
    }

    private sealed record ExtractionEntry(ZipArchiveEntry Entry, string RelativePath, bool IsDirectory);
}
