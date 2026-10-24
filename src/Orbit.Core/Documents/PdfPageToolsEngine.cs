using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Orbit.Core.Documents;

/// <summary>Content-preserving PDF page rotations and contiguous splits through local qpdf.
/// Inputs stay read-locked; only completely validated new results are published.</summary>
public sealed class PdfPageToolsEngine
{
    public const long MaximumSourceBytes = 512L * 1024 * 1024;
    public const int MaximumPages = 1000;
    public const long MaximumOutputBytes = 1024L * 1024 * 1024;
    private readonly PdfAssemblyEngine _qpdf;
    private readonly DocumentEngine _documents;
    private readonly TimeSpan _operationTimeout;

    public PdfPageToolsEngine(string? qpdfExecutable = null, TimeSpan? operationTimeout = null, string? popplerDirectory = null)
    {
        _operationTimeout = operationTimeout ?? TimeSpan.FromMinutes(5);
        if (_operationTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        _qpdf = new PdfAssemblyEngine(qpdfExecutable, _operationTimeout);
        _documents = new DocumentEngine(popplerDirectory, _operationTimeout);
    }

    public string ExecutablePath => _qpdf.ExecutablePath;
    public bool Available => _qpdf.Available && _documents.PdfToolsAvailable;

    public Task<DocumentResult> RotateAsync(string path, PdfRotateOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);ct.ThrowIfCancellationRequested();
        if (options.RotationDegrees is not (90 or 180 or 270))
            throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_0D6A6C1A1C8E"));
        return ExecuteAsync(path, options.OutputDirectory, options.RotationDegrees, options.PageSelection, null, ct);
    }

