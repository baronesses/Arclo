using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Orbit.Core.Media;

/// <summary>Creates a bounded, palette-optimized GIF from an explicitly selected local video interval.</summary>
public sealed class VideoGifEngine
{
    public const int MaximumFrames = 900;
    public const int MaximumDimension = 1280;
    public const int MaximumPixels = 1_000_000;
    public const long MaximumOutputBytes = 128L * 1024 * 1024;
    public const long MaximumSourceBytes = 16L * 1024 * 1024 * 1024;
    private readonly MediaEngine _media;
    private readonly TimeSpan _timeout;

    public VideoGifEngine(string? ffmpegExecutable = null, TimeSpan? operationTimeout = null)
    {
        _timeout = operationTimeout ?? TimeSpan.FromMinutes(10);
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        _media = new MediaEngine(ffmpegExecutable);
    }

    /// <summary>Reads the actual video-stream duration for the GIF range editor, without creating any output.</summary>
    public async Task<double> GetVideoDurationAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout < TimeSpan.FromSeconds(45) ? _timeout : TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        try
        {
            var fullPath = NormalizePath(path);
            await using var held = await OpenInputAsync(fullPath, token).ConfigureAwait(false);
            var source = await _media.InspectAsync(fullPath, token).ConfigureAwait(false);
            if (source.Kind != "Video") throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_BECD20DFB681"));
            var video = await ReadVideoAsync(fullPath, token).ConfigureAwait(false);
            var duration = video.Duration > 0 ? video.Duration : source.DurationSeconds;
            if (!double.IsFinite(duration) || duration <= 0) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_A250E6AE322D"));
            token.ThrowIfCancellationRequested(); return duration;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_BB2A0CEB2465")); }
        catch (TimeoutException exception) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_095E5F974F4F"), exception); }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_D9898264D0F7"), exception); }
        catch (UnauthorizedAccessException exception) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_1883EC73B7C7"), exception); }
        catch (IOException exception) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_248FD4B93223"), exception); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_F71B5941FA48"), exception); }
    }

    public async Task<MediaResult> CreateAsync(string path, VideoGifOptions options, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options); ct.ThrowIfCancellationRequested();
        if (!double.IsFinite(options.StartSeconds) || options.StartSeconds < 0 || !double.IsFinite(options.DurationSeconds)
            || options.DurationSeconds <= 0 || options.DurationSeconds > 30)
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_BFAB1B0088BA"));
        if (options.Width is < 160 or > MaximumDimension) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_58BE41FA878E"));
        if (options.FramesPerSecond is < 1 or > 30) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_6556762FD437"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(_timeout);
        var token = timeout.Token; string? temporary = null;
        try
        {
            var fullPath = NormalizePath(path);
            await using var sourceFile = await OpenInputAsync(fullPath, token).ConfigureAwait(false);
            var source = await _media.InspectAsync(fullPath, token).ConfigureAwait(false);
            if (source.Kind != "Video") throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_BECD20DFB681"));
            var video = await ReadVideoAsync(fullPath, token).ConfigureAwait(false);
            var duration = video.Duration > 0 ? video.Duration : source.DurationSeconds;
            if (!double.IsFinite(duration) || duration <= 0 || options.StartSeconds + options.DurationSeconds > duration + 0.000001)
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_C456330F02C4"));
            var (width, height) = OutputSize(video, options.Width);
            var directory = string.IsNullOrWhiteSpace(options.OutputDirectory) ? Path.GetDirectoryName(fullPath)! : Path.GetFullPath(options.OutputDirectory);
            // Keep potentially slow directory access off the dispatcher and under the operation deadline.
            if (!await Task.Run(() => Directory.Exists(directory), CancellationToken.None).WaitAsync(token).ConfigureAwait(false))
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_1692FC740589"));
            temporary = Path.Combine(directory, ".orbit-gif-" + Guid.NewGuid().ToString("N"));
            var workingDirectory = temporary;
            var creating = Task.Run(() => Directory.CreateDirectory(workingDirectory), CancellationToken.None);
            try { await creating.WaitAsync(token).ConfigureAwait(false); }
            catch
            {
                // A slow network CreateDirectory may finish after cancellation; remove its late result too.
                _ = creating.ContinueWith(async task =>
                {
                    if (task.IsCompletedSuccessfully) await CleanupAsync(workingDirectory).ConfigureAwait(false);
                    else _ = task.Exception;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
                throw;
            }
            var palette = Path.Combine(temporary, "palette.png"); var prepared = Path.Combine(temporary, "result.gif");
            var filter = $"trim=duration={Number(options.DurationSeconds)},setpts=PTS-STARTPTS,fps=fps={options.FramesPerSecond}:start_time=0:round=up:eof_action=pass,scale={width}:{height}:flags=lanczos,setsar=1";
            Report(progress, 0); double last = 0;
            Action<string> Progress(double start, double span) => line =>
            {
                if (!line.StartsWith("out_time_us=", StringComparison.Ordinal) || !long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us)) return;
                var value = start + Math.Clamp(us / 1_000_000d / options.DurationSeconds, 0, 1) * span;
                if (value > last) { last = value; Report(progress, value); }
            };
            // Separate passes avoid buffering as many as 900 full RGB frames in a split filter graph.
            var first = Input(fullPath, options);
            first.AddRange(["-map", "0:" + video.Index, "-an", "-sn", "-dn", "-vf", filter + ",palettegen=stats_mode=full:reserve_transparent=0",
                "-frames:v", "1", "-c:v", "png", "-threads:v", "1", "-update", "1", "-progress", "pipe:1", "-stats_period", "0.1", palette]);
            await RunAsync(_media.ExecutablePath, first, token, Progress(0, .45)).ConfigureAwait(false);
            Report(progress, last = Math.Max(last, .45));
            var frameCount = Math.Max(1, (int)Math.Ceiling(options.DurationSeconds * options.FramesPerSecond - 0.000001));
            var finalDelay = Math.Max(1, (int)Math.Round((options.DurationSeconds - (frameCount - 1d) / options.FramesPerSecond) * 100, MidpointRounding.AwayFromZero));
            var second = Input(fullPath, options);
            second.AddRange(["-protocol_whitelist", "file,pipe", "-f", "image2", "-pattern_type", "none", "-i", palette, "-filter_complex_threads", "1", "-filter_complex",
                $"[0:{video.Index}]{filter}[frames];[frames][1:v:0]paletteuse=dither=sierra2_4a:diff_mode=rectangle[out]", "-map", "[out]",
                "-an", "-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1", "-frames:v", frameCount.ToString(CultureInfo.InvariantCulture),
                "-loop", "0", "-final_delay", finalDelay.ToString(CultureInfo.InvariantCulture), "-fs", (MaximumOutputBytes + 1).ToString(CultureInfo.InvariantCulture),
                "-threads:v", "1", "-progress", "pipe:1", "-stats_period", "0.1", prepared]);
            await RunAsync(_media.ExecutablePath, second, token, Progress(.45, .5)).ConfigureAwait(false);
            var gif = await Task.Run(() => ReadGif(prepared, token), token).ConfigureAwait(false);
            var tolerance = Math.Max(.03, 1d / options.FramesPerSecond + .02);
            if (gif.Width != width || gif.Height != height || gif.Frames > frameCount || gif.Frames < 1 || !gif.LoopsForever
                || Math.Abs(gif.Duration - options.DurationSeconds) > tolerance)
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_21AEE8709B32"));
            await ValidateProbeAsync(prepared, gif, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var output = Commit(prepared, directory, fullPath, token); Report(progress, 1);
            return new MediaResult(output, gif.Bytes, width, height, "GIF", source.Bytes);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_897CA5CA4F33")); }
        catch (TimeoutException exception) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_A6533DD123AF"), exception); }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_E0C8067D03AE"), exception); }
        catch (UnauthorizedAccessException exception) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_7ACB128F684C"), exception); }
        catch (IOException exception) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_F9DD7D021DAC"), exception); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_C0F4A1BD13B6"), exception); }
        finally
        {
            if (temporary is not null) await CleanupAsync(temporary).ConfigureAwait(false);
        }
    }

    private static async Task CleanupAsync(string directory)
    {
        for (var attempt = 0; attempt < 11; attempt++)
        {
            var removing = Task.Run(() => { if (Directory.Exists(directory)) Directory.Delete(directory, true); });
            try { await removing.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); break; }
            catch (TimeoutException)
            {
                // An offline UNC share cannot be cancelled inside the OS. Bound the caller's wait;
                // this same deletion continues in the background if the share becomes available.
                _ = removing.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { if (attempt < 10) await Task.Delay(Math.Min(300, 25 << Math.Min(attempt, 4))).ConfigureAwait(false); }
        }
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_C6E443B0D3D6"));
        var full = Path.GetFullPath(path).Replace('/', Path.DirectorySeparatorChar);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal)
            || full.IndexOf(':', full.Length > 1 && full[1] == ':' ? 2 : 0) >= 0)
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_9F37A054F25B"));
        return full;
    }

    private static async Task<FileStream> OpenInputAsync(string path, CancellationToken ct)
    {
        var pending = Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested(); Recheck(path, ct);
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            try
            {
                Recheck(path, ct);
                if ((File.GetAttributes(file.SafeFileHandle) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_43396EB9D165"));
                if (file.Length is <= 0 or > MaximumSourceBytes) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_54C5546460AE"));
                return file;
            }
            catch { file.Dispose(); throw; }
        }, CancellationToken.None);
        try { return await pending.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); }
        catch
        {
            _ = pending.ContinueWith(task => { if (task.IsCompletedSuccessfully) task.Result.Dispose(); else _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default); throw;
        }
    }

    private static void Recheck(string path, CancellationToken ct)
    {
        string? current = path; var depth = 0;
        while (!string.IsNullOrEmpty(current))
        {
            ct.ThrowIfCancellationRequested(); if (++depth > 256) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_D4AFA3292751"));
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_A959658A4B5D"));
            if (depth == 1 && (attributes & FileAttributes.Directory) != 0) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_FC7E66E112DF"));
            current = Path.GetDirectoryName(current);
        }
    }

    private async Task<VideoInfo> ReadVideoAsync(string path, CancellationToken ct)
    {
        var bytes = await RunAsync(_media.ProbeExecutablePath, ["-v", "error", "-protocol_whitelist", "file,pipe", "-show_entries",
            "stream=index,codec_type,width,height,duration,start_time,sample_aspect_ratio:stream_disposition=attached_pic:stream_tags=rotate,DURATION,duration:stream_side_data=rotation", "-of", "json", "-i", path], ct).ConfigureAwait(false);
        try
        {
            using var json = JsonDocument.Parse(bytes);
            foreach (var stream in json.RootElement.GetProperty("streams").EnumerateArray())
            {
                if (Text(stream, "codec_type") != "video" || (stream.TryGetProperty("disposition", out var disposition) && Integer(disposition, "attached_pic") != 0)) continue;
                var width = Integer(stream, "width"); var height = Integer(stream, "height");
                if (width <= 0 || height <= 0 || width > 16384 || height > 16384 || (long)width * height > 100_000_000)
                    throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_9C3FE334F6EE"));
                var sar = Text(stream, "sample_aspect_ratio").Split(':'); double ratio = 1;
                if (sar.Length == 2 && double.TryParse(sar[0], CultureInfo.InvariantCulture, out var numerator) && double.TryParse(sar[1], CultureInfo.InvariantCulture, out var denominator)
                    && numerator > 0 && denominator > 0) ratio = numerator / denominator;
                var rotation = stream.TryGetProperty("tags", out var tags) ? Numeric(tags, "rotate") : 0;
                if (stream.TryGetProperty("side_data_list", out var sides))
                    foreach (var side in sides.EnumerateArray()) if (side.TryGetProperty("rotation", out _)) { rotation = Numeric(side, "rotation"); break; }
                if (Math.Abs(rotation / 90 - Math.Round(rotation / 90)) > .001)
                    throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_C6F6108B91BE"));
                var aspect = width * ratio / height;
                if (Math.Abs(Math.Round(rotation / 90) % 2) == 1) aspect = 1 / aspect;
                if (!double.IsFinite(aspect) || aspect is < .001 or > 1000) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_E44255AA19CD"));
                return new VideoInfo(Integer(stream, "index"), aspect, VideoDuration(stream));
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_ED0D19777585"), exception); }
        throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_FD5760322986"));
    }

    private static (int Width, int Height) OutputSize(VideoInfo video, int requestedWidth)
    {
        var width = (double)requestedWidth; var height = width / video.Aspect;
        var factor = Math.Min(1, Math.Min(MaximumDimension / height, Math.Sqrt(MaximumPixels / (width * height))));
        return (Math.Max(1, (int)Math.Floor(width * factor)), Math.Max(1, (int)Math.Floor(height * factor)));
    }

    private static List<string> Input(string path, VideoGifOptions options) => ["-hide_banner", "-v", "error", "-nostdin", "-n", "-nostats", "-threads", "2",
        "-filter_threads", "1", "-protocol_whitelist", "file,pipe", "-ss", Number(options.StartSeconds), "-t", Number(options.DurationSeconds), "-i", path];

    private async Task ValidateProbeAsync(string path, GifInfo gif, CancellationToken ct)
    {
        var bytes = await RunAsync(_media.ProbeExecutablePath, ["-v", "error", "-protocol_whitelist", "file,pipe", "-count_frames", "-show_entries",
            "format=format_name,duration:stream=codec_type,codec_name,width,height,nb_read_frames", "-of", "json", "-i", path], ct).ConfigureAwait(false);
        try
        {
            using var json = JsonDocument.Parse(bytes); var root = json.RootElement; var streams = root.GetProperty("streams");
            if (Text(root.GetProperty("format"), "format_name") != "gif" || streams.GetArrayLength() != 1) throw new JsonException();
            var stream = streams[0];
            if (Text(stream, "codec_name") != "gif" || Text(stream, "codec_type") != "video" || Integer(stream, "width") != gif.Width
                || Integer(stream, "height") != gif.Height || Integer(stream, "nb_read_frames") != gif.Frames
                || Math.Abs(Numeric(root.GetProperty("format"), "duration") - gif.Duration) > .03) throw new JsonException();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_D3155E38973A"), exception); }
    }

    private static GifInfo ReadGif(string path, CancellationToken ct)
    {
        using var input = File.OpenRead(path);
        if (input.Length is <= 13 or > MaximumOutputBytes) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_863D8FF7215A"));
        using var reader = new BinaryReader(input, Encoding.ASCII, leaveOpen: true);
        void Ensure(int count) { ct.ThrowIfCancellationRequested(); if (input.Length - input.Position < count) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_7F6CE8023122")); }
        int Byte() { Ensure(1); return reader.ReadByte(); }
        int Word() { Ensure(2); return reader.ReadUInt16(); }
        void Skip(int count) { Ensure(count); input.Seek(count, SeekOrigin.Current); }
        void Blocks() { int count; while ((count = Byte()) != 0) Skip(count); }
        Ensure(13); var header = Encoding.ASCII.GetString(reader.ReadBytes(6));
        if (header is not ("GIF89a" or "GIF87a")) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_A4FCB7966BE4"));
        var width = Word(); var height = Word(); var packed = Byte(); Skip(2);
        if (width is < 1 or > MaximumDimension || height is < 1 or > MaximumDimension || (long)width * height > MaximumPixels)
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_D7369AD05C4C"));
        if ((packed & 128) != 0) Skip(3 * (1 << ((packed & 7) + 1)));
        var frames = 0; long delay = 0; var pendingDelay = 0; var forever = false;
        while (true)
        {
            var marker = Byte();
            if (marker == 0x3b) { if (input.Position != input.Length) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_CC6A31D25F45")); break; }
            if (marker == 0x21)
            {
                var label = Byte();
                if (label == 0xf9)
                {
                    if (Byte() != 4) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_91F20911F85C"));
                    Skip(1); pendingDelay = Word(); Skip(1); if (Byte() != 0) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_746CC51BFEAE"));
                }
                else if (label == 0xff)
                {
                    var size = Byte(); Ensure(size); var name = Encoding.ASCII.GetString(reader.ReadBytes(size));
                    if (name is "NETSCAPE2.0" or "ANIMEXTS1.0")
                    {
                        if (Byte() != 3 || Byte() != 1) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_605636300F14"));
                        forever = Word() == 0; if (Byte() != 0) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_605636300F14"));
                    }
                    else Blocks();
                }
                else Blocks();
                continue;
            }
            if (marker != 0x2c) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_917CC9AC3C7F"));
            var left = Word(); var top = Word(); var frameWidth = Word(); var frameHeight = Word(); packed = Byte();
            if (frameWidth <= 0 || frameHeight <= 0 || left + frameWidth > width || top + frameHeight > height)
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_5C68695A8AF2"));
            if ((packed & 128) != 0) Skip(3 * (1 << ((packed & 7) + 1)));
            if (Byte() is < 2 or > 8) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_CB73B9A15307")); Blocks();
            if (++frames > MaximumFrames || pendingDelay <= 0) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_7FF11AFB5C92"));
            delay += pendingDelay; pendingDelay = 0;
        }
        return new GifInfo(width, height, frames, delay / 100d, input.Length, forever);
    }

    private static string Commit(string prepared, string directory, string source, CancellationToken ct)
    {
        var stem = Path.GetFileNameWithoutExtension(source) + "_clip";
        for (var index = 0; index < 10000; index++)
        {
            ct.ThrowIfCancellationRequested(); var path = Path.Combine(directory, stem + (index == 0 ? "" : " (" + (index + 1) + ")") + ".gif");
            try { File.Move(prepared, path, false); return path; }
            catch (IOException) when (File.Exists(path) || Directory.Exists(path)) { }
        }
        throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_1733C554CED5"));
    }

    private static async Task<byte[]> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct, Action<string>? progress = null)
    {
        ct.ThrowIfCancellationRequested(); var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        try { if (!process.Start()) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_2357F744A4AB")); }
        catch (System.ComponentModel.Win32Exception exception) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_B52408C014A0"), exception); }
        var stdout = ReadAsync(process.StandardOutput, progress); var stderr = ReadAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            // A child retaining an inherited pipe must not defeat the overall cancellation deadline.
            await Task.WhenAll(stdout, stderr).WaitAsync(ct).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false); var error = await stderr.ConfigureAwait(false); ct.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_59306E6D6741") + error[..Math.Min(error.Length, 900)].Trim());
            if (output.Length > 1_000_000) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_E67B008313B4"));
            return Encoding.UTF8.GetBytes(output);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
                var exit = process.WaitForExitAsync(CancellationToken.None);
                try { await exit.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch (TimeoutException) { Observe(exit); }
                catch (InvalidOperationException) { }
            }
            var drains = Task.WhenAll(stdout, stderr);
            try { await drains.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                process.StandardOutput.Dispose(); process.StandardError.Dispose(); Observe(drains);
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private static void Observe(Task task) => _ = task.ContinueWith(completed => _ = completed.Exception,
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static async Task<string> ReadAsync(StreamReader reader, Action<string>? progress = null)
    {
        var text = new StringBuilder(); var buffer = new char[4096]; var line = new StringBuilder(); int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            if (progress is null) { if (text.Length <= 1_000_000) text.Append(buffer, 0, Math.Min(count, 1_000_001 - text.Length)); continue; }
            for (var index = 0; index < count; index++)
                if (buffer[index] == '\n') { progress(line.ToString().TrimEnd('\r')); line.Clear(); }
                else if (line.Length < 4096) line.Append(buffer[index]);
        }
        return text.ToString();
    }

    private static string Number(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);
    private static double VideoDuration(JsonElement stream)
    {
        var duration = Numeric(stream, "duration");
        if (duration > 0) return duration;
        // Matroska may expose only the video stream's DURATION tag. The container duration
        // can instead belong to a longer audio stream and must not extend the GIF interval.
        if (!stream.TryGetProperty("tags", out var tags)) return 0;
        var text = Text(tags, "DURATION"); if (text.Length == 0) text = Text(tags, "duration");
        var parts = text.Split(':');
        if (parts.Length != 3 || !double.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !double.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || minutes is < 0 or >= 60 || seconds is < 0 or >= 60) return 0;
        duration = hours * 3600 + minutes * 60 + seconds - Numeric(stream, "start_time");
        return double.IsFinite(duration) && duration > 0 ? duration : 0;
    }
    private static string Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) ? value.ToString() : "";
    private static int Integer(JsonElement item, string name) => int.TryParse(Text(item, name), CultureInfo.InvariantCulture, out var value) ? value : 0;
    private static double Numeric(JsonElement item, string name) => double.TryParse(Text(item, name), CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : 0;
    private static void Report(IProgress<double>? progress, double value) => progress?.Report(value);
    private sealed record VideoInfo(int Index, double Aspect, double Duration);
    private sealed record GifInfo(int Width, int Height, int Frames, double Duration, long Bytes, bool LoopsForever);
}
