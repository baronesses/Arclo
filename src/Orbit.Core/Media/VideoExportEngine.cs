using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Orbit.Core.Media;

/// <summary>Bounded, cancellable video resizing/muting with an atomic, non-overwriting MP4 publication.</summary>
public sealed class VideoExportEngine
{
    public const int MaximumOutputDimension = 3840;
    public const int MaximumSourceDimension = 32768;
    public const long MaximumSourcePixels = 268_435_456;
    private readonly MediaEngine _media;
    private readonly TimeSpan _operationTimeout;
    // Observes a real encoder process for cancellation/lease tests; it does not replace processing.
    internal Action<int>? ProcessStarted { get; set; }

    public VideoExportEngine(string? ffmpegExecutable = null, TimeSpan? operationTimeout = null)
    {
        _operationTimeout = operationTimeout ?? TimeSpan.FromHours(2);
        if (_operationTimeout <= TimeSpan.Zero || _operationTimeout > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        _media = new MediaEngine(ffmpegExecutable, _operationTimeout);
    }

    public string ExecutablePath => _media.ExecutablePath;
    public string ProbeExecutablePath => _media.ProbeExecutablePath;

    public Task<MediaResult> ExportAsync(string source, VideoExportOptions options,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();
        if (options.Width is < 2 or > MaximumOutputDimension)
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_A919EC903D4C"));
        if (options.VideoCrf is < 0 or > 51)
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_3125D1338EE8"));
        if (options.FramesPerSecond is < 1 or > 60)
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_32827B886654"));
        return Task.Run(async () =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(_operationTimeout);
            try { return await ExportCoreAsync(source, options, progress, deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
            { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_AECDE1E2B29D"), ex); }
        }, ct);
    }

    private async Task<MediaResult> ExportCoreAsync(string path, VideoExportOptions options,
        IProgress<double>? progress, CancellationToken ct)
    {
        string? temporaryDirectory = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            path = ValidateSource(path);
            await using var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            RecheckSource(path);
            if ((File.GetAttributes(lease.SafeFileHandle) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || lease.Length <= 0)
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_66EB8BC1E5FC"));
            progress?.Report(0);
            var metadata = await _media.InspectAsync(path, ct).ConfigureAwait(false);
            if (metadata.Kind != "Video") throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_BDECE23A816A"));
            var input = await ProbeAsync(path, ct).ConfigureAwait(false);
            if (input.Width > MaximumSourceDimension || input.Height > MaximumSourceDimension || (long)input.Width * input.Height > MaximumSourcePixels)
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_FC7CDD30DE9B"));
            var (width, height) = OutputSize(input, options);
            var directory = options.OutputDirectory is null ? Path.GetDirectoryName(path)! : Path.GetFullPath(options.OutputDirectory);
            if (!Directory.Exists(directory)) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_45C406D632F2"));
            temporaryDirectory = Path.Combine(directory, ".orbit-video-export-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
            var temporary = Path.Combine(temporaryDirectory, "result.mp4");
            var keepAudio = !options.RemoveAudio && input.AudioIndex is not null;
            var args = new List<string> { "-hide_banner", "-v", "error", "-nostdin", "-n", "-nostats", "-xerror",
                "-threads", "2", "-protocol_whitelist", "file,pipe", "-i", path, "-map", "0:" + Invariant(input.VideoIndex), "-sn", "-dn" };
            if (keepAudio) args.AddRange(["-map", "0:" + Invariant(input.AudioIndex!.Value), "-c:a", "aac", "-b:a", "192k"]);
            else args.Add("-an");
            var filter=$"scale={Invariant(width)}:{Invariant(height)}:flags=lanczos,setsar=1";
            if(options.FramesPerSecond is int fps)filter+=",fps="+Invariant(fps)+":eof_action=pass";
            args.AddRange(["-vf", filter, "-pix_fmt", "yuv420p",
                "-c:v", "libx264", "-threads:v", "2", "-preset", "medium", "-crf", Invariant(options.VideoCrf),
                "-map_metadata", "-1", "-map_chapters", "-1", "-metadata:s:v:0", "rotate=0", "-movflags", "+faststart",
                "-progress", "pipe:1", "-stats_period", "0.1", temporary]);
            double last = 0;
            void ProgressLine(string line)
            {
                if (!line.StartsWith("out_time_us=", StringComparison.Ordinal) || input.Duration <= 0
                    || !long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us)) return;
                var value = Math.Clamp(us / 1_000_000d / input.Duration, 0, .98);
                if (value > last) { last = value; progress?.Report(value); }
            }
            await RunAsync(ExecutablePath, args, ct, ProgressLine, encoder: true).ConfigureAwait(false);
            progress?.Report(.99);
            var output = await _media.InspectAsync(temporary, ct).ConfigureAwait(false);
            var outputProbe = await ProbeAsync(temporary, ct).ConfigureAwait(false);
            var expectedDuration = input.VideoDuration > 0 ? input.VideoDuration : input.Duration;
            var actualDuration = outputProbe.VideoDuration > 0 ? outputProbe.VideoDuration : outputProbe.Duration;
            var durationTolerance = Math.Max(.15, Math.Max(input.FrameSeconds * 2,options.FramesPerSecond is int requestedFps?1d/requestedFps+.05:0));
            if (output.Kind != "Video" || output.Format != "MP4" || output.Bytes <= 0 || output.Width != width || output.Height != height
                || outputProbe.Width != width || outputProbe.Height != height || outputProbe.Codec != "h264" || outputProbe.PixelFormat != "yuv420p"
                || Math.Abs(outputProbe.Sar - 1) > 1e-6 || Math.Abs(outputProbe.Rotation) > .001 || output.HasAudio != keepAudio
                || keepAudio && outputProbe.AudioCodec != "aac"
                || options.FramesPerSecond is int targetFps && Math.Abs(outputProbe.FrameRate-targetFps)>.001
                || actualDuration <= 0 || expectedDuration > 0 && Math.Abs(actualDuration - expectedDuration) > durationTolerance)
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_EBE1DB203C1B"));
            // A successful probe alone does not prove that even the first compressed frame can be decoded.
            var preview = await _media.CreatePreviewAsync(temporary, 64, ct).ConfigureAwait(false);
            if (preview.Length < 8) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_DA0889619CEF"));
            RecheckSource(path); ct.ThrowIfCancellationRequested();
            var target = Commit(temporary, directory, path, ct);
            progress?.Report(1);
            return new(target, output.Bytes, width, height, "MP4", lease.Length);
        }
        catch (UnauthorizedAccessException ex) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_663A299EDD7C"), ex); }
        catch (IOException ex) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_7B43AD5AEDDE"), ex); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_9CA75AF339C6"), ex); }
        finally
        {
            if (temporaryDirectory is not null)
                try { Directory.Delete(temporaryDirectory, recursive: true); }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static (int Width, int Height) OutputSize(VideoProbe source, VideoExportOptions options)
    {
        double nativeWidth = source.Width * source.Sar, nativeHeight = source.Height;
        var quarter = (int)Math.Round(source.Rotation / 90);
        if (Math.Abs(source.Rotation - quarter * 90) > .001)
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_CEACF19CD084"));
        if (Math.Abs(quarter % 2) == 1) (nativeWidth, nativeHeight) = (nativeHeight, nativeWidth);
        if (!double.IsFinite(nativeWidth) || !double.IsFinite(nativeHeight) || nativeWidth < 2 || nativeHeight < 2)
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_358A32BDD074"));
        var requestedWidth = options.Width ?? nativeWidth;
        if (options.NoUpscale) requestedWidth = Math.Min(requestedWidth, nativeWidth);
        var scale = Math.Min(requestedWidth / nativeWidth, Math.Min(MaximumOutputDimension / nativeWidth, MaximumOutputDimension / nativeHeight));
        var width = (int)Math.Floor(nativeWidth * scale / 2 + 1e-8) * 2;
        var height = (int)Math.Floor(nativeHeight * scale / 2 + 1e-8) * 2;
        if (width < 2 || height < 2)
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_7043C21CCA41"));
        return (width, height);
    }

    private async Task<VideoProbe> ProbeAsync(string path, CancellationToken ct)
    {
        var bytes = await RunAsync(ProbeExecutablePath, ["-v", "error", "-protocol_whitelist", "file,pipe", "-show_streams", "-show_format", "-of", "json", path], ct).ConfigureAwait(false);
        try
        {
            using var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;
            if (!root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_3896762AB1BA"));
            JsonElement? video = null, audio = null;
            foreach (var stream in streams.EnumerateArray())
            {
                var index = Integer(stream, "index", -1);
                var codec = Text(stream, "codec_name");
                if (index < 0 || codec is "" or "unknown") continue;
                var attached = stream.TryGetProperty("disposition", out var disposition) && Integer(disposition, "attached_pic") != 0;
                if (video is null && Text(stream, "codec_type") == "video" && !attached && Integer(stream, "width") > 0 && Integer(stream, "height") > 0) video = stream;
                if (audio is null && Text(stream, "codec_type") == "audio" && Integer(stream, "channels") > 0 && Number(stream, "sample_rate") > 0) audio = stream;
            }
            if (video is not { } v) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_8550916D323A"));
            var rotation = v.TryGetProperty("tags", out var tags) ? Number(tags, "rotate") : 0;
            if (v.TryGetProperty("side_data_list", out var side) && side.ValueKind == JsonValueKind.Array)
                foreach (var entry in side.EnumerateArray()) if (entry.TryGetProperty("rotation", out _)) { rotation = Number(entry, "rotation"); break; }
            var sar = Ratio(Text(v, "sample_aspect_ratio"), 1);
            if (!double.IsFinite(sar) || sar <= 0 || sar > 10000)
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_B0457AC9590B"));
            var duration = root.TryGetProperty("format", out var format) ? Number(format, "duration") : 0;
            var videoDuration = VideoDuration(v);
            duration = Math.Max(duration, videoDuration);
            var frameRate = Ratio(Text(v, "avg_frame_rate"), 0);
            return new(Integer(v, "index"), audio is { } a ? Integer(a, "index") : null, Integer(v, "width"), Integer(v, "height"), sar, rotation,
                duration, videoDuration, frameRate > 0 ? 1 / frameRate : .04, frameRate, Text(v, "codec_name"), Text(v, "pix_fmt"), audio is { } aud ? Text(aud, "codec_name") : "");
        }
        catch (JsonException ex) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_A5D3ECC3D43B"), ex); }
    }

    private async Task<byte[]> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct,
        Action<string>? progressLine = null, bool encoder = false)
    {
        ct.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        try { if (!process.Start()) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_4836342C7011")); }
        catch (System.ComponentModel.Win32Exception ex) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_DB4D80A7D1ED"), ex); }
        var stdout = progressLine is null ? ReadBoundedAsync(process.StandardOutput.BaseStream, 2 * 1024 * 1024) : ReadProgressAsync(process.StandardOutput, progressLine);
        var stderr = ReadBoundedAsync(process.StandardError.BaseStream, 65536);
        try
        {
            if (encoder) ProcessStarted?.Invoke(process.Id);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var bytes = await stdout.ConfigureAwait(false); var errors = await stderr.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                var error = Encoding.UTF8.GetString(errors);
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_9DFF4F30260F") + error[..Math.Min(1200, error.Length)].Trim());
            }
            if (bytes.Length > 2 * 1024 * 1024) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_4ACCD610CDEC"));
            return bytes;
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum)
    {
        using var result = new MemoryStream(); var buffer = new byte[8192]; int n;
        while ((n = await stream.ReadAsync(buffer).ConfigureAwait(false)) != 0)
            if (result.Length <= maximum) result.Write(buffer, 0, (int)Math.Min(n, maximum + 1L - result.Length));
        return result.ToArray();
    }
    private static async Task<byte[]> ReadProgressAsync(StreamReader reader, Action<string> onLine)
    { while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line) onLine(line); return []; }
    private static string ValidateSource(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_62565F5E9806"));
        path = Path.GetFullPath(path);
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)
            || path.AsSpan(Path.GetPathRoot(path)?.Length ?? 0).Contains(':'))
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_89B60C5BAD3C"));
        RecheckSource(path); return path;
    }
    private static void RecheckSource(string path)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_80D4F304A69C"));
        for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_2046607EB8EF"));
    }
    private static string Commit(string temporary, string directory, string source, CancellationToken ct)
    {
        var stem = Path.GetFileNameWithoutExtension(source);
        if (stem.Length > 200) { stem = stem[..200]; if (char.IsHighSurrogate(stem[^1])) stem = stem[..^1]; }
        for (var i = 0; i < 10000; i++)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, stem + "_exported" + (i == 0 ? "" : "_" + Invariant(i)) + ".mp4");
            if (string.Equals(path, source, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Move(temporary, path, overwrite: false); return path; }
            catch (IOException) when (File.Exists(path) || Directory.Exists(path)) { }
        }
        throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_5708A6684186"));
    }
    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Text(JsonElement item, string key) => item.TryGetProperty(key, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString() : "";
    private static int Integer(JsonElement item, string key, int fallback = 0) => int.TryParse(Text(item, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    private static double Number(JsonElement item, string key) => double.TryParse(Text(item, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : 0;
    private static double VideoDuration(JsonElement stream)
    {
        var duration = Number(stream, "duration");
        if (duration > 0) return duration;
        // Matroska commonly reports stream duration only as its DURATION tag. Container duration
        // can belong to a longer audio stream and must not be mistaken for the video timeline.
        if (!stream.TryGetProperty("tags", out var tags)) return 0;
        var text = Text(tags, "DURATION");
        if (text.Length == 0) text = Text(tags, "duration");
        var parts = text.Split(':');
        if (parts.Length != 3 || !double.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !double.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || minutes is < 0 or >= 60 || seconds is < 0 or >= 60) return 0;
        duration = hours * 3600 + minutes * 60 + seconds - Number(stream, "start_time");
        return double.IsFinite(duration) && duration > 0 ? duration : 0;
    }
    private static double Ratio(string text, double fallback)
    {
        var parts = text.Split([':', '/']);
        return parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0 && n > 0 ? n / d : fallback;
    }
    private sealed record VideoProbe(int VideoIndex, int? AudioIndex, int Width, int Height, double Sar, double Rotation,
        double Duration, double VideoDuration, double FrameSeconds, double FrameRate, string Codec, string PixelFormat, string AudioCodec);
}
