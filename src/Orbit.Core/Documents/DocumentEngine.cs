using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Orbit.Core.Documents;

/// <summary>Local Poppler PDF operations. Input documents are never modified.</summary>
public sealed class DocumentEngine
{
    private readonly string? _configuredDirectory;
    private readonly TimeSpan _operationTimeout;
    private const int MaximumPages = 1000;
    private const int MaximumDimension = 12000;
    private const long MaximumPagePixels = 64_000_000;
    private const long MaximumTotalPixels = 1_000_000_000;

    public DocumentEngine(string? popplerDirectory = null, TimeSpan? operationTimeout = null)
    {
        _configuredDirectory = popplerDirectory;
        _operationTimeout = operationTimeout ?? TimeSpan.FromMinutes(5);
        if (_operationTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
    }

    public string? PdfInfoExecutablePath => FindTool("pdfinfo");
    public string? PdfRendererExecutablePath => FindTool("pdftoppm");
    public string? PdfTextExecutablePath => FindTool("pdftotext");

    public bool PdfToolsAvailable => FindTool("pdfinfo") is not null && FindTool("pdftoppm") is not null;
    public bool TextExtractionAvailable => FindTool("pdftotext") is not null;

    public async Task<PdfMetadata> InspectPdfAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = DocumentFiles.ExistingFile(path);
        await using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
        {
            var header = new byte[8];
            if (await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false) != header.Length
                || !header.AsSpan(0, 5).SequenceEqual("%PDF-"u8)
                || header[5] is < (byte)'0' or > (byte)'9' || header[6] != '.' || header[7] is < (byte)'0' or > (byte)'9')
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_31FC259731E0"));
        }

