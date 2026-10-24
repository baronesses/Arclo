using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Orbit.Core.Documents;

/// <summary>Extracts the existing text layer or embedded image objects, never OCR or page renders.</summary>
public sealed class PdfExtractEngine
{
    public const long MaximumSourceBytes = 512L * 1024 * 1024;
    public const int MaximumPages = 1000;
    public const int MaximumImages = 1000;
    public const long MaximumTextBytes = 64L * 1024 * 1024;
    public const long MaximumImageBytes = 256L * 1024 * 1024;
    public const long MaximumTotalImageBytes = 1024L * 1024 * 1024;
    private const string TemporaryPrefix = ".orbit-pdf-extract-";
    private readonly string? _directory;
    private readonly TimeSpan _timeout;
    internal Action<string, int>? ProcessStarted { get; set; }

    public PdfExtractEngine(string? popplerDirectory = null, TimeSpan? operationTimeout = null)
    {
        _directory = popplerDirectory;_timeout = operationTimeout ?? TimeSpan.FromMinutes(5);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
    }

    public string? PdfInfoExecutablePath => FindTool("pdfinfo");
    public string? TextExecutablePath => FindTool("pdftotext");
    public string? ImagesExecutablePath => FindTool("pdfimages");

    public bool TextExtractionAvailable => FindTool("pdfinfo") is not null && FindTool("pdftotext") is not null;
    public bool ImagesExtractionAvailable => FindTool("pdfinfo") is not null && FindTool("pdfimages") is not null;

    public Task<DocumentResult> ExtractTextAsync(string path, PdfTextOptions options, CancellationToken ct = default)
    { ArgumentNullException.ThrowIfNull(options);return ExtractAsync(path, options.OutputDirectory, false, options.PreserveLayout, ct); }
    public Task<DocumentResult> ExtractImagesAsync(string path, PdfEmbeddedImagesOptions options, CancellationToken ct = default)
    { ArgumentNullException.ThrowIfNull(options);return ExtractAsync(path, options.OutputDirectory, true, false, ct); }

