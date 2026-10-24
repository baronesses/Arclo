using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Orbit.Core.Documents;

namespace Orbit.Core.Media;

/// <summary>Copies the first attached picture of a true audio file into a new losslessly compressed PNG.
/// The PNG contains decoded cover pixels, never the original audio, container tags, or encoded JPEG stream.</summary>
public sealed class MediaCoverEngine
{
    public const long MaximumSourceBytes = 2L * 1024 * 1024 * 1024;
    public const long MaximumOutputBytes = 128L * 1024 * 1024;
    public const int MaximumPixels = 40_000_000;
    public const int MaximumDimension = 16000;
    private const string Prefix = ".orbit-media-cover-";
    private readonly MediaEngine _media;
    private readonly TimeSpan _timeout;
    internal Action<int>? ProcessStarted { get; set; }

    public MediaCoverEngine(string? ffmpegExecutable = null, TimeSpan? operationTimeout = null)
    {
        _media = new MediaEngine(ffmpegExecutable);_timeout = operationTimeout ?? TimeSpan.FromMinutes(3);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
    }
    public string ExecutablePath => _media.ExecutablePath;
    public string ProbeExecutablePath => _media.ProbeExecutablePath;
    public bool Available => File.Exists(ExecutablePath) && File.Exists(ProbeExecutablePath);

