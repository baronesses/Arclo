using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Orbit.Core.Media;

/// <summary>Builds bounded private playback copies; source files and public result folders are never modified.</summary>
public sealed class MediaPreviewEngine
{
    public const double MaximumDurationSeconds = 30;
    public const long MaximumSourceBytes = 16L * 1024 * 1024 * 1024;
    public const long MaximumVideoBytes = 64L * 1024 * 1024;
    public const long MaximumAudioBytes = 32L * 1024 * 1024;
    private readonly MediaEngine _media;
    private readonly TimeSpan _timeout;
    internal Action<int>? ProcessStarted { get; set; }

    public MediaPreviewEngine(string? ffmpegExecutable = null, TimeSpan? operationTimeout = null)
    {
        _timeout = operationTimeout ?? TimeSpan.FromMinutes(3);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        _media = new MediaEngine(ffmpegExecutable);
    }

    public string ExecutablePath => _media.ExecutablePath;
    public string ProbeExecutablePath => _media.ProbeExecutablePath;

    public async Task<MediaPreviewResult> BuildAsync(string path, MediaPreviewOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);ct.ThrowIfCancellationRequested();
        if (!double.IsFinite(options.StartSeconds) || options.StartSeconds < 0 || !double.IsFinite(options.DurationSeconds)
            || options.DurationSeconds <= 0 || options.DurationSeconds > MaximumDurationSeconds || !double.IsFinite(options.StartSeconds + options.DurationSeconds))
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_467363F5C860"));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(_timeout);
        var token = deadline.Token;string? temporary = null, parent = null;
        try
        {
            var fullPath = NormalizePath(path);
            await using var lease = await OpenInputAsync(fullPath, token).ConfigureAwait(false);
            var metadata = await _media.InspectAsync(fullPath, token).WaitAsync(token).ConfigureAwait(false);
            var source = await ProbeAsync(fullPath, token).ConfigureAwait(false);
            var video = metadata.Kind == "Video";
            if (metadata.Kind is not ("Video" or "Audio") || video && source.Video is null || !video && source.Audio is null)
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_B22566718D5A"));
            var duration = video ? source.Video!.Duration : source.Audio!.Duration;
            if (duration <= 0) duration = metadata.DurationSeconds;
            var end = options.StartSeconds + options.DurationSeconds;
            if (!double.IsFinite(duration) || duration <= 0 || duration > 86400 || end > duration + .000001)
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_634730166704"));
            if (source.Audio is { } audio && (audio.Channels is < 1 or > 8 || audio.Rate is < 1 or > 192000))
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_C15E116AE971"));
            var (width, height) = video ? OutputSize(source.Video!) : (0, 0);
            parent = string.IsNullOrWhiteSpace(options.OutputDirectory) ? Path.GetTempPath() : Path.GetFullPath(options.OutputDirectory);
            if (!await Task.Run(() => Directory.Exists(parent), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false))
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_918B904D8626"));
            temporary = Path.Combine(parent, MediaPreviewFiles.Prefix + Guid.NewGuid().ToString("N"));
            var creatingPath = temporary;var creatingParent = parent;
            var creating = Task.Run(() => Directory.CreateDirectory(creatingPath), CancellationToken.None);
            try { await creating.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); }
            catch
            {
                temporary = null;
                _ = creating.ContinueWith(async task =>
                { if (task.IsCompletedSuccessfully) await MediaPreviewFiles.AwaitCleanupAsync(MediaPreviewFiles.StartCleanup(creatingPath, creatingParent)).ConfigureAwait(false);else _ = task.Exception; },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
                throw;
            }
            var prepared = Path.Combine(temporary, video ? "preview.mp4" : "preview.wav");
            var args = new List<string> { "-hide_banner", "-v", "error", "-nostdin", "-n", "-nostats", "-xerror", "-threads", "2", "-filter_threads", "1",
                "-protocol_whitelist", "file,pipe", "-ss", Number(options.StartSeconds), "-t", Number(options.DurationSeconds), "-i", fullPath, "-sn", "-dn" };
            var keepAudio = source.Audio is { } track && (!video || track.Duration <= 0 || end > track.Start && options.StartSeconds < track.Start + track.Duration);
            if (video)
            {
                args.AddRange(["-map", "0:" + source.Video!.Index.ToString(CultureInfo.InvariantCulture), "-vf",
                    "trim=duration=" + Number(options.DurationSeconds) + ",setpts=PTS-STARTPTS,fps=30,scale=" + width.ToString(CultureInfo.InvariantCulture) + ":" + height.ToString(CultureInfo.InvariantCulture) + ":flags=bilinear,setsar=1",
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "27", "-maxrate", "1500k", "-bufsize", "3000k", "-pix_fmt", "yuv420p", "-threads:v", "2"]);
                if (keepAudio) args.AddRange(["-map", "0:" + source.Audio!.Index.ToString(CultureInfo.InvariantCulture), "-af", "atrim=duration=" + Number(options.DurationSeconds), "-c:a", "aac", "-b:a", "96k", "-ac", "2", "-ar", "48000"]);
                else args.Add("-an");
                args.AddRange(["-metadata:s:v:0", "rotate=0", "-movflags", "+faststart"]);
            }
            else args.AddRange(["-map", "0:" + source.Audio!.Index.ToString(CultureInfo.InvariantCulture), "-vn", "-af", "atrim=duration=" + Number(options.DurationSeconds) + ",asetpts=PTS-STARTPTS", "-c:a", "pcm_s16le", "-ar", "48000", "-ac", "2"]);
            args.AddRange(["-t", Number(options.DurationSeconds), "-map_metadata", "-1", "-map_chapters", "-1", "-fs",
                ((video ? MaximumVideoBytes : MaximumAudioBytes) + 1).ToString(CultureInfo.InvariantCulture), prepared]);
            Recheck(fullPath, token);
            await RunAsync(ExecutablePath, args, token, encoder: true).ConfigureAwait(false);
            var bytes = new FileInfo(prepared).Length;
            if (bytes is <= 0 || bytes > (video ? MaximumVideoBytes : MaximumAudioBytes)) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_ACF20265D21B"));
            var output = await _media.InspectAsync(prepared, token).WaitAsync(token).ConfigureAwait(false);
            var actual = await ProbeAsync(prepared, token).ConfigureAwait(false);
            var actualDuration = video ? actual.Video?.Duration ?? 0 : actual.Audio?.Duration ?? 0;
            if (actualDuration <= 0) actualDuration = output.DurationSeconds;
            if (video && (output.Kind != "Video" || output.Format != "MP4" || actual.Video is not { Codec: "h264", PixelFormat: "yuv420p" } v
                    || v.Width != width || v.Height != height || Math.Abs(v.Sar - 1) > .000001 || Math.Abs(v.Rotation) > .001 || v.Fps <= 0 || v.Fps > 30.001
                    || actual.Audio is { Codec: not "aac" } || output.HasAudio != keepAudio)
                || !video && (output.Kind != "Audio" || output.Format != "WAV" || actual.Audio is not { Codec: "pcm_s16le", Rate: 48000, Channels: 2 })
                || actualDuration <= 0 || actualDuration > MaximumDurationSeconds + .1 || Math.Abs(actualDuration - options.DurationSeconds) > (video ? .12 : .05))
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_51536F3340F0"));
            if (video)
            {
                var frame = await _media.CreatePreviewAsync(prepared, 64, token).WaitAsync(token).ConfigureAwait(false);
                if (frame.Length < 8) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_7CD46E75568E"));
            }
            else
                await RunAsync(ExecutablePath, ["-hide_banner", "-v", "error", "-nostdin", "-xerror", "-protocol_whitelist", "file,pipe", "-i", prepared, "-map", "0:a:0", "-t", "0.05", "-f", "null", "-"], token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var result = new MediaPreviewResult(prepared, temporary, parent, video, actualDuration, options.StartSeconds);
            temporary = null;return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_AE882CB234CA")); }
        catch (TimeoutException exception) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_D71FCD995265"), exception); }
        catch (UnauthorizedAccessException exception) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_59C7ED8BE35B"), exception); }
        catch (IOException exception) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_E8B2D8857DE9"), exception); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or System.Security.SecurityException)
        { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_945167734ABA"), exception); }
        finally { if (temporary is not null && parent is not null) await MediaPreviewFiles.AwaitCleanupAsync(MediaPreviewFiles.StartCleanup(temporary, parent)).ConfigureAwait(false); }
    }

    private static (int Width, int Height) OutputSize(Video source)
    {
        if (source.Width is < 2 or > 16000 || source.Height is < 2 or > 16000 || (long)source.Width * source.Height > 40_000_000 || source.Fps > 240)
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_BFA624335ED4"));
        if (!double.IsFinite(source.Sar) || source.Sar <= 0 || source.Sar > 10000 || Math.Abs(source.Rotation / 90 - Math.Round(source.Rotation / 90)) > .001)
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_CEC1D77BC42A"));
        double width = source.Width * source.Sar, height = source.Height;
        if (Math.Abs(Math.Round(source.Rotation / 90) % 2) == 1) (width, height) = (height, width);
        var scale = Math.Min(1, Math.Min(640 / width, 360 / height));
        var w = (int)Math.Floor(width * scale / 2) * 2;var h = (int)Math.Floor(height * scale / 2) * 2;
        if (w < 2 || h < 2) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_0049E7D0C0CB"));
        return (w, h);
    }

    private async Task<Probe> ProbeAsync(string path, CancellationToken token)
    {
        var bytes = await RunAsync(ProbeExecutablePath, ["-v", "error", "-protocol_whitelist", "file,pipe", "-show_streams", "-show_format", "-of", "json", path], token).ConfigureAwait(false);
        try
        {
            using var json = JsonDocument.Parse(bytes);var root = json.RootElement;Video? video = null;Audio? audio = null;
            var formatStart = root.TryGetProperty("format", out var format) ? Numeric(format, "start_time") : 0;
            foreach (var stream in root.GetProperty("streams").EnumerateArray())
            {
                var index = Integer(stream, "index", -1);var codec = Text(stream, "codec_name");if (index < 0 || codec is "" or "unknown") continue;
                var attached = stream.TryGetProperty("disposition", out var disposition) && Integer(disposition, "attached_pic") != 0;
                if (video is null && Text(stream, "codec_type") == "video" && !attached && Integer(stream, "width") > 0 && Integer(stream, "height") > 0)
                {
                    var rotation = stream.TryGetProperty("tags", out var tags) ? Numeric(tags, "rotate") : 0;
                    if (stream.TryGetProperty("side_data_list", out var sides)) foreach (var side in sides.EnumerateArray()) if (side.TryGetProperty("rotation", out _)) { rotation = Numeric(side, "rotation");break; }
                    video = new Video(index, Integer(stream, "width"), Integer(stream, "height"), Ratio(Text(stream, "sample_aspect_ratio"), 1), rotation, Ratio(Text(stream, "avg_frame_rate"), 0), StreamDuration(stream), codec, Text(stream, "pix_fmt"));
                }
                if (audio is null && Text(stream, "codec_type") == "audio" && Integer(stream, "channels") > 0 && Numeric(stream, "sample_rate") > 0)
                    audio = new Audio(index, Integer(stream, "sample_rate"), Integer(stream, "channels"), StreamDuration(stream), Numeric(stream, "start_time") - formatStart, codec);
            }
            return new Probe(video, audio);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_145CC18D56F3"), exception); }
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_617D76216E0C"));
        var full = Path.GetFullPath(path).Replace('/', Path.DirectorySeparatorChar);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal)
            || full.IndexOf(':', full.Length > 1 && full[1] == ':' ? 2 : 0) >= 0) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_438E481BF5C4"));
        return full;
    }

    private static void Recheck(string path, CancellationToken token)
    {
        string? current = path;var depth = 0;
        while (!string.IsNullOrEmpty(current))
        {
            token.ThrowIfCancellationRequested();if (++depth > 256) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_87ABA4A01C6F"));
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || depth == 1 && (attributes & FileAttributes.Directory) != 0)
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_35537CCFB75E"));
            current = Path.GetDirectoryName(current);
        }
    }