        var info = await RunToolAsync("pdfinfo", ["-enc", "UTF-8", fullPath], TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        var pagesMatch = Regex.Match(info, @"^Pages:\s*(\d+)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        if (!pagesMatch.Success || !int.TryParse(pagesMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pages) || pages <= 0)
            throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_85082ACB922B"));
        if (Regex.IsMatch(info, @"^Encrypted:\s*yes\b", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_0B88853F2271"));
        return new PdfMetadata(fullPath, pages, new FileInfo(fullPath).Length);
    }

    public async Task<byte[]> PdfPreviewAsync(string path, int maxSize = 640, CancellationToken cancellationToken = default)
    {
        if (maxSize is < 1 or > 2048) throw new ArgumentOutOfRangeException(nameof(maxSize), global::Orbit.Core.Localization.L.Get("Text_CDCDFCF1AC8B"));
        var source = await InspectPdfAsync(path, cancellationToken).ConfigureAwait(false);
        var parent = Path.GetTempPath();
        string? temporary = null;
        try
        {
            temporary = DocumentFiles.CreateTemporaryDirectory(parent);
            var prefix = Path.Combine(temporary, "preview");
            await RunToolAsync("pdftoppm", ["-f", "1", "-l", "1", "-singlefile", "-cropbox", "-scale-to", maxSize.ToString(CultureInfo.InvariantCulture),
                "-png", source.Path, prefix], TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            var output = prefix + ".png";
            ValidateOutputFile(output, 32 * 1024 * 1024);
            return await File.ReadAllBytesAsync(output, cancellationToken).ConfigureAwait(false);
        }
        finally { DocumentFiles.CleanupTemporaryDirectory(temporary, parent); }
    }

    public async Task<DocumentResult> ExecutePdfAsync(string path, PdfOperation operation, PdfOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
        if (options.Dpi is < 36 or > 600) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_C57346226DDC"));
        cancellationToken.ThrowIfCancellationRequested();
        var source = await InspectPdfAsync(path, cancellationToken).ConfigureAwait(false);
        if (source.Pages > MaximumPages) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Format("Text_DCFFE375AE8C",(object?)(MaximumPages)));
        if (operation == PdfOperation.ExtractText && !TextExtractionAvailable)
            throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_9CE24AE2D929"));
        var directory = DocumentFiles.OutputDirectory(source.Path, options.OutputDirectory);
        string? temporary = null;
        try
        {
            temporary = DocumentFiles.CreateTemporaryDirectory(directory);
            var stem = Path.GetFileNameWithoutExtension(source.Path);
            if (operation == PdfOperation.ExtractText)
            {
                var textOutput = Path.Combine(temporary, "text.txt");
                await RunToolAsync("pdftotext", ["-enc", "UTF-8", "-layout", source.Path, textOutput], _operationTimeout, cancellationToken).ConfigureAwait(false);
                ValidateOutputFile(textOutput, 256L * 1024 * 1024, allowEmpty: true);
                var bytes = new FileInfo(textOutput).Length;
                var target = DocumentFiles.CommitFile(textOutput, directory, stem + "_text.txt", cancellationToken);
                return new DocumentResult(target, bytes, "TXT", source.Bytes);
            }

            await ValidateRasterBoundsAsync(source, options.Dpi, cancellationToken).ConfigureAwait(false);
            var jpeg = operation == PdfOperation.JpgPages;
            // Render the visible CropBox measured by pdfinfo's Page size preflight. A hidden,
            // oversized MediaBox must not cause a larger allocation than the checked page bounds.
            var arguments = new List<string> { "-f", "1", "-l", source.Pages.ToString(CultureInfo.InvariantCulture), "-cropbox", "-r", options.Dpi.ToString(CultureInfo.InvariantCulture) };
            if (jpeg) arguments.AddRange(["-jpeg", "-jpegopt", "quality=90"]);
            else arguments.Add("-png");
            arguments.Add(source.Path);
            arguments.Add(Path.Combine(temporary, "page"));
            await RunToolAsync("pdftoppm", arguments, _operationTimeout, cancellationToken).ConfigureAwait(false);
            var extension = jpeg ? ".jpg" : ".png";
            var pages = Directory.GetFiles(temporary, "page-*" + extension);
            if (pages.Length != source.Pages) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_4F5D1C7E4015"));
            long totalBytes = 0;
            foreach (var page in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateOutputFile(page, 512L * 1024 * 1024);
                totalBytes = checked(totalBytes + new FileInfo(page).Length);
                if (totalBytes > 4L * 1024 * 1024 * 1024) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_7CC5F589E9E6"));
            }
            var folder = DocumentFiles.CommitDirectory(temporary, directory, stem + (jpeg ? "_JPG" : "_PNG"), cancellationToken);
            return new DocumentResult(folder, totalBytes, jpeg ? "JPG" : "PNG", source.Bytes);
        }
        catch (UnauthorizedAccessException exception)
        { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_78FA12B4FBB7"), exception); }
        catch (IOException exception)
        { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_370C085D8D66"), exception); }
        finally { DocumentFiles.CleanupTemporaryDirectory(temporary, directory); }
    }

    private async Task ValidateRasterBoundsAsync(PdfMetadata source, int dpi, CancellationToken cancellationToken)
    {
        var info = await RunToolAsync("pdfinfo", ["-f", "1", "-l", source.Pages.ToString(CultureInfo.InvariantCulture), "-box", source.Path],
            TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        var sizes = Regex.Matches(info, @"^Page\s+(?:\d+\s+)?size:\s*([0-9.]+)\s+x\s+([0-9.]+)\s+pts", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        if (sizes.Count != source.Pages) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_F9041CCC2CFA"));
        double totalPixels = 0;
        foreach (Match size in sizes)
        {
            var width = double.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture) / 72 * dpi;
            var height = double.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture) / 72 * dpi;
            var pixels = Math.Ceiling(width) * Math.Ceiling(height);
            totalPixels += pixels;
            if (width <= 0 || height <= 0 || width > MaximumDimension || height > MaximumDimension || pixels > MaximumPagePixels || totalPixels > MaximumTotalPixels)
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_4857B8F94EF6"));
        }
    }

    private static void ValidateOutputFile(string path, long maximumBytes, bool allowEmpty = false)
    {
        if (!File.Exists(path)) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_F8F12A9AC6C7"));
        var length = new FileInfo(path).Length;
        if ((!allowEmpty && length == 0) || length > maximumBytes) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_B60A64F721F0"));
    }

    private string? FindTool(string name)
    {
        var executable = name + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);
        var directories = new List<string?>
        {
            _configuredDirectory, Environment.GetEnvironmentVariable("ORBIT_POPPLER"),
            Path.Combine(AppContext.BaseDirectory, "tools", "poppler"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "codex-runtimes", "codex-primary-runtime", "dependencies", "native", "poppler", "Library", "bin")
        };
        directories.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator));
        foreach (var candidate in directories.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            try
            {
                var directory = candidate!.Trim('"');
                if (File.Exists(directory)) directory = Path.GetDirectoryName(directory)!;
                foreach (var suffix in new[] { string.Empty, "bin", Path.Combine("Library", "bin") })
                {
                    var path = Path.GetFullPath(Path.Combine(directory, suffix, executable));
                    if (File.Exists(path)) return path;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        return null;
    }

    private async Task<string> RunToolAsync(string tool, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executable = FindTool(tool) ?? throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Format("Text_97F795569981",(object?)(tool)));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["LC_ALL"] = "C";
        start.Environment["LANG"] = "C";
        // Keep CMaps/data attached to the selected portable Poppler build, including Library/bin layouts.
        var toolDirectory = Path.GetDirectoryName(executable)!;
        foreach (var baseDirectory in new[] { toolDirectory, Path.GetDirectoryName(toolDirectory), Path.GetDirectoryName(Path.GetDirectoryName(toolDirectory)) })
        {
            if (baseDirectory is null) continue;
            var dataDirectory = Path.Combine(baseDirectory, "share", "poppler");
            if (!Directory.Exists(dataDirectory)) continue;
            start.Environment["POPPLER_DATADIR"] = dataDirectory;
            break;
        }
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_61FB04086524"));
        }
        catch (System.ComponentModel.Win32Exception exception)
        { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_13809BC3747A"), exception); }
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);
        using var killRegistration = linked.Token.Register(() => Kill(process));
        var stdout = ReadBoundedAsync(process.StandardOutput, 2 * 1024 * 1024, linked.Token, process);
        var stderr = ReadBoundedAsync(process.StandardError, 128 * 1024, linked.Token, process);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                if (error.Contains("password", StringComparison.OrdinalIgnoreCase) || error.Contains("encrypted", StringComparison.OrdinalIgnoreCase) || process.ExitCode == 3)
                    throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_97980CB24063"));
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_714CD82740AC") +
                    (string.IsNullOrWhiteSpace(error) ? string.Empty : " " + error.Trim()[..Math.Min(error.Trim().Length, 600)]));
            }
            return output;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            Kill(process);
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_142D43950111"));
        }
        finally
        {
            Kill(process);
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception exception) when (exception is InvalidOperationException or TimeoutException) { }
            // Observe both reader tasks even after cancellation or one overflowing its output bound.
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or DocumentProcessingException) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken cancellationToken, Process process)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > limit)
            {
                Kill(process);
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_FAFC4708B264"));
            }
            output.Append(buffer, 0, count);
        }
        return output.ToString();
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }
}
