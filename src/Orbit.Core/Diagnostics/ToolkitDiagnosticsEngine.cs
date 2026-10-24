using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Orbit.Core.Documents;
using Orbit.Core.Ocr;

namespace Orbit.Core.Diagnostics;

/// <summary>Explicit, read-only local executable checks. No conversion, network request or file/profile write is performed.</summary>
public sealed class ToolkitDiagnosticsEngine
{
    public const int MaximumOutputBytes = 65536;
    public static readonly TimeSpan MaximumComponentTime = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaximumTotalTime = TimeSpan.FromSeconds(30);
    // Retained until the actual resolver worker exits, even if its caller cancels or times out.
    // This bounds background File.Exists/PATH work when a configured network disk stops responding.
    private static readonly SemaphoreSlim ResolverSlots = new(2, 2);
    private static readonly SemaphoreSlim NativeSlots = new(2, 2);
    private readonly IReadOnlyList<DiagnosticToolSpec> _specs;
    private readonly TimeSpan _componentTimeout, _totalTimeout, _cleanupReserve;
    internal Action<int>? ProcessStarted { get; set; }

    public ToolkitDiagnosticsEngine() : this(Defaults(), MaximumComponentTime, MaximumTotalTime) { }
    internal ToolkitDiagnosticsEngine(IReadOnlyList<DiagnosticToolSpec> specs, TimeSpan? componentTimeout = null, TimeSpan? totalTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(specs); _componentTimeout = componentTimeout ?? MaximumComponentTime; _totalTimeout = totalTimeout ?? MaximumTotalTime;
        if (specs.Count is < 1 or > 32 || _componentTimeout <= TimeSpan.Zero || _componentTimeout > MaximumComponentTime || _totalTimeout <= TimeSpan.Zero || _totalTimeout > MaximumTotalTime) throw new ArgumentOutOfRangeException(nameof(specs));
        _cleanupReserve = TimeSpan.FromTicks(Math.Min(TimeSpan.FromMilliseconds(250).Ticks, Math.Min(_componentTimeout.Ticks, _totalTimeout.Ticks) / 4));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        _specs = Array.AsReadOnly(specs.Select(spec =>
        {
            if (spec is null || spec.ResolvePath is null || string.IsNullOrWhiteSpace(spec.Id) || spec.Id.Length > 64 || !ids.Add(spec.Id) || string.IsNullOrWhiteSpace(spec.Name)
                || spec.Name.Length > 128 || string.IsNullOrWhiteSpace(spec.VersionMarker) || spec.VersionMarker.Length > 128 || spec.Arguments.Count > 16 || spec.Arguments.Any(value => value is null || value.Length > 2048)) throw new ArgumentException(global::Orbit.Core.Localization.L.Get("Text_1EF3168C5F49"), nameof(specs));
            return spec with { Arguments = Array.AsReadOnly(spec.Arguments.ToArray()) };
        }).ToArray());
    }

