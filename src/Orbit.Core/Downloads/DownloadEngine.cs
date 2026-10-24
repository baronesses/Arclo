using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Orbit.Core.Downloads;

/// <summary>Single public HTTP(S) item, without account cookies, plugins or user configuration.
/// Downloads and conversion are private until a validated MP3/MP4 is atomically published.</summary>
public sealed class DownloadEngine
{
    public const long MaximumOutputBytes = 2L * 1024 * 1024 * 1024;
    public const long MaximumStagingBytes = 6L * 1024 * 1024 * 1024;
    public const double MaximumDurationSeconds = 86400;
    private readonly string? _ytDlp, _deno;
    private readonly MediaEngine _media;
    private readonly TimeSpan _timeout;

    public DownloadEngine(string? ytDlpExecutable = null, string? ffmpegExecutable = null, string? denoExecutable = null, TimeSpan? operationTimeout = null)
    {
        _ytDlp = ytDlpExecutable; _deno = denoExecutable; _media = new MediaEngine(ffmpegExecutable);
        _timeout = operationTimeout ?? TimeSpan.FromHours(2);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromHours(24)) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
    }

    public string ExecutablePath => Resolve(_ytDlp, "ORBIT_YTDLP", "yt-dlp", "yt-dlp.exe");
    public string DenoExecutablePath => Resolve(_deno, "ORBIT_DENO", "deno", "deno.exe");
    public string FfmpegExecutablePath => _media.ExecutablePath;
    public string FfprobeExecutablePath => _media.ProbeExecutablePath;

    /// <summary>Pure validation; does not access the filesystem, profile or network.</summary>
    public static DownloadRequest Normalize(DownloadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var url = request.Url?.Trim() ?? "";
        if (url.Length is 0 or > 8192 || url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))
            || !Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(parsed.Host) || !string.IsNullOrEmpty(parsed.UserInfo))
            throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_9137880D8AF6"));
        if (parsed.AbsoluteUri.Length > 8192) throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_5D4BD4214680"));
        if (!Enum.IsDefined(request.Mode)) throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_631BA264FCAC"));
        string output;
        try
        {
            if (string.IsNullOrWhiteSpace(request.OutputDirectory)) throw new ArgumentException();
            output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.OutputDirectory));
            if (output.StartsWith(@"\\?\", StringComparison.Ordinal) || output.StartsWith(@"\\.\", StringComparison.Ordinal)
                || output.Skip(Path.GetPathRoot(output)?.Length ?? 0).Contains(':') || output.Any(char.IsControl)) throw new ArgumentException();
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_D83C39AC034F")); }
        return request with { Url = parsed.AbsoluteUri, OutputDirectory = output };
    }

    public static DownloadRequest NormalizeRequest(DownloadRequest request) => Normalize(request);

    public async Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var normalized = Normalize(request); ct.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(_timeout);
        var token = deadline.Token; string? working = null;
        using var monitorStop = new CancellationTokenSource(); Task? monitoring = null;
        try
        {
            var tools = await Task.Run(() => (Yt: ExecutablePath, Ffmpeg: FfmpegExecutablePath, Probe: FfprobeExecutablePath, Deno: DenoExecutablePath), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            await Task.Run(() => { CheckDirectory(normalized.OutputDirectory); }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
            working = Path.Combine(normalized.OutputDirectory, ".orbit-download-" + Guid.NewGuid().ToString("N"));
            var creatingPath = working; var creating = Task.Run(() => Directory.CreateDirectory(creatingPath), CancellationToken.None);
            try { await creating.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); }
            catch
            {
                working = null;
                _ = creating.ContinueWith(async t => { if (t.IsCompletedSuccessfully) await CleanupAsync(creatingPath).ConfigureAwait(false); else _ = t.Exception; },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap(); throw;
            }
            monitoring = MonitorAsync(working, deadline, monitorStop.Token);
            progress?.Report(new(0, global::Orbit.Core.Localization.L.Get("Text_E8FF7D8C2791")));
            var paths = new List<string>(); string? title = null; var responseLock = new object();
            var arguments = new List<string> {
                "--ignore-config", "--no-plugin-dirs", "--no-js-runtimes", "--js-runtimes", "deno:" + tools.Deno,
                "--no-remote-components", "--no-cookies", "--no-cookies-from-browser", "--no-cache-dir", "--no-exec", "--no-batch-file",
                "--no-download-archive", "--no-write-thumbnail", "--no-write-comments", "--no-mark-watched", "--no-playlist", "--playlist-items", "1",
                "--max-downloads", "1", "--max-filesize", MaximumOutputBytes.ToString(CultureInfo.InvariantCulture),
                "--socket-timeout", "15", "--retries", "2", "--fragment-retries", "2", "--abort-on-unavailable-fragments",
                "--encoding", "utf-8", "--color", "never", "--newline", "--progress", "--progress-delta", "1", "--no-simulate",
                "--progress-template", "download:ORBIT_PROGRESS:%(progress.downloaded_bytes)s|%(progress.total_bytes,progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s",
                "--print", "before_dl:ORBIT_TITLE:%(title)j", "--print", "after_move:ORBIT_PATH:%(filepath)j",
                "--paths", working, "--paths", "temp:" + working, "--output", "source.%(ext)s", "--windows-filenames",
                "--ffmpeg-location", Path.GetDirectoryName(tools.Ffmpeg)!, "--merge-output-format", "mkv", "--format",
                normalized.Mode == DownloadMode.AudioMp3 ? "ba/b" : "bv*[height<=1080]+ba/b[height<=1080]/b", "--", normalized.Url
            };
            var downloaded = await DownloadProcess.RunAsync(tools.Yt, arguments, working, token, line =>
            {
                if (line.StartsWith("ORBIT_PATH:", StringComparison.Ordinal))
                {
                    var value = ReadJsonString(line[11..]);
                    lock (responseLock) { paths.Add(value); if (paths.Count > 1) throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_43EEE159D0EF")); }
                }
                else if (line.StartsWith("ORBIT_TITLE:", StringComparison.Ordinal)) { lock (responseLock) title ??= ReadJsonString(line[12..]); }
                else if (line.StartsWith("ORBIT_PROGRESS:", StringComparison.Ordinal))
                {
                    var values = line[15..].Split('|');
                    if (values.Length == 4) { var done = Number(values[0]); var total = Number(values[1]);
                        progress?.Report(new(total > 0 && done >= 0 ? .65 * Math.Clamp(done!.Value / total.Value, 0, 1) : null, global::Orbit.Core.Localization.L.Get("Text_E8FF7D8C2791"), Number(values[2]), Number(values[3]))); }
                }
            }, 16 * 1024 * 1024).ConfigureAwait(false);
            // yt-dlp uses exit101 after successfully reaching --max-downloads1.
            if (downloaded.ExitCode is not (0 or 101)) throw NativeError(downloaded.Error);
            if (paths.Count != 1) throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_6BAE80AA3A78"));
            var source = ContainedFile(paths[0], working);
            await using var sourceLease = await OpenAsync(source, token).ConfigureAwait(false);
            if (sourceLease.Length is <= 0 or > MaximumOutputBytes) throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_89A65962761E"));
            var input = await ProbeAsync(tools.Probe, source, working, token).ConfigureAwait(false);
            ValidateInput(input, normalized.Mode); progress?.Report(new(.65, global::Orbit.Core.Localization.L.Get("Text_E4A5E36E285E")));
            var prepared = Path.Combine(working, normalized.Mode == DownloadMode.AudioMp3 ? "result.mp3" : "result.mp4");
            var ff = Input(source); int width = 0, height = 0, channels = 0;
            if (normalized.Mode == DownloadMode.AudioMp3)
            {
                channels = Math.Min(input.Audio!.Channels, 2);
                ff.AddRange(["-map", "0:" + input.Audio.Index, "-vn", "-sn", "-dn", "-c:a", "libmp3lame", "-b:a", "192k", "-ar", "48000", "-ac", channels.ToString(CultureInfo.InvariantCulture), "-f", "mp3"]);
            }
            else
            {
                (width, height) = Fit(input.Video!);
                ff.AddRange(["-map", "0:" + input.Video!.Index, "-sn", "-dn", "-vf", $"scale={width}:{height}:flags=lanczos,setsar=1", "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p"]);
                if (input.Audio is not null) { channels = Math.Min(input.Audio.Channels, 2); ff.AddRange(["-map", "0:" + input.Audio.Index, "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", channels.ToString(CultureInfo.InvariantCulture)]); }
                else ff.Add("-an");
                ff.AddRange(["-movflags", "+faststart", "-f", "mp4"]);
            }
            ff.AddRange(["-map_metadata", "-1", "-map_chapters", "-1", "-t", "86401", "-fs", (MaximumOutputBytes + 1).ToString(CultureInfo.InvariantCulture), "-progress", "pipe:1", "-stats_period", "1", prepared]);
            var converted = await DownloadProcess.RunAsync(tools.Ffmpeg, ff, working, token, line =>
            {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && Number(line[12..]) is { } us)
                    progress?.Report(new(.65 + .28 * Math.Clamp(us / 1e6 / input.Duration, 0, 1), global::Orbit.Core.Localization.L.Get("Text_E4A5E36E285E")));
            }, 16 * 1024 * 1024).ConfigureAwait(false);
            if (converted.ExitCode != 0) throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_21D22BD55B7F"));
            var actual = await ProbeAsync(tools.Probe, prepared, working, token).ConfigureAwait(false);
            var bytes = new FileInfo(prepared).Length;
            if (bytes is <= 0 or > MaximumOutputBytes || actual.Duration is <= 0 or > MaximumDurationSeconds)
                throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_71B4E2CA78C0"));
            if (normalized.Mode == DownloadMode.AudioMp3)
            {
                if (actual.Streams != 1 || actual.Audio is not { Codec: "mp3", Rate: 48000 } || actual.Audio.Channels != channels || actual.Audio.Bitrate != 192000)
                    throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_D5BAF2A57B21"));
            }
            else if (actual.Video is not { Codec: "h264" } || actual.Video.Width != width || actual.Video.Height != height
                || Math.Abs(actual.Video.Sar - 1) > .00001 || Math.Abs(actual.Video.Rotation) % 360 > .1
                || actual.Streams != (channels > 0 ? 2 : 1) || channels > 0 && (actual.Audio is not { Codec: "aac", Rate: 48000 } || actual.Audio.Channels != channels))
                throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_80632FDEEE20"));
            if (Math.Abs(actual.Duration - input.Duration) > Math.Max(1, input.Duration * .01))
                throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_79D9686F513A"));
            progress?.Report(new(.94, global::Orbit.Core.Localization.L.Get("Text_CCE2758CB7E6")));
            var check = Input(prepared); check.AddRange(["-map", "0:v:0?", "-map", "0:a:0?", "-f", "null", "-", "-progress", "pipe:1", "-stats_period", "1"]);
            double decoded = 0;
            var verification = await DownloadProcess.RunAsync(tools.Ffmpeg, check, working, token, line =>
            { if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && Number(line[12..]) is { } us) decoded = Math.Max(decoded, us / 1e6); }, 16 * 1024 * 1024).ConfigureAwait(false);
            if (verification.ExitCode != 0 || decoded <= 0 || Math.Abs(decoded - actual.Duration) > Math.Max(1, actual.Duration * .01))
                throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_97A0164D3841"));
            token.ThrowIfCancellationRequested();
            CheckDirectory(normalized.OutputDirectory);
            var output = Commit(prepared, normalized.OutputDirectory, SafeName(title), normalized.Mode == DownloadMode.AudioMp3 ? ".mp3" : ".mp4", token);
            progress?.Report(new(1, global::Orbit.Core.Localization.L.Get("Text_EF05D57959CF")));
            return new(output, bytes, normalized.Mode, normalized.Mode == DownloadMode.AudioMp3 ? "MP3" : "MP4", width, height, actual.Duration);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (monitoring?.Exception is { } failure) throw failure.GetBaseException();
            throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_46D862D7AB73"));
        }
        catch (DownloadException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException or TimeoutException or MediaProcessingException)
        { throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_90BE55F4368B")); }
        finally
        {
            monitorStop.Cancel();
            if (monitoring is not null) try { await monitoring.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
            if (working is not null) await CleanupAsync(working).ConfigureAwait(false);
        }
    }

    private const string InputFormats = "mov,mp4,m4a,3gp,3g2,mj2,matroska,webm,mp3,flac,wav,ogg,aac,mpeg,mpegts,avi,asf,aiff,ape,amr";
    private static List<string> Input(string path) => ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-xerror", "-threads", "2", "-max_alloc", "536870912", "-max_pixels", "40000000", "-protocol_whitelist", "file,pipe", "-format_whitelist", InputFormats, "-i", path];
    private static double? Number(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) && n >= 0 ? n : null;
    private static string ReadJsonString(string text) => JsonSerializer.Deserialize<string>(text) ?? throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_2A77D41FA650"));
    private static DownloadException NativeError(string error) => new(error.Contains("sign in", StringComparison.OrdinalIgnoreCase) || error.Contains("login", StringComparison.OrdinalIgnoreCase) || error.Contains("cookies", StringComparison.OrdinalIgnoreCase)
        ? global::Orbit.Core.Localization.L.Get("Text_AEF79CA3C85A") : error.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase) ? global::Orbit.Core.Localization.L.Get("Text_6E27B4F9138B") : global::Orbit.Core.Localization.L.Get("Text_45312702962A"));

    private sealed record Track(int Index, string Codec, int Width, int Height, int Rate, int Channels, long Bitrate, double Sar, double Rotation);
    private sealed record Probe(double Duration, int Streams, Track? Video, Track? Audio);
    private static async Task<Probe> ProbeAsync(string executable, string path, string working, CancellationToken token)
    {
        var r = await DownloadProcess.RunAsync(executable, ["-v", "error", "-max_alloc", "536870912", "-max_pixels", "40000000", "-skip_frame", "all", "-protocol_whitelist", "file,pipe", "-format_whitelist", InputFormats, "-show_streams", "-show_format", "-of", "json", path], working, token).ConfigureAwait(false);
        if (r.ExitCode != 0) throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_E9E3F2096B2E"));
        using var json = JsonDocument.Parse(r.Output, new JsonDocumentOptions { MaxDepth = 32 });
        var root = json.RootElement;
        if (!root.TryGetProperty("streams", out var streams) || streams.GetArrayLength() is 0 or > 128) throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_8F3E4C11E4BF"));
        Track? video = null, audio = null;
        foreach (var item in streams.EnumerateArray())
        {
            string type = Text(item, "codec_type");
            bool attached = item.TryGetProperty("disposition", out var disposition) && Int(disposition, "attached_pic") != 0;
            double sar = 1, rotation = 0; var ratio = Text(item, "sample_aspect_ratio").Split(':');
            if (ratio.Length == 2 && Number(ratio[0]) is > 0 and var numerator && Number(ratio[1]) is > 0 and var denominator) sar = numerator / denominator;
            if (item.TryGetProperty("side_data_list", out var data)) foreach (var row in data.EnumerateArray())
                if (row.TryGetProperty("rotation", out var angle) && angle.TryGetDouble(out var value) && double.IsFinite(value)) rotation = value;
            var track = new Track(Int(item, "index"), Text(item, "codec_name"), Int(item, "width"), Int(item, "height"), Int(item, "sample_rate"), Int(item, "channels"), Long(item, "bit_rate"), sar, rotation);
            if (type == "video" && !attached) video ??= track;
            if (type == "audio") audio ??= track;
        }
        double duration = root.TryGetProperty("format", out var format) ? Number(Text(format, "duration")) ?? 0 : 0;
        return new(duration, streams.GetArrayLength(), video, audio);
    }
    private static string Text(JsonElement value, string key) => value.TryGetProperty(key, out var p) ? p.ToString() : "";
    private static int Int(JsonElement value, string key) => int.TryParse(Text(value, key), CultureInfo.InvariantCulture, out var n) ? n : 0;
    private static long Long(JsonElement value, string key) => long.TryParse(Text(value, key), CultureInfo.InvariantCulture, out var n) ? n : 0;
    private static void ValidateInput(Probe input, DownloadMode mode)
    {
        if (input.Duration is <= 0 or > MaximumDurationSeconds || !double.IsFinite(input.Duration)) throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_031ADCB647CC"));
        if (mode == DownloadMode.AudioMp3 && input.Audio is null) throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_FCCA42B2E00B"));
        if (mode == DownloadMode.VideoMp4 && input.Video is null) throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_AB8F2534475E"));
        if (input.Audio is { } audio && (audio.Channels is < 1 or > 8 || audio.Rate is < 1 or > 192000)) throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_2EAB1319BCD7"));
        if (input.Video is { } video && (video.Width is < 1 or > 8192 || video.Height is < 1 or > 8192 || (long)video.Width * video.Height > 40000000 || video.Sar is < .01 or > 100))
            throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_0D8937FA0AE3"));
    }
    private static (int Width, int Height) Fit(Track video)
    {
        var w = video.Width * video.Sar; var h = (double)video.Height;
        var rotation = Math.Abs(video.Rotation) % 180;
        if (Math.Abs(rotation - 90) < .1) (w, h) = (h, w);
        else if (rotation > .1) throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_642B09FD2A26"));
        var scale = Math.Min(1, Math.Min(1920 / w, 1080 / h));
        return (Math.Max(2, (int)Math.Floor(w * scale / 2) * 2), Math.Max(2, (int)Math.Floor(h * scale / 2) * 2));
    }
    private static void CheckDirectory(string path)
    {
        if (!Directory.Exists(path)) throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_45C406D632F2"));
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_E931118077F8"));
    }
    private static string ContainedFile(string value, string working)
    {
        var full = Path.GetFullPath(value);
        if (!string.Equals(Path.GetDirectoryName(full), working, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith("source.", StringComparison.Ordinal) || (File.GetAttributes(full) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_7FBD0D3DAE03"));
        return full;
    }
    private static async Task<FileStream> OpenAsync(string path, CancellationToken token)
    {
        var opening = Task.Run(() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous), CancellationToken.None);
        try { return await opening.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); }
        catch { _ = opening.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); else _ = t.Exception; }, TaskScheduler.Default); throw; }
    }
    private static string SafeName(string? value)
    {
        var builder = new StringBuilder();
        foreach (var c in value ?? "download") builder.Append(char.IsControl(c) || Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        var name = builder.ToString().Trim().TrimEnd('.', ' ');
        if (name.Length > 160) name = name[..160].TrimEnd('.', ' ');
        if (name.Length > 0 && char.IsHighSurrogate(name[^1])) name = name[..^1];
        if (string.IsNullOrEmpty(name) || name is "." or "..") name = "download";
        var first = name.Split('.')[0];
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(first, StringComparer.OrdinalIgnoreCase)) name = "download_" + name;
        return name;
    }
    private static string Commit(string prepared, string parent, string stem, string extension, CancellationToken token)
    {
        for (int i = 1; i <= 10000; i++)
        {
            token.ThrowIfCancellationRequested(); var path = Path.Combine(parent, stem + (i == 1 ? "" : " (" + i.ToString(CultureInfo.InvariantCulture) + ")") + extension);
            try { File.Move(prepared, path, false); return path; }
            catch (IOException) when (File.Exists(path) || Directory.Exists(path)) { }
        }
        throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_7D0DAD0A78C1"));
    }
    private static async Task MonitorAsync(string working, CancellationTokenSource deadline, CancellationToken stop)
    {
        try
        {
            while (true)
            {
                await Task.Delay(250, stop).ConfigureAwait(false);
                try { await Task.Run(() =>
                {
                    long total = 0; int count = 0; var pending = new Stack<string>(); pending.Push(working);
                    while (pending.TryPop(out var directory)) foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                    {
                        stop.ThrowIfCancellationRequested(); if (++count > 20000) throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_2CF59BD9A86C"));
                        try
                        {
                            var attributes = File.GetAttributes(path);
                            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_5E2406E82AED"));
                            if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                            else { total = checked(total + new FileInfo(path).Length); if (total > MaximumStagingBytes) throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_0C4CD0E715D7")); }
                        }
                        catch (FileNotFoundException) { /* A fragment was renamed; next pass enumerates its replacement. */ }
                    }
                }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3), stop).ConfigureAwait(false); }
                catch (DirectoryNotFoundException) when (Directory.Exists(working)) { /* A temporary child was renamed; keep monitoring. */ }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch { deadline.Cancel(); throw; }
    }
    private static async Task CleanupAsync(string working)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            var deleting = Task.Run(() => { if (Directory.Exists(working)) Directory.Delete(working, true); }, CancellationToken.None);
            try { await deleting.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); return; }
            catch (TimeoutException) { _ = deleting.ContinueWith(t => _ = t.Exception, TaskScheduler.Default); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { await Task.Delay(100).ConfigureAwait(false); }
        }
    }
    private static string Resolve(string? configured, string environment, string folder, string executable)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? Path.GetFullPath(configured) : throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_87A10F4AAE2E"));
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", folder, executable); if (File.Exists(bundled)) return bundled;
        var env = Environment.GetEnvironmentVariable(environment); if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return Path.GetFullPath(env);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        { try { var path = Path.Combine(directory.Trim('"'), executable); if (File.Exists(path)) return Path.GetFullPath(path); } catch (ArgumentException) { } }
        throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_F94CB52FB254") + folder + global::Orbit.Core.Localization.L.Get("Text_70DCCA911850"));
    }
}