    private static async Task<FileStream> OpenInputAsync(string path, CancellationToken token)
    {
        var pending = Task.Run(() =>
        {
            Recheck(path, token);var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            try { Recheck(path, token);if ((File.GetAttributes(input.SafeFileHandle) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || input.Length is <= 0 or > MaximumSourceBytes) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_1B4A243E8C9E"));return input; }
            catch { input.Dispose();throw; }
        }, CancellationToken.None);
        try { return await pending.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); }
        catch { _ = pending.ContinueWith(task => { if (task.IsCompletedSuccessfully) task.Result.Dispose();else _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);throw; }
    }

    private async Task<byte[]> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token, bool encoder = false)
    {
        token.ThrowIfCancellationRequested();var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { if (!process.Start()) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_0D4E443B4B05")); }
        catch (System.ComponentModel.Win32Exception exception) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_B63A0D395A25"), exception); }
        var output = DrainAsync(process.StandardOutput.BaseStream, 2 * 1024 * 1024);var error = DrainAsync(process.StandardError.BaseStream, 65536);
        try
        {
            if (encoder) ProcessStarted?.Invoke(process.Id);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await Task.WhenAll(output, error).WaitAsync(token).ConfigureAwait(false);
            var bytes = await output.ConfigureAwait(false);var errors = await error.ConfigureAwait(false);token.ThrowIfCancellationRequested();
            if (bytes.Length > 2 * 1024 * 1024 || errors.Length > 65536) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_AEF5500E491A"));
            if (process.ExitCode != 0) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_52C7EA4ED165") + Encoding.UTF8.GetString(errors)[..Math.Min(800, Encoding.UTF8.GetString(errors).Length)].Trim());
            return bytes;
        }
        finally
        {
            if (!process.HasExited) { try { process.Kill(entireProcessTree: true); } catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (Exception exception) when (exception is TimeoutException or InvalidOperationException) { }
            var readers = Task.WhenAll(output, error);
            try { await readers.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (TimeoutException) { process.StandardOutput.Dispose();process.StandardError.Dispose();_ = readers.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default); }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
        }
    }

    private static async Task<byte[]> DrainAsync(Stream input, int maximum)
    { using var result = new MemoryStream();var buffer = new byte[8192];int count;while ((count = await input.ReadAsync(buffer).ConfigureAwait(false)) != 0) if (result.Length <= maximum) result.Write(buffer, 0, (int)Math.Min(count, maximum + 1 - result.Length));return result.ToArray(); }
    private static string Text(JsonElement value, string name) => value.TryGetProperty(name, out var item) ? item.ToString() : "";
    private static double StreamDuration(JsonElement stream)
    {
        var duration = Numeric(stream, "duration");if (duration > 0) return duration;
        // Matroska often stores an absolute stream end in DURATION instead of
        // stream.duration. A longer other track must not extend this interval.
        if (!stream.TryGetProperty("tags", out var tags)) return 0;
        var text = Text(tags, "DURATION");if (text.Length == 0) text = Text(tags, "duration");
        var parts = text.Split(':');
        if (parts.Length != 3 || !double.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !double.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || hours < 0 || minutes is < 0 or >= 60 || seconds is < 0 or >= 60) return 0;
        duration = hours * 3600 + minutes * 60 + seconds - Numeric(stream, "start_time");
        return double.IsFinite(duration) && duration > 0 ? duration : 0;
    }
    private static double Numeric(JsonElement value, string name) => double.TryParse(Text(value, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : 0;
    private static int Integer(JsonElement value, string name, int fallback = 0) => int.TryParse(Text(value, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : fallback;
    private static double Ratio(string value, double fallback) { var fields = value.Split([':', '/']);return fields.Length == 2 && double.TryParse(fields[0], CultureInfo.InvariantCulture, out var numerator) && double.TryParse(fields[1], CultureInfo.InvariantCulture, out var denominator) && numerator > 0 && denominator > 0 ? numerator / denominator : fallback; }
    private static string Number(double value) => value.ToString("0.################", CultureInfo.InvariantCulture);
    private sealed record Video(int Index, int Width, int Height, double Sar, double Rotation, double Fps, double Duration, string Codec, string PixelFormat);
    private sealed record Audio(int Index, int Rate, int Channels, double Duration, double Start, string Codec);
    private sealed record Probe(Video? Video, Audio? Audio);
}