    public async Task<ToolkitDiagnosticsReport> InspectAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var total = CancellationTokenSource.CreateLinkedTokenSource(ct); total.CancelAfter(_totalTimeout - _cleanupReserve);
        var components = new List<ToolkitDiagnosticComponent>(); var resolved = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var spec in _specs)
        {
            ct.ThrowIfCancellationRequested(); string? path = null; ToolkitDiagnosticComponent component;
            if (total.IsCancellationRequested) component = Failure(spec, null, global::Orbit.Core.Localization.L.Get("Text_F29A65697331"));
            else
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(total.Token); deadline.CancelAfter(_componentTimeout - _cleanupReserve);
                try
                {
                    var value = await ResolveAsync(spec.ResolvePath, deadline.Token).ConfigureAwait(false); path = value.Path;
                    if (spec.SamePathAs is { } same && resolved.TryGetValue(same, out var existing) && string.Equals(path, existing, StringComparison.OrdinalIgnoreCase)) continue;
                    if (path is null || !value.Exists) component = new(spec.Id, spec.Name, ToolkitDiagnosticState.Missing, path, "", global::Orbit.Core.Localization.L.Get("Text_C2298FAE0C49"));
                    else
                    {
                        var output = await RunAsync(path, spec.Arguments, deadline.Token).ConfigureAwait(false);
                        var version = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim()).FirstOrDefault(value => value.Contains(spec.VersionMarker, StringComparison.OrdinalIgnoreCase));
                        component = version is null ? Failure(spec, path, global::Orbit.Core.Localization.L.Get("Text_9799855C679C"))
                            : new(spec.Id, spec.Name, ToolkitDiagnosticState.Ready, path, Short(version, 512), global::Orbit.Core.Localization.L.Get("Text_FD701D9D189B"));
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) { component = Failure(spec, path, total.IsCancellationRequested ? global::Orbit.Core.Localization.L.Get("Text_8C301A8CD91F") : global::Orbit.Core.Localization.L.Get("Text_E922D7F37B1E")); }
                catch (MissingToolException exception) { if (spec.SamePathAs is { } same && resolved.TryGetValue(same, out var other) && other is null) continue; component = new(spec.Id, spec.Name, ToolkitDiagnosticState.Missing, path, "", Short(exception.Message, 1200)); }
                catch (Exception exception) when (Expected(exception)) { component = Failure(spec, path, Short(exception.Message, 1200)); }
            }
            components.Add(component); resolved[spec.Id] = path;
            if (spec.IncludeOcrLanguages) await AppendLanguagesAsync(components, component, total.Token, ct).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested(); return new ToolkitDiagnosticsReport(DateTimeOffset.UtcNow, Array.AsReadOnly(components.ToArray()));
    }

    private async Task AppendLanguagesAsync(List<ToolkitDiagnosticComponent> components, ToolkitDiagnosticComponent tesseract, CancellationToken overall, CancellationToken caller)
    {
        string? output = null; string? failure = null; IReadOnlyDictionary<string, bool>? files = null;
        if (tesseract.State == ToolkitDiagnosticState.Ready && tesseract.Path is not null && !overall.IsCancellationRequested)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(overall); deadline.CancelAfter(_componentTimeout - _cleanupReserve);
            try
            {
                output = await RunAsync(tesseract.Path, ["--list-langs", "--tessdata-dir", "tessdata"], deadline.Token).ConfigureAwait(false);
                var directory = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(tesseract.Path)!, "tessdata");
                files = await ResolveModelsAsync(directory, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (caller.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { failure = overall.IsCancellationRequested ? global::Orbit.Core.Localization.L.Get("Text_8C301A8CD91F") : global::Orbit.Core.Localization.L.Get("Text_1C5C6A13EACB"); }
            catch (Exception exception) when (Expected(exception)) { failure = Short(exception.Message, 1200); }
        }
        foreach (var language in new[] { "rus", "eng" })
        {
            var path = tesseract.Path is null ? null : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(tesseract.Path)!, "tessdata", language + ".traineddata");
            var listed = output?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Any(line => line.Trim().Equals(language, StringComparison.Ordinal)) == true;
            var state = failure is not null || tesseract.State == ToolkitDiagnosticState.Error || overall.IsCancellationRequested ? ToolkitDiagnosticState.Error
                : tesseract.State == ToolkitDiagnosticState.Missing || !listed || files?.GetValueOrDefault(language) != true ? ToolkitDiagnosticState.Missing : ToolkitDiagnosticState.Ready;
            var detail = state == ToolkitDiagnosticState.Ready ? global::Orbit.Core.Localization.L.Get("Text_2BF3122239BB")
                : failure ?? (overall.IsCancellationRequested ? global::Orbit.Core.Localization.L.Get("Text_0E651B97036A") : state == ToolkitDiagnosticState.Missing ? global::Orbit.Core.Localization.L.Get("Text_F3EB2385A266") : global::Orbit.Core.Localization.L.Get("Text_43A092328133"));
            components.Add(new("ocr-" + language, "OCR · " + language, state, path, "", detail));
        }
    }

    private static async Task<(string? Path, bool Exists)> ResolveAsync(Func<string?> resolve, CancellationToken token)
    {
        await ResolverSlots.WaitAsync(token).ConfigureAwait(false);
        var worker = Task.Run(() =>
        {
            try
            {
                string? path;
                try { path = resolve(); }
                catch (Exception exception) when (exception is ImageProcessingException or MediaProcessingException or DocumentProcessingException) { throw new MissingToolException(exception.Message); }
                return (path, path is not null && File.Exists(path));
            }
            finally { ResolverSlots.Release(); }
        }, CancellationToken.None);
        try { return await worker.WaitAsync(token).ConfigureAwait(false); } catch { Observe(worker); throw; }
    }
    private static async Task<IReadOnlyDictionary<string, bool>> ResolveModelsAsync(string directory, CancellationToken token)
    {
        await ResolverSlots.WaitAsync(token).ConfigureAwait(false);
        var worker = Task.Run<IReadOnlyDictionary<string, bool>>(() =>
        {
            try { return new Dictionary<string, bool> { ["rus"] = Nonempty("rus"), ["eng"] = Nonempty("eng") }; }
            finally { ResolverSlots.Release(); }
            bool Nonempty(string value) { var path = System.IO.Path.Combine(directory, value + ".traineddata"); return File.Exists(path) && new FileInfo(path).Length > 0; }
        }, CancellationToken.None);
        try { return await worker.WaitAsync(token).ConfigureAwait(false); } catch { Observe(worker); throw; }
    }

    private async Task<string> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); await NativeSlots.WaitAsync(token).ConfigureAwait(false);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(executable))!, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument); start.Environment["LC_ALL"] = "C"; start.Environment["LANG"] = "C"; start.Environment["OMP_THREAD_LIMIT"] = "2";
        var process = new Process { StartInfo = start }; var startupOwned = true;
        try
        {
            // File-backed process startup can itself block on an unavailable configured disk.
            // Hold the global slot until the actual startup/late cleanup finishes.
            var starting = Task.Run(() => { if (!process.Start()) throw new IOException(global::Orbit.Core.Localization.L.Get("Text_9E5C7548DCDE")); }, CancellationToken.None);
            try { await starting.WaitAsync(token).ConfigureAwait(false); }
            catch
            {
                startupOwned = false;
                var late = starting.ContinueWith(async completed =>
                {
                    try { if (completed.IsCompletedSuccessfully) await StopAsync(process, Task.CompletedTask).ConfigureAwait(false); else _ = completed.Exception; }
                    finally { process.Dispose(); NativeSlots.Release(); }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap(); Observe(late); throw;
            }
            using var run = CancellationTokenSource.CreateLinkedTokenSource(token);
            var output = ReadAsync(process.StandardOutput.BaseStream, run.Token); var error = ReadAsync(process.StandardError.BaseStream, run.Token);
            void StopReaders() { try { run.Cancel(); } catch (ObjectDisposedException) { } }
            _ = output.ContinueWith(_ => StopReaders(), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            _ = error.ContinueWith(_ => StopReaders(), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            try
            {
                ProcessStarted?.Invoke(process.Id); await process.WaitForExitAsync(run.Token).ConfigureAwait(false); await Task.WhenAll(output, error).WaitAsync(run.Token).ConfigureAwait(false);
                var stdout = await output.ConfigureAwait(false); var stderr = await error.ConfigureAwait(false); token.ThrowIfCancellationRequested();
                var utf8 = new UTF8Encoding(false, true); var result = utf8.GetString(stdout) + "\n" + utf8.GetString(stderr);
                if (process.ExitCode != 0) throw new IOException(global::Orbit.Core.Localization.L.Get("Text_E18C1BAD9C76") + process.ExitCode + ": " + Short(result, 1000)); return result;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && (output.IsFaulted || error.IsFaulted)) { throw new IOException(global::Orbit.Core.Localization.L.Get("Text_6F6B7F11267F")); }
            finally { await StopAsync(process, Task.WhenAll(output, error)).ConfigureAwait(false); }
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3) { throw new MissingToolException(global::Orbit.Core.Localization.L.Get("Text_C5D159AD208E")); }
        finally { if (startupOwned) { process.Dispose(); NativeSlots.Release(); } }
    }
    private async Task StopAsync(Process process, Task readers)
    {
        if (!process.HasExited) { try { process.Kill(true); } catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { } }
        var stopped = Task.WhenAll(process.WaitForExitAsync(), readers);
        try { await stopped.WaitAsync(_cleanupReserve).ConfigureAwait(false); }
        catch (TimeoutException) { process.StandardOutput.Dispose(); process.StandardError.Dispose(); Observe(stopped); }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException) { }
    }
    private static async Task<byte[]> ReadAsync(Stream stream, CancellationToken token)
    {
        using var output = new MemoryStream(); var bytes = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(bytes, token).ConfigureAwait(false)) != 0)
        { if (output.Length + read > MaximumOutputBytes) throw new IOException(global::Orbit.Core.Localization.L.Get("Text_3FB1C64B48BA")); output.Write(bytes, 0, read); } return output.ToArray();
    }
    private static ToolkitDiagnosticComponent Failure(DiagnosticToolSpec spec, string? path, string detail) => new(spec.Id, spec.Name, ToolkitDiagnosticState.Error, path, "", detail);
    private static bool Expected(Exception exception) => exception is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException or NotSupportedException or System.Security.SecurityException or InvalidOperationException;
    private static string Short(string value, int maximum) { var result = new string(value.Select(character => char.IsControl(character) || character is '\u2028' or '\u2029' ? ' ' : character).ToArray()).Trim(); if (result.Length > maximum) result = result[..maximum]; return result.Length > 0 && char.IsHighSurrogate(result[^1]) ? result[..^1] : result; }
    private static void Observe(Task task) => _ = task.ContinueWith(completed => _ = completed.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private sealed class MissingToolException(string message) : IOException(message);

    private static IReadOnlyList<DiagnosticToolSpec> Defaults()
    {
        var images = new ImageMagickEngine(); var media = new MediaEngine(); var documents = new DocumentEngine(); var extract = new PdfExtractEngine(); var optimize = new PdfOptimizeEngine(); var ocr = new OcrEngine();var sevenzip = new SevenZipArchiveEngine();
        return new DiagnosticToolSpec[]
        {
            new("imagemagick", "ImageMagick", () => images.ExecutablePath, ["-version"], "ImageMagick"),
            new("ffmpeg", "FFmpeg", () => media.ExecutablePath, ["-version"], "ffmpeg version"),
            new("ffprobe", "ffprobe", () => media.ProbeExecutablePath, ["-version"], "ffprobe version"),
            new("pdfinfo", "Poppler · pdfinfo", () => documents.PdfInfoExecutablePath, ["-v"], "pdfinfo version"),
            new("pdftoppm", "Poppler · pdftoppm", () => documents.PdfRendererExecutablePath, ["-v"], "pdftoppm version"),
            new("pdftotext", "Poppler · pdftotext", () => extract.TextExecutablePath, ["-v"], "pdftotext version"),
            new("pdfimages", "Poppler · pdfimages", () => extract.ImagesExecutablePath, ["-v"], "pdfimages version"),
            new("qpdf", "qpdf", () => optimize.ExecutablePath, ["--version"], "qpdf version"),
            new("sevenzip", "7-Zip", () => sevenzip.ExecutablePath, ["i"], "7-Zip"),
            new("tesseract", "Tesseract", () => ocr.ExecutablePath, ["--version"], "tesseract", true),
            new("pdfinfo-extract", global::Orbit.Core.Localization.L.Get("Text_CBCC01B1DA37"), () => extract.PdfInfoExecutablePath, ["-v"], "pdfinfo version", SamePathAs: "pdfinfo"),
            new("pdfinfo-optimize", global::Orbit.Core.Localization.L.Get("Text_888CEB7F1021"), () => optimize.PdfInfoExecutablePath, ["-v"], "pdfinfo version", SamePathAs: "pdfinfo"),
            new("pdfinfo-ocr", "Poppler · pdfinfo (OCR)", () => ocr.PdfInfoExecutablePath, ["-v"], "pdfinfo version", SamePathAs: "pdfinfo"),
            new("pdftoppm-ocr", "Poppler · pdftoppm (OCR)", () => ocr.PdfRendererExecutablePath, ["-v"], "pdftoppm version", SamePathAs: "pdftoppm")
        };
    }
}