    public Task<DocumentResult> SplitAsync(string path, PdfSplitOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);ct.ThrowIfCancellationRequested();
        if (options.PagesPerFile is < 1 or > MaximumPages)
            throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_14301C02E5F2"));
        return ExecuteAsync(path, options.OutputDirectory, null, null, options.PagesPerFile, ct);
    }

    private async Task<DocumentResult> ExecuteAsync(string path, string? requestedDirectory, int? rotation,
        string? selection, int? pagesPerFile, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_operationTimeout);
        var token = timeout.Token;
        string? temporary = null, directory = null;
        try
        {
            var fullPath = NormalizeSourcePath(path);
            await using var sourceFile = await OpenInputAsync(fullPath, token).ConfigureAwait(false);
            var source = await InspectSourceAsync(fullPath, sourceFile.Length, token).ConfigureAwait(false);
            // Rotation is a set operation: repeated selections rotate a page once, never reorder or duplicate it.
            var selected = rotation.HasValue ? PdfAssemblyEngine.ParsePageSelection(selection!, source.Pages).Distinct().ToArray() : Array.Empty<int>();
            directory = string.IsNullOrWhiteSpace(requestedDirectory) ? Path.GetDirectoryName(fullPath)! : Path.GetFullPath(requestedDirectory);
            if (!await Task.Run(() => Directory.Exists(directory), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false))
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_45C406D632F2"));
            temporary = Path.Combine(directory, ".orbit-pdf-page-tools-" + Guid.NewGuid().ToString("N"));
            var working = temporary;var parent = directory;
            var creating = Task.Run(() => Directory.CreateDirectory(working), CancellationToken.None);
            try { await creating.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); }
            catch
            {
                temporary = null;
                _ = creating.ContinueWith(async task =>
                {
                    if (task.IsCompletedSuccessfully) await CleanupAsync(working, parent).ConfigureAwait(false);
                    else _ = task.Exception;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
                throw;
            }
            if (rotation.HasValue)
            {
                var prepared = Path.Combine(temporary, "result.pdf");
                var range = string.Join(',', selected.Select(page => page.ToString(CultureInfo.InvariantCulture)));
                RecheckSource(fullPath, token);
                await RunQpdfAsync([fullPath, "--rotate=+" + rotation.Value.ToString(CultureInfo.InvariantCulture) + ":" + range, prepared], token).ConfigureAwait(false);
                var output = await ValidateOutputAsync(prepared, source.Pages, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var target = DocumentFiles.CommitFile(prepared, directory, Path.GetFileNameWithoutExtension(fullPath) + "_rotated_" + rotation.Value.ToString(CultureInfo.InvariantCulture) + ".pdf", token);
                return new DocumentResult(target, output.Bytes, "PDF", source.Bytes);
            }
            var payload = Path.Combine(temporary, "parts");Directory.CreateDirectory(payload);
            long totalBytes = 0;var count = pagesPerFile!.Value;
            for (var first = 1; first <= source.Pages; first += count)
            {
                token.ThrowIfCancellationRequested();RecheckSource(fullPath, token);
                var last = Math.Min(source.Pages, first + count - 1);
                var range = first.ToString(CultureInfo.InvariantCulture) + "-" + last.ToString(CultureInfo.InvariantCulture);
                var name = "part_" + first.ToString("D4", CultureInfo.InvariantCulture) + (last == first ? "" : "-" + last.ToString("D4", CultureInfo.InvariantCulture)) + ".pdf";
                var prepared = Path.Combine(payload, name);
                // Incremental parts bound resource duplication: validate cumulative bytes before another qpdf invocation.
                await RunQpdfAsync([fullPath, "--pages", ".", range, "--", prepared], token).ConfigureAwait(false);
                var output = await ValidateOutputAsync(prepared, last - first + 1, token).ConfigureAwait(false);
                totalBytes = checked(totalBytes + output.Bytes);
                if (totalBytes > MaximumOutputBytes) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_2A3E03BDA467"));
            }
            token.ThrowIfCancellationRequested();
            var folder = DocumentFiles.CommitDirectory(payload, directory, Path.GetFileNameWithoutExtension(fullPath) + "_split", token);
            return new DocumentResult(folder, totalBytes, "FOLDER", source.Bytes);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_A84AF1D52591")); }
        catch (TimeoutException exception)
        { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_E1C332832A92"), exception); }
        catch (UnauthorizedAccessException exception)
        { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_27154E4A7AD7"), exception); }
        catch (IOException exception)
        { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_43113181B237"), exception); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or System.Security.SecurityException or OverflowException)
        { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_316C7E3FC4CC"), exception); }
        finally { await CleanupAsync(temporary, directory).ConfigureAwait(false); }
    }

    private async Task<PdfMetadata> InspectSourceAsync(string path, long expectedBytes, CancellationToken token)
    {
        var encryption = await RunQpdfAsync(["--is-encrypted", path], token, encryptionStatus: true).ConfigureAwait(false);
        if (encryption.ExitCode == 0) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_272548EE5647"));
        await RunQpdfAsync(["--check", path], token).ConfigureAwait(false);
        var count = await QpdfPageCountAsync(path, token).ConfigureAwait(false);
        if (count is < 1 or > MaximumPages) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_9783A7DAAC9A"));
        var source = await _documents.InspectPdfAsync(path, token).WaitAsync(token).ConfigureAwait(false);
        if (source.Pages != count || source.Bytes != expectedBytes)
            throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_5875C797E453"));
        return source;
    }

    private async Task<PdfMetadata> ValidateOutputAsync(string path, int expectedPages, CancellationToken token)
    {
        var bytes = new FileInfo(path).Length;
        if (bytes is <= 0 or > MaximumOutputBytes) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_E947E4EE34AD"));
        await RunQpdfAsync(["--check", path], token).ConfigureAwait(false);
        var count = await QpdfPageCountAsync(path, token).ConfigureAwait(false);
        var output = await _documents.InspectPdfAsync(path, token).WaitAsync(token).ConfigureAwait(false);
        if (count != expectedPages || output.Pages != expectedPages || output.Bytes != bytes)
            throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_83F72CD897C6"));
        return output;
    }

    private async Task<int> QpdfPageCountAsync(string path, CancellationToken token)
    {
        var response = await RunQpdfAsync(["--show-npages", path], token).ConfigureAwait(false);
        if (!int.TryParse(response.Output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_093B4429A38C"));
        return count;
    }

    private static string NormalizeSourcePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path).Replace('/', Path.DirectorySeparatorChar);
        if (full.Length > 4096 || !Path.GetExtension(full).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal)
            || full.IndexOf(':', full.Length > 1 && full[1] == ':' ? 2 : 0) >= 0)
            throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_029544BF9D3C"));
        return full;
    }

    private static void RecheckSource(string path, CancellationToken token)
    {
        string? current = path;var depth = 0;
        while (!string.IsNullOrEmpty(current))
        {
            token.ThrowIfCancellationRequested();
            if (++depth > 256) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_69639F7F85C0"));
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_F9EFCBA5C1F5"));
            if (depth == 1 && (attributes & FileAttributes.Directory) != 0) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_03827496CF52"));
            current = Path.GetDirectoryName(current);
        }
    }

    private static async Task<FileStream> OpenInputAsync(string path, CancellationToken token)
    {
        var pending = Task.Run(() =>
        {
            RecheckSource(path, token);
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            try
            {
                RecheckSource(path, token);
                if ((File.GetAttributes(stream.SafeFileHandle) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                    throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_EA8F2CF435C5"));
                if (stream.Length is < 8 or > MaximumSourceBytes) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_C5AE176775BA"));
                return stream;
            }
            catch { stream.Dispose();throw; }
        }, CancellationToken.None);
        FileStream input;
        try { input = await pending.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); }
        catch
        {
            _ = pending.ContinueWith(task => { if (task.IsCompletedSuccessfully) task.Result.Dispose();else _ = task.Exception; },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
        try
        {
            var header = new byte[8];await input.ReadExactlyAsync(header, token).ConfigureAwait(false);
            if (!header.AsSpan(0, 5).SequenceEqual("%PDF-"u8) || header[5] is < (byte)'0' or > (byte)'9' || header[6] != '.' || header[7] is < (byte)'0' or > (byte)'9')
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_31FC259731E0"));
            input.Position = 0;return input;
        }
        catch { await input.DisposeAsync().ConfigureAwait(false);throw; }
    }

    private async Task<ProcessResult> RunQpdfAsync(IReadOnlyList<string> arguments, CancellationToken token, bool encryptionStatus = false)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(ExecutablePath) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { if (!process.Start()) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_7A595387E303")); }
        catch (System.ComponentModel.Win32Exception exception) { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_FBDB1EF61597"), exception); }
        var stdout = DrainAsync(process.StandardOutput, 2 * 1024 * 1024);var stderr = DrainAsync(process.StandardError, 128 * 1024);
        try { await process.WaitForExitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            Kill(process);
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (Exception exception) when (exception is TimeoutException or InvalidOperationException) { }
            var readers = Task.WhenAll(stdout, stderr);
            try { await readers.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                process.StandardOutput.Dispose();process.StandardError.Dispose();
                _ = readers.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
            throw;
        }
        var output = await stdout.ConfigureAwait(false);var error = await stderr.ConfigureAwait(false);token.ThrowIfCancellationRequested();
        if (output.Length > 2 * 1024 * 1024 || error.Length > 128 * 1024) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_B0ED285E9EB4"));
        if (encryptionStatus && process.ExitCode == 0) return new ProcessResult(0, output);
        if (encryptionStatus && process.ExitCode == 2 && string.IsNullOrWhiteSpace(error)) return new ProcessResult(2, output);
        // qpdf's warning status3 is deliberately not suppressed or treated as successful repair.
        if (process.ExitCode != 0) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_9A58FD0D75D6") + error[..Math.Min(error.Length, 800)].Trim());
        return new ProcessResult(process.ExitCode, output);
    }

    private static async Task<string> DrainAsync(StreamReader reader, int maximum)
    {
        var result = new StringBuilder();var buffer = new char[4096];int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
            if (result.Length <= maximum) result.Append(buffer, 0, Math.Min(count, maximum + 1 - result.Length));
        return result.ToString();
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static async Task CleanupAsync(string? temporary, string? parent)
    {
        if (temporary is null || parent is null || !string.Equals(Path.GetDirectoryName(temporary), Path.TrimEndingDirectorySeparator(parent), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(temporary).StartsWith(".orbit-pdf-page-tools-", StringComparison.Ordinal)) return;
        var deleting = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 11; attempt++)
            {
                try
                {
                    if (Directory.Exists(temporary) && (File.GetAttributes(temporary) & FileAttributes.ReparsePoint) == 0) Directory.Delete(temporary, recursive: true);
                    return;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                if (attempt < 10) await Task.Delay(Math.Min(300, 25 << Math.Min(attempt, 4))).ConfigureAwait(false);
            }
        }, CancellationToken.None);
        try { await deleting.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (TimeoutException)
        { _ = deleting.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default); }
    }

    private sealed record ProcessResult(int ExitCode, string Output);
}
