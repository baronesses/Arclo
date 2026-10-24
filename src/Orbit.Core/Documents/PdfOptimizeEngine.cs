using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Orbit.Core.Documents;

/// <summary>Lossless qpdf stream/object optimization into a new, independently validated PDF.
/// Images are neither resampled nor JPEG-reencoded; a smaller output is not guaranteed.</summary>
public sealed class PdfOptimizeEngine
{
    public const long MaximumSourceBytes = 512L * 1024 * 1024;
    public const long MaximumOutputBytes = 1024L * 1024 * 1024;
    public const int MaximumPages = 1000;
    private readonly PdfAssemblyEngine _qpdf;
    private readonly string? _popplerDirectory;
    private readonly TimeSpan _timeout;
    internal Action<string>? NativeStarted { get; set; }

    public PdfOptimizeEngine(string? qpdfExecutable = null, TimeSpan? operationTimeout = null, string? popplerDirectory = null)
    {
        _timeout = operationTimeout ?? TimeSpan.FromMinutes(5);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromHours(24)) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        _qpdf = new PdfAssemblyEngine(qpdfExecutable, _timeout); _popplerDirectory = popplerDirectory;
    }

    public string? PdfInfoExecutablePath => FindPdfInfo();
    public bool Available => _qpdf.Available && FindPdfInfo() is not null;
    public string ExecutablePath => _qpdf.ExecutablePath;

    public async Task<DocumentResult> OptimizeAsync(string path, PdfOptimizeOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options); ct.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(_timeout);
        var token = deadline.Token; string? temporary = null, parent = null;
        try
        {
            var source = NormalizePath(path); await using var lease = await OpenInputAsync(source, token).ConfigureAwait(false);
            var encryption = await RunAsync(ExecutablePath, ["--is-encrypted", source], token, encryptionStatus: true).ConfigureAwait(false);
            if (encryption.Code == 0) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_272548EE5647"));
            await RunAsync(ExecutablePath, ["--check", source], token).ConfigureAwait(false);
            var pages = await PageCountAsync(source, token).ConfigureAwait(false);
            if (pages is < 1 or > MaximumPages) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_88A85AF90DD7"));
            var geometry = await GeometryAsync(source, pages, token).ConfigureAwait(false);
            parent = Path.TrimEndingDirectorySeparator(string.IsNullOrWhiteSpace(options.OutputDirectory) ? Path.GetDirectoryName(source)! : Path.GetFullPath(options.OutputDirectory));
            if (!await Task.Run(() => Directory.Exists(parent), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false))
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_45C406D632F2"));
            temporary = Path.Combine(parent, ".orbit-pdf-optimize-" + Guid.NewGuid().ToString("N"));
            var working = temporary; var directory = parent; var creating = Task.Run(() => Directory.CreateDirectory(working), CancellationToken.None);
            try { await creating.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); }
            catch
            {
                temporary = null;
                _ = creating.ContinueWith(async task =>
                { if (task.IsCompletedSuccessfully) await CleanupAsync(working, directory).ConfigureAwait(false); else _ = task.Exception; },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
                throw;
            }
            var prepared = Path.Combine(temporary, "result.pdf");
            // Explicit generalized decoding leaves lossy DCT/JPX image data untouched.
            // No optimize-images/jpeg-quality/content-normalization flags are used.
            var args = new List<string> { source, "--compress-streams=y", "--recompress-flate", "--compression-level=9", "--object-streams=generate", "--decode-level=generalized" };
            if (options.Linearize) args.Add("--linearize"); args.Add(prepared);
            Recheck(source, token); await RunAsync(ExecutablePath, args, token).ConfigureAwait(false);
            var bytes = new FileInfo(prepared).Length;
            if (bytes is <= 0 or > MaximumOutputBytes) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_F39FE095864E"));
            await RunAsync(ExecutablePath, ["--check", prepared], token).ConfigureAwait(false);
            if (await PageCountAsync(prepared, token).ConfigureAwait(false) != pages
                || !geometry.SequenceEqual(await GeometryAsync(prepared, pages, token).ConfigureAwait(false), StringComparer.Ordinal))
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_E4735782A767"));
            if (options.Linearize)
            {
                var linear = await RunAsync(ExecutablePath, ["--check-linearization", prepared], token).ConfigureAwait(false);
                if (!linear.Output.Contains("no linearization errors", StringComparison.OrdinalIgnoreCase))
                    throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_4F6EC2FFDE74"));
            }
            token.ThrowIfCancellationRequested(); var stem = Path.GetFileNameWithoutExtension(source);
            if (stem.Length > 180) { stem = stem[..180]; if (char.IsHighSurrogate(stem[^1])) stem = stem[..^1]; }
            var output = DocumentFiles.CommitFile(prepared, parent, stem + "_optimized.pdf", token);
            return new DocumentResult(output, bytes, "PDF", lease.Length);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_62C28771FAF8")); }
        catch (TimeoutException exception) { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_89AF8C08CA9A"), exception); }
        catch (UnauthorizedAccessException exception) { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_A9F5940C7366"), exception); }
        catch (IOException exception) { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_4B96F8307BF3"), exception); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or System.Security.SecurityException)
        { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_7F02E1B84E23"), exception); }
        finally { await CleanupAsync(temporary, parent).ConfigureAwait(false); }
    }

    private async Task<int> PageCountAsync(string path, CancellationToken token)
    {
        var result = await RunAsync(ExecutablePath, ["--show-npages", path], token).ConfigureAwait(false);
        if (!int.TryParse(result.Output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var pages))
            throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_0D80BA1A821B"));
        return pages;
    }

    private async Task<string[]> GeometryAsync(string path, int pages, CancellationToken token)
    {
        var executable = FindPdfInfo() ?? throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_F7AB9D3120E2"));
        var result = await RunAsync(executable, ["-enc", "UTF-8", "-f", "1", "-l", pages.ToString(CultureInfo.InvariantCulture), "-box", path], token).ConfigureAwait(false);
        var lines = Regex.Matches(result.Output, @"^Page\s+(\d+)\s+(size|rot|MediaBox|CropBox|BleedBox|TrimBox|ArtBox):\s*([^\r\n]+)", RegexOptions.Multiline | RegexOptions.CultureInvariant)
            .Select(match => match.Groups[1].Value + ":" + match.Groups[2].Value + ":" + Regex.Replace(match.Groups[3].Value.Trim(), @"\s+", " ")).ToArray();
        if (lines.Length != pages * 7 || Regex.IsMatch(result.Output, @"^Encrypted:\s*yes\b", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_9BAB7098B817"));
        return lines;
    }

    private string? FindPdfInfo()
    {
        var name = OperatingSystem.IsWindows() ? "pdfinfo.exe" : "pdfinfo";
        var locations = new List<string?> { _popplerDirectory, Path.Combine(AppContext.BaseDirectory, "tools", "poppler"), Environment.GetEnvironmentVariable("ORBIT_POPPLER") };
        locations.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        foreach (var location in locations.Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            try
            {
                foreach (var suffix in new[] { name, Path.Combine("bin", name), Path.Combine("Library", "bin", name) })
                { var candidate = Path.GetFullPath(Path.Combine(location!.Trim('"'), suffix)); if (File.Exists(candidate)) return candidate; }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        return null;
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path); var full = Path.GetFullPath(path).Replace('/', Path.DirectorySeparatorChar);
        if (full.Length > 4096 || !Path.GetExtension(full).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal)
            || full.IndexOf(':', full.Length > 1 && full[1] == ':' ? 2 : 0) >= 0)
            throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_029544BF9D3C"));
        return full;
    }

    private static void Recheck(string path, CancellationToken token)
    {
        var depth = 0;
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            token.ThrowIfCancellationRequested(); if (++depth > 256) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_69639F7F85C0"));
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || depth == 1 && (attributes & FileAttributes.Directory) != 0)
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_37C25B3434A9"));
        }
    }

    private static async Task<FileStream> OpenInputAsync(string path, CancellationToken token)
    {
        var opening = Task.Run(() =>
        {
            Recheck(path, token); var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            try
            {
                Recheck(path, token);
                if ((File.GetAttributes(input.SafeFileHandle) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || input.Length is < 8 or > MaximumSourceBytes)
                    throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_767F8CA3190F"));
                return input;
            }
            catch { input.Dispose(); throw; }
        }, CancellationToken.None);
        FileStream stream;
        try { stream = await opening.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); }
        catch { _ = opening.ContinueWith(task => { if (task.IsCompletedSuccessfully) task.Result.Dispose(); else _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default); throw; }
        try
        {
            var header = new byte[8]; await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
            if (!header.AsSpan(0, 5).SequenceEqual("%PDF-"u8) || header[5] is < (byte)'0' or > (byte)'9' || header[6] != '.' || header[7] is < (byte)'0' or > (byte)'9')
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_31FC259731E0"));
            stream.Position = 0; return stream;
        }
        catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async Task<NativeResult> RunAsync(string executable, IReadOnlyList<string> args, CancellationToken token, bool encryptionStatus = false)
    {
        token.ThrowIfCancellationRequested(); var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { if (!process.Start()) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_A9AC8212E385")); }
        catch (System.ComponentModel.Win32Exception exception) { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_21C87B12A9A5"), exception); }
        var output = DrainAsync(process.StandardOutput, 2 * 1024 * 1024, token); var error = DrainAsync(process.StandardError, 128 * 1024, token);
        try
        {
            NativeStarted?.Invoke(string.Join(' ', args));
            await process.WaitForExitAsync(token).ConfigureAwait(false); await Task.WhenAll(output, error).WaitAsync(token).ConfigureAwait(false);
            var stdout = await output.ConfigureAwait(false); var stderr = await error.ConfigureAwait(false); token.ThrowIfCancellationRequested();
            if (stdout.Length > 2 * 1024 * 1024 || stderr.Length > 128 * 1024) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_FDA651449E2D"));
            if (encryptionStatus && (process.ExitCode == 0 || process.ExitCode == 2 && string.IsNullOrWhiteSpace(stderr))) return new NativeResult(process.ExitCode, stdout);
            if (process.ExitCode != 0) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_899CA879D952") + stderr[..Math.Min(stderr.Length, 700)].Trim());
            return new NativeResult(process.ExitCode, stdout);
        }
        finally
        {
            if (!process.HasExited) { try { process.Kill(true); } catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (Exception exception) when (exception is InvalidOperationException or TimeoutException) { }
            var drains = Task.WhenAll(output, error);
            try { await drains.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (TimeoutException) { process.StandardOutput.Dispose(); process.StandardError.Dispose(); Observe(drains); }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException) { }
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader, int maximum, CancellationToken token)
    {
        var result = new StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
            if (result.Length <= maximum) result.Append(buffer, 0, Math.Min(count, maximum + 1 - result.Length));
        return result.ToString();
    }

    private static async Task CleanupAsync(string? temporary, string? parent)
    {
        if (temporary is null || parent is null || !string.Equals(Path.GetDirectoryName(temporary), Path.TrimEndingDirectorySeparator(parent), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(temporary).StartsWith(".orbit-pdf-optimize-", StringComparison.Ordinal)) return;
        var deleting = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 11; attempt++)
            {
                try { if (Directory.Exists(temporary) && (File.GetAttributes(temporary) & FileAttributes.ReparsePoint) == 0) Directory.Delete(temporary, true); return; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                if (attempt < 10) await Task.Delay(Math.Min(300, 25 << Math.Min(attempt, 4))).ConfigureAwait(false);
            }
        }, CancellationToken.None);
        try { await deleting.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (TimeoutException) { Observe(deleting); }
    }
    private static void Observe(Task task) => _ = task.ContinueWith(completed => _ = completed.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private sealed record NativeResult(int Code, string Output);
}
