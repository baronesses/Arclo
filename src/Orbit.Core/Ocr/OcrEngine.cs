using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Orbit.Core.Documents;

namespace Orbit.Core.Ocr;

/// <summary>Offline OCR. Images are decoded locally; PDFs are rendered one page at a time.</summary>
public sealed class OcrEngine
{
    public const int MaximumPdfPages = 100;
    public const long MaximumSourceBytes = 256L * 1024 * 1024;
    public const long MaximumPagePixels = 40_000_000;
    public const long MaximumTotalPixels = 500_000_000;
    private const int MaximumTextCharacters = 16 * 1024 * 1024;
    private readonly string? _configuredTesseract;
    private readonly string? _configuredPoppler;
    private readonly ImageMagickEngine _images;
    private readonly TimeSpan _operationTimeout;

    public OcrEngine(string? tesseractDirectory = null, string? popplerDirectory = null,
        string? imageMagickExecutable = null, TimeSpan? operationTimeout = null)
    {
        _configuredTesseract = tesseractDirectory;
        _configuredPoppler = popplerDirectory;
        _images = new ImageMagickEngine(imageMagickExecutable);
        _operationTimeout = operationTimeout ?? TimeSpan.FromMinutes(15);
        if (_operationTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
    }

    public string? ExecutablePath => FindTesseract();
    public string PdfInfoExecutablePath => RequiredPoppler("pdfinfo");
    public string PdfRendererExecutablePath => RequiredPoppler("pdftoppm");

    public bool Available => FindTesseract() is not null;

    public async Task<DocumentResult> ExtractImageAsync(string path, OcrOptions options,
        CancellationToken cancellationToken = default)
    {
        var language = ValidateOptions(options);
        var fullPath = ValidateSource(path, cancellationToken);
        var source = await _images.InspectAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if (source.Frames != 1) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_0C3039390ABD"));
        if ((long)source.Width * source.Height > MaximumPagePixels || source.Width > 16000 || source.Height > 16000)
            throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_FD610A0AA199"));
        var executable = RequiredTesseract(language);
        var directory = DocumentFiles.OutputDirectory(fullPath, options.OutputDirectory);
        string? temporary = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_operationTimeout);
        try
        {
            temporary = DocumentFiles.CreateTemporaryDirectory(directory);
            var png = Path.Combine(temporary, "page.png");
            await RunAsync(_images.ExecutablePath, ["-limit", "thread", "2", "-limit", "memory", "512MiB", "-limit", "map", "0", "-limit", "disk", "0",
                "-define", "filename:literal=true", source.Format + ":" + fullPath, "-auto-orient", "-background", "white", "-alpha", "remove", "-alpha", "off",
                "-colorspace", "Gray", "-depth", "8", "PNG:" + png], TimeSpan.FromMinutes(2), timeout.Token).ConfigureAwait(false);
            ValidateRaster(png);
            var text = await RecognizeAsync(executable, png, language, options.Dpi, timeout.Token).ConfigureAwait(false);
            return await SaveTextAsync(fullPath, source.Bytes, text, temporary, directory, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_FE540BF6781D")); }
        catch (UnauthorizedAccessException exception)
        { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_42BE267F076B"), exception); }
        catch (IOException exception)
        { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_9B476F0FED84"), exception); }
        finally { await CleanupTemporaryDirectoryAsync(temporary, directory).ConfigureAwait(false); }
    }

    public async Task<DocumentResult> ExtractPdfAsync(string path, OcrOptions options,
        CancellationToken cancellationToken = default)
    {
        var language = ValidateOptions(options);
        var fullPath = ValidateSource(path, cancellationToken);
        var source = await new DocumentEngine(_configuredPoppler).InspectPdfAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if (source.Pages > MaximumPdfPages)
            throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Format("Text_C271AD3EBF24",(object?)(MaximumPdfPages)));
        var executable = RequiredTesseract(language);
        var pdfinfo = RequiredPoppler("pdfinfo");
        var renderer = RequiredPoppler("pdftoppm");
        var directory = DocumentFiles.OutputDirectory(fullPath, options.OutputDirectory);
        string? temporary = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_operationTimeout);
        try
        {
            var info = await RunAsync(pdfinfo, ["-f", "1", "-l", source.Pages.ToString(CultureInfo.InvariantCulture), "-box", fullPath],
                TimeSpan.FromSeconds(30), timeout.Token).ConfigureAwait(false);
            ValidatePdfDimensions(info, source.Pages, options.Dpi);
            temporary = DocumentFiles.CreateTemporaryDirectory(directory);
            var text = new StringBuilder();
            for (var page = 1; page <= source.Pages; page++)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var prefix = Path.Combine(temporary, "page");
                var png = prefix + ".png";
                var number = page.ToString(CultureInfo.InvariantCulture);
                // pdfinfo's page size describes the visible CropBox. Render the same box so that a
                // maliciously huge hidden MediaBox cannot bypass the preflight pixel limits.
                await RunAsync(renderer, ["-f", number, "-l", number, "-singlefile", "-cropbox", "-r", options.Dpi.ToString(CultureInfo.InvariantCulture),
                    "-png", fullPath, prefix], TimeSpan.FromMinutes(2), timeout.Token).ConfigureAwait(false);
                ValidateRaster(png);
                var pageText = await RecognizeAsync(executable, png, language, options.Dpi, timeout.Token).ConfigureAwait(false);
                File.Delete(png);
                text.Append(global::Orbit.Core.Localization.L.Get("Text_2A98768A3B59")).Append(number).AppendLine(" ---").Append(pageText.TrimEnd()).AppendLine().AppendLine();
                if (text.Length > MaximumTextCharacters) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_B42693F017D0"));
            }
            return await SaveTextAsync(fullPath, source.Bytes, text.ToString(), temporary, directory, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_A5BB1E526192")); }
        catch (UnauthorizedAccessException exception)
        { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_42BE267F076B"), exception); }
        catch (IOException exception)
        { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_9B476F0FED84"), exception); }
        finally { await CleanupTemporaryDirectoryAsync(temporary, directory).ConfigureAwait(false); }
    }

    private static async Task<DocumentResult> SaveTextAsync(string source, long sourceBytes, string text, string temporary,
        string directory, CancellationToken cancellationToken)
    {
        var output = Path.Combine(temporary, "text.txt");
        // An empty TXT is a valid result for a blank page. No synthetic text is added to an image result.
        await File.WriteAllTextAsync(output, text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
        var bytes = new FileInfo(output).Length;
        var committed = DocumentFiles.CommitFile(output, directory, Path.GetFileNameWithoutExtension(source) + "_ocr.txt", cancellationToken);
        return new DocumentResult(committed, bytes, "TXT", sourceBytes);
    }

    private static async Task CleanupTemporaryDirectoryAsync(string? temporary, string directory)
    {
        if (temporary is null) return;
        // A terminated native writer or a virus scanner can briefly retain a Windows file handle.
        // Retry only our generated child directory, independently of the cancelled operation token.
        // The shared helper checks the exact parent/prefix and refuses a reparse-point directory.
        for (var attempt = 0; attempt < 11; attempt++)
        {
            DocumentFiles.CleanupTemporaryDirectory(temporary, directory);
            if (!Directory.Exists(temporary)) return;
            if (attempt < 10) await Task.Delay(Math.Min(300, 25 << Math.Min(attempt, 4))).ConfigureAwait(false);
        }
    }

    private static string ValidateOptions(OcrOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Dpi is < 120 or > 300) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_A3F8672FB8DC"));
        var tokens = (options.Language ?? string.Empty).Trim().ToLowerInvariant().Split('+');
        if (tokens.Length is < 1 or > 2 || tokens.Any(value => value is not ("eng" or "rus")) || tokens.Distinct().Count() != tokens.Length)
            throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_36DF0C1F1605"));
        return string.Join('+', tokens);
    }

    private static string ValidateSource(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = DocumentFiles.ExistingFile(path);
        if (new FileInfo(fullPath).Length > MaximumSourceBytes)
            throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_9223CE1E0520"));
        return fullPath;
    }

    private static void ValidateRaster(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > 192L * 1024 * 1024)
            throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_A761B000C950"));
    }

    private static void ValidatePdfDimensions(string info, int pages, int dpi)
    {
        var matches = Regex.Matches(info, @"^Page\s+(?:\d+\s+)?size:\s*([0-9.]+)\s+x\s+([0-9.]+)\s+pts", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        if (matches.Count != pages) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_C97C00FE7E03"));
        double total = 0;
        foreach (Match match in matches)
        {
            var width = Math.Ceiling(double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * dpi / 72);
            var height = Math.Ceiling(double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * dpi / 72);
            total += width * height;
            if (width <= 0 || height <= 0 || width > 16000 || height > 16000 || width * height > MaximumPagePixels || total > MaximumTotalPixels)
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_0D734C09E7C9"));
        }
    }

    private Task<string> RecognizeAsync(string executable, string input, string language, int dpi, CancellationToken cancellationToken)
        // Tesseract's Windows argv handling can corrupt Unicode filenames. Pipe only the locally
        // decoded PNG and use a relative model directory under its Unicode-safe working directory.
        => RunAsync(executable, ["stdin", "stdout", "--tessdata-dir", "tessdata",
            "-l", language, "--oem", "1", "--psm", "3", "--dpi", dpi.ToString(CultureInfo.InvariantCulture)], TimeSpan.FromMinutes(2), cancellationToken, input);

    private string RequiredTesseract(string language)
    {
        var executable = FindTesseract() ?? throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_14078F207E23"));
        foreach (var value in language.Split('+'))
            if (!File.Exists(Path.Combine(Path.GetDirectoryName(executable)!, "tessdata", value + ".traineddata")))
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Format("Text_2B8F28104DB4",(object?)(value)));
        return executable;
    }

    private string? FindTesseract() => FindExecutable("tesseract", [_configuredTesseract,
        Path.Combine(AppContext.BaseDirectory, "tools", "tesseract"), Environment.GetEnvironmentVariable("ORBIT_TESSERACT"), @"C:\Program Files\Tesseract-OCR"]);

    private string RequiredPoppler(string name) => FindExecutable(name, [_configuredPoppler, Environment.GetEnvironmentVariable("ORBIT_POPPLER"),
        Path.Combine(AppContext.BaseDirectory, "tools", "poppler"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "codex-runtimes", "codex-primary-runtime", "dependencies", "native", "poppler", "Library", "bin")])
        ?? throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_4371E95FEF3B"));

    private static string? FindExecutable(string name, IEnumerable<string?> candidates)
    {
        var file = name + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);
        foreach (var candidate in candidates.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)))
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try
            {
                var directory = candidate.Trim('"');
                if (File.Exists(directory))
                {
                    if (string.Equals(Path.GetFileName(directory), file, StringComparison.OrdinalIgnoreCase)) return Path.GetFullPath(directory);
                    continue;
                }
                foreach (var suffix in new[] { "", "bin", Path.Combine("Library", "bin") })
                {
                    var path = Path.GetFullPath(Path.Combine(directory, suffix, file));
                    if (File.Exists(path)) return path;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { }
        }
        return null;
    }

    private static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken,
        string? inputFile = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = inputFile is not null,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["OMP_THREAD_LIMIT"] = "2";
        start.Environment["LC_ALL"] = "C";
        start.Environment["LANG"] = "C";
        using var process = new Process { StartInfo = start };
        try { if (!process.Start()) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_E2C66C7F84D6")); }
        catch (System.ComponentModel.Win32Exception exception)
        { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_5DC16144867A"), exception); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var output = ReadBoundedAsync(process.StandardOutput, MaximumTextCharacters, deadline.Token);
        var error = ReadBoundedAsync(process.StandardError, 65536, deadline.Token);
        var input = inputFile is null ? Task.CompletedTask : FeedInputAsync(process, inputFile, deadline.Token);
        // A reader failure must also interrupt a native writer blocked on a full pipe.
        _ = output.ContinueWith(_ => deadline.Cancel(), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        _ = error.ContinueWith(_ => deadline.Cancel(), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        _ = input.ContinueWith(_ => deadline.Cancel(), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await input.ConfigureAwait(false);
            var text = await output.ConfigureAwait(false);
            var diagnostics = await error.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_3B186BF7FA6A") + diagnostics.Trim()[..Math.Min(diagnostics.Trim().Length, 1200)]);
            return text;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (input.IsFaulted) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_FF2912248A32"));
            if (output.IsFaulted || error.IsFaulted) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_E0731FC0D136"));
            throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_D4549A6004C5"));
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
                try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch (InvalidOperationException) { }
            }
            try { await Task.WhenAll(output, error, input).ConfigureAwait(false); } catch { }
        }
    }

    private static async Task FeedInputAsync(Process process, string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
            await source.CopyToAsync(process.StandardInput.BaseStream, cancellationToken).ConfigureAwait(false);
        }
        finally { process.StandardInput.Close(); }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximum, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (result.Length + count > maximum) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_E0731FC0D136"));
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}
