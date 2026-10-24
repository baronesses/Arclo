using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Orbit.Core.Documents;

namespace Orbit.Core.Media;

/// <summary>Exports the displayed video frame at start+n/fps, while that timestamp precedes the selected end.</summary>
public sealed class VideoFramesEngine
{
    public const long MaximumSourceBytes = 16L * 1024 * 1024 * 1024;
    public const int MaximumFrames = 300;
    public const int MaximumDimension = 1920;
    public const long MaximumFrameBytes = 32L * 1024 * 1024;
    public const long MaximumTotalBytes = 512L * 1024 * 1024;
    private const string Prefix = ".orbit-video-frames-";
    private readonly MediaEngine _media;
    private readonly TimeSpan _timeout;
    internal Action<int>? ProcessStarted { get; set; }

    public VideoFramesEngine(string? ffmpegExecutable = null, TimeSpan? operationTimeout = null)
    {
        _timeout = operationTimeout ?? TimeSpan.FromMinutes(10);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        _media = new MediaEngine(ffmpegExecutable);
    }

    public string ExecutablePath => _media.ExecutablePath;
    public string ProbeExecutablePath => _media.ProbeExecutablePath;
    public bool Available => File.Exists(ExecutablePath) && File.Exists(ProbeExecutablePath);

    public async Task<MediaResult> ExtractAsync(string path, VideoFramesOptions options, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);ct.ThrowIfCancellationRequested();
        if (!double.IsFinite(options.StartSeconds) || options.StartSeconds < 0 || !double.IsFinite(options.DurationSeconds)
            || options.DurationSeconds <= 0 || options.DurationSeconds > 30 || !double.IsFinite(options.StartSeconds + options.DurationSeconds))
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_2E61D431DF3E"));
        if (options.FramesPerSecond is < 1 or > 10) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_9B218B9063B8"));
        if (options.Width is < 160 or > MaximumDimension) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_ABB7AD5E67CB"));
        var expected = (int)Math.Ceiling(options.DurationSeconds * options.FramesPerSecond);
        if (expected is < 1 or > MaximumFrames) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_AF885D65FE42"));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(_timeout);
        var token = deadline.Token;string? temporary = null, directory = null;
        try
        {
            var sourcePath = NormalizePath(path);
            await using var held = await OpenInputAsync(sourcePath, token).ConfigureAwait(false);
            var metadata = await _media.InspectAsync(sourcePath, token).WaitAsync(token).ConfigureAwait(false);
            if (metadata.Kind != "Video") throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_0D2350B2B0BE"));
            var video = await ReadVideoAsync(sourcePath, token).ConfigureAwait(false);
            if (video.Duration <= 0 || video.Duration > 86400 || metadata.DurationSeconds > 86400 || options.StartSeconds + options.DurationSeconds > video.Duration)
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_B7E1E18BC694"));
            var (width, height) = OutputSize(video, options.Width);
            directory = string.IsNullOrWhiteSpace(options.OutputDirectory) ? Path.GetDirectoryName(sourcePath)! : Path.GetFullPath(options.OutputDirectory);
            if (!await Task.Run(() => Directory.Exists(directory), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false))
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_099F7AFF3299"));
            temporary = Path.Combine(directory, Prefix + Guid.NewGuid().ToString("N"));var working = temporary;var parent = directory;
            var creating = Task.Run(() => Directory.CreateDirectory(working), CancellationToken.None);
            try { await creating.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); }
            catch
            {
                temporary = null;
                _ = creating.ContinueWith(async task => { if (task.IsCompletedSuccessfully) await CleanupAsync(working, parent).ConfigureAwait(false);else _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();throw;
            }
            var payload = Path.Combine(temporary, "frames");Directory.CreateDirectory(payload);
            var pattern = Path.Combine(payload.Replace("%", "%%", StringComparison.Ordinal), "frame_%04d.png");
            var fps = options.FramesPerSecond.ToString(CultureInfo.InvariantCulture);var count = expected.ToString(CultureInfo.InvariantCulture);
            // Retain preroll frames with their negative timestamps so fps:round=up selects
            // the image already displayed at t=0, even when the requested start is between source frames.
            // Do not combine input -t with -noaccurate_seek: FFmpeg counts it from the seek keyframe.
            // The verified stream duration bounds the interval; the explicit frame limit bounds output.
            var filter = $"tpad=stop_mode=clone:stop=-1,fps=fps={fps}:start_time=0:round=up:eof_action=pass,scale={width}:{height}:flags=lanczos,setsar=1";
            var args = new List<string> { "-hide_banner", "-v", "error", "-nostdin", "-n", "-nostats", "-xerror", "-threads", "2", "-filter_threads", "1",
                "-protocol_whitelist", "file,pipe", "-ss", Number(options.StartSeconds + video.RelativeStart), "-noaccurate_seek", "-i", sourcePath,
                "-map", "0:" + video.Index.ToString(CultureInfo.InvariantCulture), "-an", "-sn", "-dn", "-vf", filter, "-frames:v", count,
                "-fps_mode", "passthrough", "-c:v", "png", "-pix_fmt", "rgb24", "-threads:v", "2", "-map_metadata", "-1", "-map_chapters", "-1",
                "-f", "image2", "-start_number", "1", "-progress", "pipe:1", "-stats_period", "0.1", pattern };
            double last = 0;progress?.Report(0);Recheck(sourcePath, token);
            await RunAsync(ExecutablePath, args, token, payload, line =>
            {
                if (!line.StartsWith("frame=", StringComparison.Ordinal) || !int.TryParse(line.AsSpan(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out var done)) return;
                var value = .95 * Math.Clamp(done / (double)expected, 0, 1);if (value > last) { last = value;progress?.Report(value); }
            }, encoder: true).ConfigureAwait(false);
            var files = Directory.GetFiles(payload);
            if (files.Length != expected || Directory.GetDirectories(payload).Length != 0) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_9157D54EC3B9"));
            long total = 0;
            for (var index = 1; index <= expected; index++)
            {
                token.ThrowIfCancellationRequested();var frame = Path.Combine(payload, "frame_" + index.ToString("D4", CultureInfo.InvariantCulture) + ".png");
                if (!File.Exists(frame) || (File.GetAttributes(frame) & FileAttributes.ReparsePoint) != 0) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_E008FE199BD7"));
                await using var input = new FileStream(frame, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
                if (input.Length is < 45 or > MaximumFrameBytes) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_66722D183316"));
                total = checked(total + input.Length);if (total > MaximumTotalBytes) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_4AE7E15ADB0E"));
                var header = new byte[29];await input.ReadExactlyAsync(header, token).ConfigureAwait(false);
                if (!header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) || !header.AsSpan(12, 4).SequenceEqual("IHDR"u8)
                    || BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16)) != width || BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20)) != height || header[24] != 8 || header[25] != 2)
                    throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_FDD05AFA0323"));
            }
            await ValidateFramesAsync(pattern, expected, width, height, options.FramesPerSecond, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();var target = DocumentFiles.CommitDirectory(payload, directory, ShortStem(sourcePath) + "_frames", token);progress?.Report(1);
            return new MediaResult(target, total, width, height, "FOLDER", held.Length);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_0938FF7D443A")); }
        catch (TimeoutException exception) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_28EA58092647"), exception); }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_993BA632CD52"), exception); }
        catch (UnauthorizedAccessException exception) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_9C5103CC58FB"), exception); }
        catch (IOException exception) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_FA74D4EC41D0"), exception); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or System.Security.SecurityException or OverflowException)
        { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_647F06B66B2E"), exception); }
        finally { if (temporary is not null && directory is not null) await CleanupAsync(temporary, directory).ConfigureAwait(false); }
    }

    private async Task<VideoInfo> ReadVideoAsync(string path, CancellationToken token)
    {
        var text = await RunAsync(ProbeExecutablePath, ["-v", "error", "-protocol_whitelist", "file,pipe", "-show_entries",
            "format=start_time:stream=index,codec_type,codec_name,width,height,duration,start_time,sample_aspect_ratio,avg_frame_rate:stream_disposition=attached_pic:stream_tags=rotate,DURATION,duration:stream_side_data=rotation", "-of", "json", "-i", path], token).ConfigureAwait(false);
        try
        {
            using var json = JsonDocument.Parse(text);var root = json.RootElement;var origin = root.TryGetProperty("format", out var format) ? Numeric(format, "start_time") : 0;
            foreach (var stream in root.GetProperty("streams").EnumerateArray())
            {
                if (Text(stream, "codec_type") != "video" || Text(stream, "codec_name") is "" or "unknown" || stream.TryGetProperty("disposition", out var disposition) && Integer(disposition, "attached_pic") != 0) continue;
                var rotation = stream.TryGetProperty("tags", out var tags) ? Numeric(tags, "rotate") : 0;
                if (stream.TryGetProperty("side_data_list", out var sides)) foreach (var side in sides.EnumerateArray()) if (side.TryGetProperty("rotation", out _)) { rotation = Numeric(side, "rotation");break; }
                return new VideoInfo(Integer(stream, "index"), Integer(stream, "width"), Integer(stream, "height"), Ratio(Text(stream, "sample_aspect_ratio"), 1), rotation,
                    Ratio(Text(stream, "avg_frame_rate"), 0), StreamDuration(stream), Math.Max(0, Numeric(stream, "start_time") - origin));
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_F016B5E6D53D"), exception); }
        throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_4B7296134FBA"));
    }

    private async Task ValidateFramesAsync(string pattern, int count, int width, int height, int fps, CancellationToken token)
    {
        var text = await RunAsync(ProbeExecutablePath, ["-v", "error", "-err_detect", "explode", "-protocol_whitelist", "file,pipe", "-f", "image2", "-framerate", fps.ToString(CultureInfo.InvariantCulture),
            "-start_number", "1", "-start_number_range", "1", "-count_frames", "-show_frames", "-show_entries", "stream=codec_type,codec_name,width,height,pix_fmt,nb_read_frames:frame=media_type,width,height,pix_fmt", "-of", "json", "-i", pattern], token).ConfigureAwait(false);
        try
        {
            using var json = JsonDocument.Parse(text);var root = json.RootElement;var streams = root.GetProperty("streams");var frames = root.GetProperty("frames");
            if (streams.GetArrayLength() != 1 || Text(streams[0], "codec_name") != "png" || Integer(streams[0], "nb_read_frames") != count || frames.GetArrayLength() != count) throw new JsonException();
            foreach (var frame in frames.EnumerateArray()) if (Text(frame, "media_type") != "video" || Integer(frame, "width") != width || Integer(frame, "height") != height || Text(frame, "pix_fmt") != "rgb24") throw new JsonException();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_944300A6F0F5"), exception); }
    }

    private static (int Width, int Height) OutputSize(VideoInfo video, int requested)
    {
        if (video.Width is < 1 or > 16000 || video.Height is < 1 or > 16000 || (long)video.Width * video.Height > 40_000_000 || video.Fps > 240)
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_72A04F937A82"));
        if (!double.IsFinite(video.Sar) || video.Sar is <= 0 or > 10000 || Math.Abs(video.Rotation / 90 - Math.Round(video.Rotation / 90)) > .001)
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_E85B7A18DC86"));
        var width = video.Width * video.Sar;var height = (double)video.Height;
        if (Math.Abs(Math.Round(video.Rotation / 90) % 2) == 1) (width, height) = (height, width);
        var factor = Math.Min(1, Math.Min(requested / width, MaximumDimension / height));
        var w = (int)Math.Floor(width * factor + .0000001);var h = (int)Math.Floor(height * factor + .0000001);
        if (w < 1 || h < 1 || w > MaximumDimension || h > MaximumDimension || (long)w * h > 4_000_000) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_11705B69C340"));
        return (w, h);
    }

    private async Task<string> RunAsync(string executable, IReadOnlyList<string> args, CancellationToken token, string? watched = null, Action<string>? line = null, bool encoder = false)
    {
        token.ThrowIfCancellationRequested();var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in args) start.ArgumentList.Add(argument);using var process = new Process { StartInfo = start };
        try { if (!process.Start()) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_424AE4379E18")); }
        catch (System.ComponentModel.Win32Exception exception) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_53CA7B3E1CB7"), exception); }
        var output = DrainAsync(process.StandardOutput, 2 * 1024 * 1024, line);var error = DrainAsync(process.StandardError, 128 * 1024);
        using var watchCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);var monitoring = watched is null ? Task.Delay(Timeout.Infinite, watchCancellation.Token) : WatchAsync(watched, watchCancellation.Token);
        try
        {
            if (encoder) ProcessStarted?.Invoke(process.Id);var exit = process.WaitForExitAsync(token);
            if (await Task.WhenAny(exit, monitoring).ConfigureAwait(false) == monitoring) await monitoring.ConfigureAwait(false);
            await exit.ConfigureAwait(false);await Task.WhenAll(output, error).WaitAsync(token).ConfigureAwait(false);
            var text = await output.ConfigureAwait(false);var errors = await error.ConfigureAwait(false);token.ThrowIfCancellationRequested();
            if (text.Length > 2 * 1024 * 1024 || errors.Length > 128 * 1024) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_F7BBB962012F"));
            if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(errors)) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_5039CE1FFF9D") + errors.Trim()[..Math.Min(700, errors.Trim().Length)]);
            return text;
        }
        finally
        {
            watchCancellation.Cancel();try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (Exception exception) when (exception is TimeoutException or InvalidOperationException) { }
            var readers = Task.WhenAll(output, error);
            try { await readers.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (TimeoutException) { process.StandardOutput.Dispose();process.StandardError.Dispose();Observe(readers); }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
            Observe(monitoring);
        }
    }

    private static Task WatchAsync(string directory, CancellationToken token) => Task.Run(async () =>
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();long total = 0;var count = 0;
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                token.ThrowIfCancellationRequested();var bytes = new FileInfo(path).Length;total = checked(total + bytes);
                if (++count > MaximumFrames || bytes > MaximumFrameBytes || total > MaximumTotalBytes) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_7507F224EB71"));
            }
            await Task.Delay(100, token).ConfigureAwait(false);
        }
    }, CancellationToken.None);

    private static async Task<string> DrainAsync(StreamReader reader, int maximum, Action<string>? callback = null)
    {
        var output = new StringBuilder();var current = new StringBuilder();var buffer = new char[4096];int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            if (output.Length <= maximum) output.Append(buffer, 0, Math.Min(read, maximum + 1 - output.Length));
            if (callback is null) continue;
            for (var index = 0; index < read; index++) if (buffer[index] == '\n') { callback(current.ToString().TrimEnd('\r'));current.Clear(); } else if (current.Length < 4096) current.Append(buffer[index]);
        }
        return output.ToString();
    }
    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_76A6E3A670B1"));
        var full = Path.GetFullPath(path);if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal) || full.IndexOf(':', full.Length > 1 && full[1] == ':' ? 2 : 0) >= 0) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_D3926B083BA4"));return full;
    }
    private static void Recheck(string path, CancellationToken token)
    {
        var depth = 0;for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            token.ThrowIfCancellationRequested();if (++depth > 256) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_D4AFA3292751"));
            var attributes = File.GetAttributes(current);if ((attributes & FileAttributes.ReparsePoint) != 0 || depth == 1 && (attributes & FileAttributes.Directory) != 0) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_BBF2241C083F"));
        }
    }
    private static async Task<FileStream> OpenInputAsync(string path, CancellationToken token)
    {
        var pending = Task.Run(() =>
        {
            Recheck(path, token);var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            try { Recheck(path, token);if ((File.GetAttributes(source.SafeFileHandle) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || source.Length is <= 0 or > MaximumSourceBytes) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_A58FC0D1B3A2"));return source; }
            catch { source.Dispose();throw; }
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
                try { if (!Directory.Exists(directory)) return;if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;Directory.Delete(directory, true);return; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                await Task.Delay(Math.Min(400, 25 << Math.Min(attempt, 4))).ConfigureAwait(false);
            }
        }, CancellationToken.None);
        try { await deleting.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (TimeoutException) { Observe(deleting); }
    }
    private static void Observe(Task task) => _ = task.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private static string ShortStem(string path) { var stem = Path.GetFileNameWithoutExtension(path);if (stem.Length <= 180) return stem;stem = stem[..180];return char.IsHighSurrogate(stem[^1]) ? stem[..^1] : stem; }
    private static string Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) ? value.ToString() : "";
    private static int Integer(JsonElement item, string name) => int.TryParse(Text(item, name), CultureInfo.InvariantCulture, out var number) ? number : 0;
    private static double Numeric(JsonElement item, string name) => double.TryParse(Text(item, name), CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : 0;
    private static double Ratio(string value, double fallback) { var fields = value.Split([':', '/']);return fields.Length == 2 && double.TryParse(fields[0], CultureInfo.InvariantCulture, out var a) && double.TryParse(fields[1], CultureInfo.InvariantCulture, out var b) && a > 0 && b > 0 ? a / b : fallback; }
    private static double StreamDuration(JsonElement item)
    {
        var duration = Numeric(item, "duration");if (duration > 0) return duration;if (!item.TryGetProperty("tags", out var tags)) return 0;
        var text = Text(tags, "DURATION");if (text.Length == 0) text = Text(tags, "duration");var fields = text.Split(':');
        if (fields.Length != 3 || !double.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) || !double.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || !double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || hours < 0 || minutes is < 0 or >= 60 || seconds is < 0 or >= 60) return 0;
        duration = hours * 3600 + minutes * 60 + seconds - Numeric(item, "start_time");return double.IsFinite(duration) && duration > 0 ? duration : 0;
    }
    private static string Number(double value) => value.ToString("0.################", CultureInfo.InvariantCulture);
    private sealed record VideoInfo(int Index, int Width, int Height, double Sar, double Rotation, double Fps, double Duration, double RelativeStart);
}
