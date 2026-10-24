using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Orbit.Core.Documents;

/// <summary>Content-preserving page assembly through local qpdf. No rendering, cloud access, or source replacement.</summary>
public sealed class PdfAssemblyEngine
{
    public const int MaximumPages = 1000;
    public const long MaximumSourceBytes = 512L * 1024 * 1024;
    private const long MaximumOutputBytes = 1024L * 1024 * 1024;
    private readonly string? _configuredExecutable;
    private readonly TimeSpan _operationTimeout;
    private readonly DocumentEngine _documents = new();

    public PdfAssemblyEngine(string? executablePath = null, TimeSpan? operationTimeout = null)
    {
        _configuredExecutable = executablePath;
        _operationTimeout = operationTimeout ?? TimeSpan.FromMinutes(5);
        if (_operationTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
    }

    public bool Available => FindTool() is not null;
    public string ExecutablePath => FindTool() ?? throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_8303134AEE91"));

    public async Task<DocumentResult> MergeAsync(IReadOnlyList<string> paths, string? outputDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        cancellationToken.ThrowIfCancellationRequested();
        if (paths.Count is < 2 or > MaximumPages)
            throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Format("Text_5FBCB759F93E",(object?)(MaximumPages)));
        var sources = new List<PdfMetadata>();
        var planned = new List<(string Path, long Bytes)>();
        var inspected = new Dictionary<string, PdfMetadata>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        int totalPages = 0;
        var requested = paths.ToArray();
        await Task.Run(() =>
        {
            foreach (var path in requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var input = ValidateSourcePath(path);
                totalBytes = checked(totalBytes + input.Bytes);
                if (totalBytes > MaximumSourceBytes) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_513145F3B6A2"));
                planned.Add(input);
            }
        }, cancellationToken).ConfigureAwait(false);
        foreach (var input in planned)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!inspected.TryGetValue(input.Path, out var source))
            {
                source = await InspectSourceAsync(input.Path, cancellationToken).ConfigureAwait(false);
                inspected.Add(input.Path, source);
            }
            if (source.Bytes != input.Bytes) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_0382F7A2660A"));
            totalPages = checked(totalPages + source.Pages);
            if (totalPages > MaximumPages) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Format("Text_2DD9B135A6C2",(object?)(MaximumPages)));
            sources.Add(source);
        }
        return await AssembleAsync(sources, sources.Select(source => new PageSource(source.Path, "1-z")).ToArray(),
            totalPages, totalBytes, outputDirectory, "Arclo_merged.pdf", cancellationToken).ConfigureAwait(false);
    }

    public async Task<DocumentResult> ExtractPagesAsync(string path, string selection, string? outputDirectory = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = await InspectSourceAsync(path, cancellationToken).ConfigureAwait(false);
        var pages = ParsePageSelection(selection, source.Pages);
        var normalized = string.Join(",", pages.Select(page => page.ToString(CultureInfo.InvariantCulture)));
        return await AssembleAsync([source], [new PageSource(source.Path, normalized)], pages.Count, source.Bytes,
            outputDirectory, Path.GetFileNameWithoutExtension(source.Path) + "_pages.pdf", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One-based pages, inclusive ranges, explicit segment order and duplicates are preserved.</summary>
    public static IReadOnlyList<int> ParsePageSelection(string selection, int pageCount)
    {
        if (pageCount is < 1 or > MaximumPages) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Format("Text_C855BCF78596",(object?)(MaximumPages)));
        if (string.IsNullOrWhiteSpace(selection) || selection.Length > 10000)
            throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_1790348AA259"));
        var pages = new List<int>();
        foreach (var segment in selection.Split(','))
        {
            var ends = segment.Trim().Split('-');
            if (ends.Length is < 1 or > 2 || !TryPage(ends[0], pageCount, out var start) ||
                (ends.Length == 2 && !TryPage(ends[1], pageCount, out _)))
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Format("Text_AEF8619E5C53",(object?)(segment.Trim()),(object?)(pageCount)));
            var end = ends.Length == 2 ? int.Parse(ends[1].Trim(), CultureInfo.InvariantCulture) : start;
            var count = Math.Abs(end - start) + 1;
            if (pages.Count + count > MaximumPages) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Format("Text_16A80E151C02",(object?)(MaximumPages)));
            var step = end >= start ? 1 : -1;
            for (var page = start; ; page += step) { pages.Add(page); if (page == end) break; }
        }
        return pages.AsReadOnly();
    }

    private static bool TryPage(string text, int count, out int page) => int.TryParse(text.Trim(), NumberStyles.None,
        CultureInfo.InvariantCulture, out page) && page >= 1 && page <= count;

    private async Task<PdfMetadata> InspectSourceAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = ValidateSourcePath(path);
        var fullPath = input.Path;
        // This status command detects even encrypted PDFs whose password is unknown or empty.
        var encryption = await RunAsync(["--is-encrypted", fullPath], TimeSpan.FromSeconds(30), cancellationToken, encryptionStatus: true).ConfigureAwait(false);
        if (encryption.ExitCode == 0) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_272548EE5647"));
        var metadata = await _documents.InspectPdfAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if (metadata.Pages > MaximumPages) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Format("Text_104C0070C56E",(object?)(MaximumPages)));
        return metadata;
    }

    private static (string Path, long Bytes) ValidateSourcePath(string path)
    {
        var fullPath = DocumentFiles.ExistingFile(path);
        if (fullPath.Length > 4096) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_D543C447B937"));
        var length = new FileInfo(fullPath).Length;
        if (length <= 0 || length > MaximumSourceBytes) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_826EF4299A19"));
        return (fullPath, length);
    }

    private async Task<DocumentResult> AssembleAsync(IReadOnlyList<PdfMetadata> sources, IReadOnlyList<PageSource> pageSources,
        int expectedPages, long sourceBytes, string? outputDirectory, string outputName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parent = DocumentFiles.OutputDirectory(sources[0].Path, outputDirectory);
        string? temporary = null;
        try
        {
            // All inputs and requested pages are validated before creating even the private output directory.
            temporary = DocumentFiles.CreateTemporaryDirectory(parent);
            var resultPath = Path.Combine(temporary, "result.pdf");
            var jobPath = Path.Combine(temporary, "job.json");
            var job = new
            {
                inputFile = sources[0].Path,
                outputFile = resultPath,
                keepFilesOpen = "n",
                pages = pageSources.Select(source => new { file = source.Path, range = source.Range }).ToArray()
            };
            var jobBytes = JsonSerializer.SerializeToUtf8Bytes(job);
            if (jobBytes.Length > 16 * 1024 * 1024) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_B2C946A9B226"));
            await File.WriteAllBytesAsync(jobPath, jobBytes, cancellationToken).ConfigureAwait(false);
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(source.Path) || new FileInfo(source.Path).Length != source.Bytes)
                    throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_AFEBE7007524"));
            }
            await RunAsync(["--job-json-file=" + jobPath], _operationTimeout, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(resultPath) || new FileInfo(resultPath).Length is <= 0 or > MaximumOutputBytes)
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_28A93228B090"));
            await RunAsync(["--check", resultPath], _operationTimeout, cancellationToken).ConfigureAwait(false);
            var output = await _documents.InspectPdfAsync(resultPath, cancellationToken).ConfigureAwait(false);
            if (output.Pages != expectedPages) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_D62FABAD5EF2"));
            cancellationToken.ThrowIfCancellationRequested();
            var target = DocumentFiles.CommitFile(resultPath, parent, outputName, cancellationToken);
            return new DocumentResult(target, output.Bytes, "PDF", sourceBytes);
        }
        catch (UnauthorizedAccessException exception) { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_27154E4A7AD7"), exception); }
        catch (IOException exception) { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_795CD04A1D5C"), exception); }
        finally { DocumentFiles.CleanupTemporaryDirectory(temporary, parent); }
    }

    private string? FindTool()
    {
        var executable = OperatingSystem.IsWindows() ? "qpdf.exe" : "qpdf";
        var locations = new List<string?>
        {
            _configuredExecutable, Path.Combine(AppContext.BaseDirectory, "tools", "qpdf"), Environment.GetEnvironmentVariable("ORBIT_QPDF")
        };
        locations.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        foreach (var location in locations.Where(location => !string.IsNullOrWhiteSpace(location)))
        {
            try
            {
                var path = location!.Trim('"');
                if (File.Exists(path)) return Path.GetFullPath(path);
                foreach (var suffix in new[] { executable, Path.Combine("bin", executable) })
                {
                    var candidate = Path.GetFullPath(Path.Combine(path, suffix));
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        return null;
    }

    private async Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout,
        CancellationToken cancellationToken, bool encryptionStatus = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        try { if (!process.Start()) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_7A595387E303")); }
        catch (System.ComponentModel.Win32Exception exception) { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_FBDB1EF61597"), exception); }
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        using var registration = linked.Token.Register(() => Kill(process));
        var stdout = ReadBoundedAsync(process.StandardOutput, 2 * 1024 * 1024, linked.Token, process);
        var stderr = ReadBoundedAsync(process.StandardError, 128 * 1024, linked.Token, process);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (encryptionStatus && process.ExitCode == 0) return new ProcessResult(0, output);
            if (encryptionStatus && process.ExitCode == 2 && string.IsNullOrWhiteSpace(error)) return new ProcessResult(2, output);
            if (process.ExitCode != 0)
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_1099E8F293F9") +
                    (string.IsNullOrWhiteSpace(error) ? "" : " " + error.Trim()[..Math.Min(error.Trim().Length, 800)]));
            return new ProcessResult(process.ExitCode, output);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            Kill(process);
            cancellationToken.ThrowIfCancellationRequested();
            throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_142D43950111"));
        }
        finally
        {
            Kill(process);
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception exception) when (exception is InvalidOperationException or TimeoutException) { }
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or DocumentProcessingException) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken cancellationToken, Process process)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > limit) { Kill(process); throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_B0ED285E9EB4")); }
            output.Append(buffer, 0, count);
        }
        return output.ToString();
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private sealed record PageSource(string Path, string Range);
    private sealed record ProcessResult(int ExitCode, string Output);
}