    private async Task<DocumentResult> ExtractAsync(string path, string? requested, bool images, bool layout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(_timeout);
        var token = deadline.Token;string? temporary = null, directory = null;
        try
        {
            var source = NormalizePath(path);
            await using var lease = await OpenInputAsync(source, token).ConfigureAwait(false);
            var info = await RunAsync("pdfinfo", ["-enc", "UTF-8", source], token).ConfigureAwait(false);
            var pages = Regex.Match(info, @"^Pages:\s*(\d+)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
            if (!pages.Success || !int.TryParse(pages.Groups[1].Value, out var count) || count is < 1 or > MaximumPages)
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_D05AF835AA08"));
            if (Regex.IsMatch(info, @"^Encrypted:\s*yes\b", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_272548EE5647"));
            IReadOnlyList<ImageInfo> imageList = [];
            if (images)
            {
                imageList = ParseImages(await RunAsync("pdfimages", ["-list", source], token).ConfigureAwait(false), count);
                if (imageList.Count == 0) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_FECF3970AE6A"));
            }
            directory = string.IsNullOrWhiteSpace(requested) ? Path.GetDirectoryName(source)! : Path.GetFullPath(requested);
            if (!await Task.Run(() => Directory.Exists(directory), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false))
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_45C406D632F2"));
            temporary = Path.Combine(directory, TemporaryPrefix + Guid.NewGuid().ToString("N"));
            var working = temporary;var parent = directory;var creating = Task.Run(() => Directory.CreateDirectory(working), CancellationToken.None);
            try { await creating.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); }
            catch
            {
                temporary = null;
                _ = creating.ContinueWith(async task => { if (task.IsCompletedSuccessfully) await CleanupAsync(working, parent).ConfigureAwait(false);else _ = task.Exception; },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();throw;
            }
            Recheck(source, token);var stem = ShortStem(source);
            if (!images)
            {
                var prepared = Path.Combine(temporary, "text.txt");var args = new List<string> { "-enc", "UTF-8", "-eol", "unix" };
                if (layout) args.Add("-layout");args.AddRange([source, prepared]);
                await RunAsync("pdftotext", args, token, temporary, false).ConfigureAwait(false);
                var bytes = new FileInfo(prepared).Length;
                if (bytes > MaximumTextBytes) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_A0A4EE4B1D97"));
                var hasText = false;
                using (var reader = new StreamReader(prepared, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false))
                {
                    var buffer = new char[16384];int read;
                    while ((read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
                        for (var index = 0; index < read; index++) if (!char.IsWhiteSpace(buffer[index]) && !char.IsControl(buffer[index]) && buffer[index] != '\uFEFF') hasText = true;
                }
                if (!hasText) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_E3969AD9EFE4"));
                token.ThrowIfCancellationRequested();
                return new DocumentResult(DocumentFiles.CommitFile(prepared, directory, stem + "_text.txt", token), bytes, "TXT", lease.Length);
            }
            var payload = Path.Combine(temporary, "images");Directory.CreateDirectory(payload);
            await RunAsync("pdfimages", ["-png", source, Path.Combine(payload, "image")], token, payload, true).ConfigureAwait(false);
            var files = Directory.GetFiles(payload).OrderBy(Path.GetFileName, StringComparer.Ordinal).ToArray();
            if (files.Length != imageList.Count || Directory.GetDirectories(payload).Length != 0)
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_1AB038938C4D"));
            long total = 0;
            for (var index = 0; index < imageList.Count; index++)
            {
                token.ThrowIfCancellationRequested();var expected = Path.Combine(payload, "image-" + index.ToString("D3", CultureInfo.InvariantCulture) + ".png");
                if (!File.Exists(expected) || (File.GetAttributes(expected) & FileAttributes.ReparsePoint) != 0)
                    throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_F1EA3B187617"));
                var bytes = new FileInfo(expected).Length;
                if (bytes is < 45 or > MaximumImageBytes) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_FFD338A251D6"));
                total = checked(total + bytes);if (total > MaximumTotalImageBytes) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_AD0F898C0B73"));
                await ValidatePngAsync(expected, imageList[index], token).ConfigureAwait(false);
            }
            var note = global::Orbit.Core.Localization.L.Get("Text_B9992BC71FC4") +
                string.Join('\n', imageList.Select((item, index) => global::Orbit.Core.Localization.L.Format("Text_56B8BC65E4ED",(object?)(index),(object?)(item.Page),(object?)(item.Type),(object?)(item.Width),(object?)(item.Height)))) + "\n";
            var readme = Path.Combine(payload, "README.txt");await File.WriteAllTextAsync(readme, note, new UTF8Encoding(false), token).ConfigureAwait(false);
            total = checked(total + new FileInfo(readme).Length);
            if (total > MaximumTotalImageBytes) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_59147AAF4891"));
            token.ThrowIfCancellationRequested();
            return new DocumentResult(DocumentFiles.CommitDirectory(payload, directory, stem + "_images", token), total, "FOLDER", lease.Length);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_A63B802646CD")); }
        catch (DecoderFallbackException exception) { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_25CEDEF6CCB5"), exception); }
        catch (InvalidDataException exception) { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_6254C132ADA2"), exception); }
        catch (TimeoutException exception) { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_47EAC0E3C875"), exception); }
        catch (UnauthorizedAccessException exception) { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_A9F5940C7366"), exception); }
        catch (IOException exception) { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_860574A730B7"), exception); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or System.Security.SecurityException or OverflowException)
        { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_3D51133C2EFB"), exception); }
        finally { if (temporary is not null && directory is not null) await CleanupAsync(temporary, directory).ConfigureAwait(false); }
    }

    private static IReadOnlyList<ImageInfo> ParseImages(string text, int pages)
    {
        var result = new List<ImageInfo>();long totalDecoded = 0;
        foreach (var line in text.Split('\n'))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0 || fields[0] is "page" || fields[0].All(character => character == '-')) continue;
            if (fields.Length < 10 || !int.TryParse(fields[0], out var page) || !int.TryParse(fields[1], out var index)
                || page < 1 || page > pages || index != result.Count || fields[2] is not ("image" or "mask" or "smask" or "stencil")
                || !int.TryParse(fields[3], out var width) || !int.TryParse(fields[4], out var height) || !int.TryParse(fields[6], out var components)
                || !int.TryParse(fields[7], out var bits) || width is < 1 or > 16000 || height is < 1 or > 16000
                || (long)width * height > 40_000_000 || components is < 1 or > 4 || bits is not (1 or 2 or 4 or 8 or 16))
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_5523591CCE26"));
            var decoded = checked((long)width * height * Math.Max(4, components) * Math.Max(1, bits / 8));
            totalDecoded = checked(totalDecoded + decoded);
            if (decoded > MaximumImageBytes || totalDecoded > MaximumTotalImageBytes || result.Count >= MaximumImages)
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_DB545D14EB67"));
            result.Add(new ImageInfo(page, fields[2], width, height));
        }
        return result;
    }

    private static async Task ValidatePngAsync(string path, ImageInfo expected, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        var signature = new byte[8];await input.ReadExactlyAsync(signature, token).ConfigureAwait(false);
        if (!signature.AsSpan().SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_4CD60135A6B7"));
        using var compressed = new MemoryStream();var header = new byte[8];var buffer = new byte[65536];var crcBytes = new byte[4];
        var chunkIndex = 0;var ended = false;var channels = 0;var bits = 0;var sawData = false;var endedData = false;
        while (!ended)
        {
            token.ThrowIfCancellationRequested();if (++chunkIndex > 16384) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_E4B06EDFC9A6"));
            await input.ReadExactlyAsync(header, token).ConfigureAwait(false);var size = BinaryPrimitives.ReadUInt32BigEndian(header);var kind = Encoding.ASCII.GetString(header, 4, 4);
            if (size > MaximumImageBytes || size > input.Length - input.Position - 4 || chunkIndex == 1 && kind != "IHDR") throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_3C00EBDB058D"));
            if (kind == "IHDR" && (chunkIndex != 1 || size != 13) || kind == "IDAT" && endedData || kind == "IEND" && size != 0)
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_E8B48269670C"));
            var crc = UpdateCrc(uint.MaxValue, header.AsSpan(4));var remaining = (long)size;
            while (remaining > 0)
            {
                var read = (int)Math.Min(buffer.Length, remaining);await input.ReadExactlyAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                crc = UpdateCrc(crc, buffer.AsSpan(0, read));
                if (kind == "IHDR")
                {
                    if (BinaryPrimitives.ReadUInt32BigEndian(buffer) != expected.Width || BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(4)) != expected.Height || buffer[10] != 0 || buffer[11] != 0 || buffer[12] != 0)
                        throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_86E84F6D7096"));
                    bits = buffer[8];channels = buffer[9] switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
                    if (channels == 0 || bits is not (1 or 2 or 4 or 8 or 16) || buffer[9] is 2 or 4 or 6 && bits is not (8 or 16) || buffer[9] == 3 && bits == 16)
                        throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_20A87F9FE054"));
                }
                if (kind == "IDAT") { if (compressed.Length + read > MaximumImageBytes) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_A62ECF3D99CD"));compressed.Write(buffer, 0, read);sawData = true; }
                remaining -= read;
            }
            await input.ReadExactlyAsync(crcBytes, token).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32BigEndian(crcBytes) != ~crc) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_236503DC8AA9"));
            if (sawData && kind != "IDAT") endedData = true;if (kind == "IEND") ended = true;
        }
        if (!sawData || input.Position != input.Length) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_143A8A755CBA"));
        var row = checked(((long)expected.Width * channels * bits + 7) / 8);
        if ((row + 1) * expected.Height > MaximumImageBytes) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_B9D359BF04BC"));
        compressed.Position = 0;using var inflater = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: true);var filter = new byte[1];
        for (var y = 0; y < expected.Height; y++)
        {
            await inflater.ReadExactlyAsync(filter, token).ConfigureAwait(false);if (filter[0] > 4) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_A33039299852"));
            for (long left = row; left > 0;) { var read = (int)Math.Min(buffer.Length, left);await inflater.ReadExactlyAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);left -= read; }
        }
        if (await inflater.ReadAsync(filter, token).ConfigureAwait(false) != 0) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_8E29F44FB0A0"));
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    { foreach (var value in bytes) { crc ^= value;for (var bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb88320U; }return crc; }

    private async Task<string> RunAsync(string name, IReadOnlyList<string> arguments, CancellationToken token, string? watched = null, bool images = false)
    {
        token.ThrowIfCancellationRequested();var executable = FindTool(name) ?? throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Format("Text_AA16ABBE11D6",(object?)(name)));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);start.Environment["LC_ALL"] = "C";start.Environment["LANG"] = "C";
        for (var current = Path.GetDirectoryName(executable); current is not null; current = Path.GetDirectoryName(current))
        { var data = Path.Combine(current, "share", "poppler");if (Directory.Exists(data)) { start.Environment["POPPLER_DATADIR"] = data;break; } }
        using var process = new Process { StartInfo = start };
        try { if (!process.Start()) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_61FB04086524")); }
        catch (System.ComponentModel.Win32Exception exception) { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_2A9A05441AB7"), exception); }
        var output = DrainAsync(process.StandardOutput, 2 * 1024 * 1024);var error = DrainAsync(process.StandardError, 128 * 1024);
        using var watchCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var monitoring = watched is null ? Task.Delay(Timeout.Infinite, watchCancellation.Token) : WatchAsync(watched, images, watchCancellation.Token);
        try
        {
            ProcessStarted?.Invoke(name, process.Id);var exit = process.WaitForExitAsync(token);
            var first = await Task.WhenAny(exit, monitoring).ConfigureAwait(false);if (first == monitoring) await monitoring.ConfigureAwait(false);
            await exit.ConfigureAwait(false);await Task.WhenAll(output, error).WaitAsync(token).ConfigureAwait(false);
            var stdout = await output.ConfigureAwait(false);var stderr = await error.ConfigureAwait(false);token.ThrowIfCancellationRequested();
            if (stdout.Length > 2 * 1024 * 1024 || stderr.Length > 128 * 1024) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_2E4797E42227"));
            if (stderr.Contains("password", StringComparison.OrdinalIgnoreCase) || stderr.Contains("encrypted", StringComparison.OrdinalIgnoreCase) || process.ExitCode == 3)
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_97980CB24063"));
            if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(stderr))
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_1DCEEDDFD09F") + stderr.Trim()[..Math.Min(600, stderr.Trim().Length)]);
            return stdout;
        }
        finally
        {
            watchCancellation.Cancel();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (Exception exception) when (exception is TimeoutException or InvalidOperationException) { }
            var readers = Task.WhenAll(output, error);
            try { await readers.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (TimeoutException) { process.StandardOutput.Dispose();process.StandardError.Dispose();Observe(readers); }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
            Observe(monitoring);
        }
    }

    private static Task WatchAsync(string directory, bool images, CancellationToken token) => Task.Run(async () =>
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();long total = 0;var count = 0;
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                token.ThrowIfCancellationRequested();var bytes = new FileInfo(path).Length;total = checked(total + bytes);
                if (++count > (images ? MaximumImages : 1) || bytes > (images ? MaximumImageBytes : MaximumTextBytes) || total > (images ? MaximumTotalImageBytes : MaximumTextBytes))
                    throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_57E64F7772C4"));
            }
            await Task.Delay(100, token).ConfigureAwait(false);
        }
    }, CancellationToken.None);

    private string? FindTool(string name)
    {
        var exe = name + (OperatingSystem.IsWindows() ? ".exe" : "");
        foreach (var root in new[] { _directory, Environment.GetEnvironmentVariable("ORBIT_POPPLER"), Path.Combine(AppContext.BaseDirectory, "tools", "poppler") }.Where(value => !string.IsNullOrWhiteSpace(value)))
            foreach (var suffix in new[] { "", "bin", Path.Combine("Library", "bin") })
                try { var path = Path.GetFullPath(Path.Combine(root!, suffix, exe));if (File.Exists(path)) return path; }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { }
        return null;
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_A3EC001C1DF5"));
        var full = Path.GetFullPath(path);
        if (!Path.GetExtension(full).Equals(".pdf", StringComparison.OrdinalIgnoreCase) || full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal)
            || full.IndexOf(':', full.Length > 1 && full[1] == ':' ? 2 : 0) >= 0) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_029544BF9D3C"));
        return full;
    }
    private static string ShortStem(string path) { var stem = Path.GetFileNameWithoutExtension(path);if (stem.Length <= 180) return stem;stem = stem[..180];return char.IsHighSurrogate(stem[^1]) ? stem[..^1] : stem; }
    private static void Recheck(string path, CancellationToken token)
    {
        var depth = 0;
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            token.ThrowIfCancellationRequested();if (++depth > 256) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_509341ED3164"));
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || depth == 1 && (attributes & FileAttributes.Directory) != 0)
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_37A66DB342A9"));
        }
    }
    private static async Task<FileStream> OpenInputAsync(string path, CancellationToken token)
    {
        var pending = Task.Run(() =>
        {
            Recheck(path, token);var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            try { Recheck(path, token);if ((File.GetAttributes(input.SafeFileHandle) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || input.Length is < 8 or > MaximumSourceBytes) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_767F8CA3190F"));return input; }
            catch { input.Dispose();throw; }
        }, CancellationToken.None);
        FileStream source;
        try { source = await pending.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); }
        catch { _ = pending.ContinueWith(task => { if (task.IsCompletedSuccessfully) task.Result.Dispose();else _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);throw; }
        try
        {
            var header = new byte[8];await source.ReadExactlyAsync(header, token).ConfigureAwait(false);
            if (!header.AsSpan(0, 5).SequenceEqual("%PDF-"u8) || header[5] is < (byte)'0' or > (byte)'9' || header[6] != '.' || header[7] is < (byte)'0' or > (byte)'9') throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_31FC259731E0"));
            source.Position = 0;return source;
        }
        catch { await source.DisposeAsync().ConfigureAwait(false);throw; }
    }
    private static async Task<string> DrainAsync(StreamReader reader, int maximum)
    { var result = new StringBuilder();var buffer = new char[4096];int read;while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0) if (result.Length <= maximum) result.Append(buffer, 0, Math.Min(read, maximum + 1 - result.Length));return result.ToString(); }
    private static void Observe(Task task) => _ = task.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private static async Task CleanupAsync(string temporary, string parent)
    {
        if (!string.Equals(Path.GetDirectoryName(temporary), Path.TrimEndingDirectorySeparator(parent), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(temporary).StartsWith(TemporaryPrefix, StringComparison.Ordinal)) return;
        var deleting = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 24; attempt++)
            {
                try { if (!Directory.Exists(temporary)) return;if ((File.GetAttributes(temporary) & FileAttributes.ReparsePoint) != 0) return;Directory.Delete(temporary, true);return; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                await Task.Delay(Math.Min(400, 25 << Math.Min(4, attempt))).ConfigureAwait(false);
            }
        }, CancellationToken.None);
        try { await deleting.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (TimeoutException) { Observe(deleting); }
    }
    private sealed record ImageInfo(int Page, string Type, int Width, int Height);
}