    public async Task<MediaResult> ExtractAsync(string path, MediaCoverOptions options, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);ct.ThrowIfCancellationRequested();using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(_timeout);
        var token = deadline.Token;string? temporary = null, parent = null;
        try
        {
            var source = NormalizePath(path);progress?.Report(0);
            await using var held = await OpenInputAsync(source, token).ConfigureAwait(false);
            var cover = await ReadSourceAsync(source, token).ConfigureAwait(false);progress?.Report(.2);
            // Require real decoded audio samples, without decoding hours of unrelated audio to extract one picture.
            var audio = Input(source);audio.InsertRange(audio.Count - 2, ["-skip_frame:v", "all"]);
            audio.AddRange(["-map", "0:" + cover.AudioIndex.ToString(CultureInfo.InvariantCulture), "-vn", "-sn", "-dn", "-frames:a", "1", "-af",
                "astats=metadata=1:reset=1:measure_perchannel=none:measure_overall=Number_of_samples,ametadata=print:key=lavfi.astats.Overall.Number_of_samples:file=-:direct=1", "-c:a", "pcm_s16le", "-f", "null", "-"]);
            var sampleReport = await RunAsync(ExecutablePath, audio, token).ConfigureAwait(false);
            if (!sampleReport.Split('\n').Any(line => line.StartsWith("lavfi.astats.Overall.Number_of_samples=", StringComparison.Ordinal)
                && double.TryParse(line.AsSpan("lavfi.astats.Overall.Number_of_samples=".Length), CultureInfo.InvariantCulture, out var samples) && samples > 0))
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_AC233CCF11C9"));
            progress?.Report(.35);
            parent = Path.TrimEndingDirectorySeparator(string.IsNullOrWhiteSpace(options.OutputDirectory) ? Path.GetDirectoryName(source)! : Path.GetFullPath(options.OutputDirectory));
            if (!await Task.Run(() => Directory.Exists(parent), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false))
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_0CB6216CB7EB"));
            temporary = Path.Combine(parent, Prefix + Guid.NewGuid().ToString("N"));var working = temporary;var directory = parent;
            var creating = Task.Run(() => Directory.CreateDirectory(working), CancellationToken.None);
            try { await creating.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); }
            catch
            {
                temporary = null;
                _ = creating.ContinueWith(async task => { if (task.IsCompletedSuccessfully) await CleanupAsync(working, directory).ConfigureAwait(false);else _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();throw;
            }
            var prepared = Path.Combine(temporary, "cover.png");var args = Input(source);
            args.AddRange(["-map", "0:" + cover.Index.ToString(CultureInfo.InvariantCulture), "-an", "-sn", "-dn", "-frames:v", "1", "-vf", "setsar=1,metadata=mode=delete,sidedata=mode=delete",
                "-c:v", "png", "-threads:v", "1", "-flags:v", "+bitexact", "-fflags", "+bitexact", "-map_metadata", "-1", "-map_chapters", "-1", "-metadata:s:v:0", "rotate=0",
                "-fs", (MaximumOutputBytes + 1).ToString(CultureInfo.InvariantCulture), "-f", "image2pipe", prepared]);
            Recheck(source, token);await RunAsync(ExecutablePath, args, token, temporary, encoder: true).ConfigureAwait(false);progress?.Report(.8);
            var bytes = new FileInfo(prepared).Length;
            if (bytes is < 45 or > MaximumOutputBytes || Directory.GetFiles(temporary).Length != 1 || Directory.GetDirectories(temporary).Length != 0)
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_16C539AF4E19"));
            await using (var png = new FileStream(prepared, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous))
            {
                var header = new byte[24];await png.ReadExactlyAsync(header, token).ConfigureAwait(false);
                if (!header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) || !header.AsSpan(12, 4).SequenceEqual("IHDR"u8)
                    || BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16)) != cover.Width || BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20)) != cover.Height)
                    throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_9CCD590A8C67"));
            }
            await ValidatePngAsync(prepared, cover, token).ConfigureAwait(false);progress?.Report(.95);token.ThrowIfCancellationRequested();
            var target = DocumentFiles.CommitFile(prepared, parent, ShortStem(source) + "_cover.png", token);progress?.Report(1);
            return new MediaResult(target, bytes, cover.Width, cover.Height, "PNG", held.Length);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_75E173EB65C1")); }
        catch (TimeoutException exception) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_51DC48FE0AFA"), exception); }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_B48B50E90495"), exception); }
        catch (UnauthorizedAccessException exception) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_15E9CB710143"), exception); }
        catch (IOException exception) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_F6991761FB44"), exception); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or System.Security.SecurityException or OverflowException)
        { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_EE3CF9DE6A3D"), exception); }
        finally { if (temporary is not null && parent is not null) await CleanupAsync(temporary, parent).ConfigureAwait(false); }
    }

    private async Task<CoverInfo> ReadSourceAsync(string path, CancellationToken token)
    {
        // max_pixels is a native decoder guard even during stream-info probing; skip_frame
        // avoids decoding attached pictures before their dimensions have been checked.
        var text = await RunAsync(ProbeExecutablePath, ["-v", "error", "-max_alloc", "536870912", "-max_pixels", MaximumPixels.ToString(CultureInfo.InvariantCulture), "-threads", "2", "-skip_frame", "all",
            "-protocol_whitelist", "file,pipe", "-show_entries", "format=format_name,duration:stream=index,codec_type,codec_name,width,height,sample_rate,channels,duration,start_time:stream_disposition=attached_pic:stream_tags=rotate,DURATION,duration:stream_side_data=rotation", "-of", "json", "-i", path], token).ConfigureAwait(false);
        try
        {
            using var json = JsonDocument.Parse(text);var root = json.RootElement;var format = root.GetProperty("format");var names = Text(format, "format_name").Split(',');
            var allowed = new[] { "mp3", "flac", "mov", "mp4", "m4a", "3gp", "3g2", "mj2", "wav", "aac", "ogg", "aiff", "asf", "matroska", "webm", "avi", "mpeg", "mpegts" };
            if (!names.Any(allowed.Contains)) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_74196E529387"));
            if (Numeric(format, "duration") > 86400) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_93DD594C381B"));
            JsonElement? audio = null, cover = null;
            foreach (var stream in root.GetProperty("streams").EnumerateArray())
            {
                var kind = Text(stream, "codec_type");var attached = stream.TryGetProperty("disposition", out var disposition) && Integer(disposition, "attached_pic") != 0;
                if (kind == "video" && !attached) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_A4FDD0470784"));
                if (kind == "audio" && StreamDuration(stream) > 86400) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_24A04FA004A9"));
                if (kind == "audio" && (audio is null || Integer(stream, "index") < Integer(audio.Value, "index"))) audio = stream;
                if (kind == "video" && attached && (cover is null || Integer(stream, "index") < Integer(cover.Value, "index"))) cover = stream;
            }
            if (audio is not { } first || Text(first, "codec_name") is "" or "unknown" || Integer(first, "channels") is < 1 or > 8 || Integer(first, "sample_rate") <= 0)
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_9FF170DDF8BD"));
            var duration = StreamDuration(first);if (duration <= 0) duration = Numeric(format, "duration");
            if (duration is <= 0 or > 86400 || !double.IsFinite(duration)) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_D49E88D2F55E"));
            if (cover is not { } picture) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_D9D7CFAD32E9"));
            var width = Integer(picture, "width");var height = Integer(picture, "height");
            if (Text(picture, "codec_name") is "" or "unknown" || width is < 1 or > MaximumDimension || height is < 1 or > MaximumDimension || (long)width * height > MaximumPixels)
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_CCADF74DD165"));
            var rotation = picture.TryGetProperty("tags", out var tags) ? Numeric(tags, "rotate") : 0;
            if (picture.TryGetProperty("side_data_list", out var sides)) foreach (var side in sides.EnumerateArray()) if (side.TryGetProperty("rotation", out _)) { rotation = Numeric(side, "rotation");break; }
            if (Math.Abs(rotation / 90 - Math.Round(rotation / 90)) > .001) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_B0A79BFB8C39"));
            if (Math.Abs(Math.Round(rotation / 90) % 2) == 1) (width, height) = (height, width);
            return new CoverInfo(Integer(picture, "index"), Integer(first, "index"), width, height);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_6CA3D616AF19"), exception); }
    }

    private async Task ValidatePngAsync(string path, CoverInfo expected, CancellationToken token)
    {
        var text = await RunAsync(ProbeExecutablePath, ["-v", "error", "-err_detect", "explode", "-max_alloc", "536870912", "-max_pixels", MaximumPixels.ToString(CultureInfo.InvariantCulture),
            "-threads", "2", "-protocol_whitelist", "file,pipe", "-count_frames", "-show_frames", "-show_entries", "stream=codec_type,codec_name,width,height,nb_read_frames:frame=media_type,width,height", "-of", "json", "-i", path], token).ConfigureAwait(false);
        try
        {
            using var json = JsonDocument.Parse(text);var root = json.RootElement;var streams = root.GetProperty("streams");var frames = root.GetProperty("frames");
            if (streams.GetArrayLength() != 1 || Text(streams[0], "codec_name") != "png" || Integer(streams[0], "nb_read_frames") != 1 || frames.GetArrayLength() != 1
                || Text(frames[0], "media_type") != "video" || Integer(frames[0], "width") != expected.Width || Integer(frames[0], "height") != expected.Height) throw new JsonException();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_49F9A10E7167"), exception); }
    }

    private static List<string> Input(string path) => ["-hide_banner", "-v", "error", "-nostdin", "-n", "-nostats", "-xerror", "-err_detect", "explode", "-max_alloc", "536870912", "-max_pixels", MaximumPixels.ToString(CultureInfo.InvariantCulture),
        "-threads", "2", "-filter_threads", "1", "-protocol_whitelist", "file,pipe", "-i", path];

    private async Task<string> RunAsync(string executable, IReadOnlyList<string> args, CancellationToken token, string? watched = null, bool encoder = false)
    {
        token.ThrowIfCancellationRequested();var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in args) start.ArgumentList.Add(argument);using var process = new Process { StartInfo = start };
        try { if (!process.Start()) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_743981143DAD")); }
        catch (System.ComponentModel.Win32Exception exception) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_53CA7B3E1CB7"), exception); }
        var output = DrainAsync(process.StandardOutput, 2 * 1024 * 1024);var error = DrainAsync(process.StandardError, 128 * 1024);
        using var watchCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);var monitoring = watched is null ? Task.Delay(Timeout.Infinite, watchCancellation.Token) : WatchAsync(watched, watchCancellation.Token);
        try
        {
            if (encoder) ProcessStarted?.Invoke(process.Id);var exit = process.WaitForExitAsync(token);
            if (await Task.WhenAny(exit, monitoring).ConfigureAwait(false) == monitoring) await monitoring.ConfigureAwait(false);
            await exit.ConfigureAwait(false);await Task.WhenAll(output, error).WaitAsync(token).ConfigureAwait(false);var text = await output.ConfigureAwait(false);var errors = await error.ConfigureAwait(false);token.ThrowIfCancellationRequested();
            if (text.Length > 2 * 1024 * 1024 || errors.Length > 128 * 1024) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_95DE1D5AEA2C"));
            if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(errors)) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_B70BD93029F5") + errors.Trim()[..Math.Min(700, errors.Trim().Length)]);
            return text;
        }
        finally
        {
            watchCancellation.Cancel();try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (Exception exception) when (exception is TimeoutException or InvalidOperationException) { }
            var readers = Task.WhenAll(output, error);try { await readers.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (TimeoutException) { process.StandardOutput.Dispose();process.StandardError.Dispose();Observe(readers); } catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
            Observe(monitoring);
        }
    }
    private static Task WatchAsync(string directory, CancellationToken token) => Task.Run(async () =>
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();var count = 0;
            foreach (var path in Directory.EnumerateFiles(directory)) { token.ThrowIfCancellationRequested();if (++count > 1 || new FileInfo(path).Length > MaximumOutputBytes) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_4F418F640D29")); }
            await Task.Delay(100, token).ConfigureAwait(false);
        }
    }, CancellationToken.None);
    private static async Task<string> DrainAsync(StreamReader reader, int maximum)
    { var result = new StringBuilder();var buffer = new char[4096];int read;while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0) if (result.Length <= maximum) result.Append(buffer, 0, Math.Min(read, maximum + 1 - result.Length));return result.ToString(); }
    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_92E785780165"));
        var full = Path.GetFullPath(path);var extension = Path.GetExtension(full).ToLowerInvariant();
        var allowed = new[] { ".mp3", ".flac", ".m4a", ".wav", ".aac", ".ogg", ".opus", ".aif", ".aiff", ".wma", ".mkv", ".mka", ".webm", ".mp4", ".mov", ".m4v", ".avi", ".mpeg", ".mpg", ".ts" };
        if (!allowed.Contains(extension) || full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal) || full.IndexOf(':', full.Length > 1 && full[1] == ':' ? 2 : 0) >= 0)
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_7E90A8FB9D7F"));return full;
    }
    private static void Recheck(string path, CancellationToken token)
    {
        var depth = 0;for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            token.ThrowIfCancellationRequested();if (++depth > 256) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_D17EF523382C"));var flags = File.GetAttributes(current);
            if ((flags & FileAttributes.ReparsePoint) != 0 || depth == 1 && (flags & FileAttributes.Directory) != 0) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_D3A6E2F1C4E8"));
        }
    }
    private static async Task<FileStream> OpenInputAsync(string path, CancellationToken token)
    {
        var pending = Task.Run(() =>
        {
            Recheck(path, token);var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            try { Recheck(path, token);if ((File.GetAttributes(input.SafeFileHandle) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || input.Length is <= 0 or > MaximumSourceBytes) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_AB804ADE40EA"));return input; }
            catch { input.Dispose();throw; }
        }, CancellationToken.None);
        try { return await pending.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); }
        catch { _ = pending.ContinueWith(task => { if (task.IsCompletedSuccessfully) task.Result.Dispose();else _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);throw; }
    }
    private static async Task CleanupAsync(string directory, string parent)
    {
        if (!string.Equals(Path.GetDirectoryName(directory), Path.TrimEndingDirectorySeparator(parent), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(directory).StartsWith(Prefix, StringComparison.Ordinal)) return;
        var deleting = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 24; attempt++)
            {
                try { if (!Directory.Exists(directory)) return;if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;Directory.Delete(directory, true);return; } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                await Task.Delay(Math.Min(400, 25 << Math.Min(attempt, 4))).ConfigureAwait(false);
            }
        }, CancellationToken.None);
        try { await deleting.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (TimeoutException) { Observe(deleting); }
    }
    private static void Observe(Task task) => _ = task.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private static string ShortStem(string path) { var stem = Path.GetFileNameWithoutExtension(path);if (stem.Length <= 180) return stem;stem = stem[..180];return char.IsHighSurrogate(stem[^1]) ? stem[..^1] : stem; }
    private static string Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) ? value.ToString() : "";
    private static int Integer(JsonElement item, string name) => int.TryParse(Text(item, name), CultureInfo.InvariantCulture, out var value) ? value : 0;
    private static double Numeric(JsonElement item, string name) => double.TryParse(Text(item, name), CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : 0;
    private static double StreamDuration(JsonElement item)
    {
        var duration = Numeric(item, "duration");if (duration > 0) return duration;if (!item.TryGetProperty("tags", out var tags)) return 0;var text = Text(tags, "DURATION");if (text.Length == 0) text = Text(tags, "duration");var fields = text.Split(':');
        if (fields.Length != 3 || !double.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) || !double.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) || !double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || hours < 0 || minutes is < 0 or >= 60 || seconds is < 0 or >= 60) return 0;duration = hours * 3600 + minutes * 60 + seconds - Numeric(item, "start_time");return double.IsFinite(duration) && duration > 0 ? duration : 0;
    }
    private sealed record CoverInfo(int Index, int AudioIndex, int Width, int Height);
}
