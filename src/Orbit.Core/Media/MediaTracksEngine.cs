using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Orbit.Core.Media;

/// <summary>Read-only, SHA256-bound stream catalogs and explicit native-index audio extraction.</summary>
public sealed class MediaTracksEngine
{
    public const long MaximumSourceBytes = 16L * 1024 * 1024 * 1024;
    public const long MaximumOutputBytes = 2L * 1024 * 1024 * 1024;
    public const int MaximumStreams = 128;
    public const int MaximumProbeBytes = 4 * 1024 * 1024;
    public const double MaximumDurationSeconds = 86400;
    private static readonly int[] Mp3Rates = [8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000];
    private static readonly int[] Mp3Bitrates = [64, 96, 128, 160, 192, 256, 320];
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mp4", ".mov", ".mkv", ".mka", ".webm", ".avi", ".m4v", ".mpeg", ".mpg", ".ts", ".mts", ".m2ts", ".wmv", ".3gp", ".3g2", ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".aif", ".aiff" };
    private static readonly HashSet<string> Containers = new(StringComparer.Ordinal)
    { "mov", "mp4", "m4a", "3gp", "3g2", "mj2", "matroska", "webm", "avi", "asf", "mpeg", "mpegts", "m4v", "mp3", "wav", "flac", "aac", "ogg", "aiff" };
    private readonly MediaEngine _media;
    private readonly TimeSpan _timeout;
    internal Action<string>? NativeStarted { get; set; }

    public MediaTracksEngine(string? ffmpegExecutable = null, TimeSpan? operationTimeout = null)
    {
        _media = new MediaEngine(ffmpegExecutable); _timeout = operationTimeout ?? TimeSpan.FromHours(2);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromHours(24)) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
    }
    public string ExecutablePath => _media.ExecutablePath;
    public string ProbeExecutablePath => _media.ProbeExecutablePath;

    public async Task<MediaTrackCatalog> InspectAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var deadline = Deadline(ct);
        try
        {
            var source = NormalizePath(path); await using var lease = await OpenAsync(source, deadline.Token).ConfigureAwait(false);
            var catalog = await ProbeAsync(source, lease.Length, deadline.Token).ConfigureAwait(false);
            return catalog with { SourceSha256 = await HashAsync(lease, deadline.Token).ConfigureAwait(false) };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw TimeoutError(); }
        catch (Exception exception) when (Expected(exception)) { throw Explain(exception); }
    }

    public async Task<MediaResult> ExtractAudioAsync(string path, AudioTrackExtractOptions options, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options); ct.ThrowIfCancellationRequested();
        if (options.StreamIndex is < 0 or > 65535) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_B104837D676B"));
        if (options.SourceSha256 is null || options.SourceSha256.Length != 64 || !options.SourceSha256.All(Uri.IsHexDigit)) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_8841F1F98E29"));
        if (options.Format is not ("MP3" or "WAV" or "FLAC")) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_C72DA7B878E5"));
        if (!Mp3Bitrates.Contains(options.BitrateKbps)) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_9007429E067C"));
        using var deadline = Deadline(ct); var token = deadline.Token; string? temporary = null, parent = null;
        try
        {
            var source = NormalizePath(path); progress?.Report(0); await using var lease = await OpenAsync(source, token).ConfigureAwait(false);
            var hash = await HashAsync(lease, token).ConfigureAwait(false);
            if (!hash.Equals(options.SourceSha256, StringComparison.OrdinalIgnoreCase)) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_D0FB67E46E46"));
            var catalog = await ProbeAsync(source, lease.Length, token).ConfigureAwait(false);
            var selected = catalog.Streams.SingleOrDefault(stream => stream.StreamIndex == options.StreamIndex);
            if (selected is not { Type: "Audio", IsAttachedPicture: false } || selected.Codec is "" or "unknown")
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_E25E6387EA0F"));
            if (selected.Channels is < 1 or > 8 || selected.SampleRate is < 1 or > 192000) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_A8BBF770AED2"));
            var sourceSamples = await MeasureAsync(source, selected.StreamIndex, selected.SampleRate, token).ConfigureAwait(false);
            var sourceDuration = sourceSamples / selected.SampleRate;
            if (sourceSamples <= 0 || !double.IsFinite(sourceDuration) || sourceDuration > MaximumDurationSeconds) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_D2BB7F3D6EC2"));
            var mp3 = options.Format == "MP3";
            // LAME silently caps MPEG2.5 rates at64kbps and MPEG2 at160kbps.
            // Choose the nearest rate whose MPEG version can represent the requested CBR.
            var minimumMp3Rate = options.BitrateKbps > 160 ? 32000 : options.BitrateKbps > 64 ? 16000 : 8000;
            var rate = mp3 ? Mp3Rates.Where(value => value >= minimumMp3Rate).MinBy(value => Math.Abs(value - selected.SampleRate)) : selected.SampleRate;
            var channels = mp3 ? Math.Min(selected.Channels, 2) : selected.Channels;
            if (options.Format == "WAV" && sourceSamples * channels * 3 + 4096 > MaximumOutputBytes) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_A6FE2339A0DB"));
            progress?.Report(.2);
            parent = Path.TrimEndingDirectorySeparator(string.IsNullOrWhiteSpace(options.OutputDirectory) ? Path.GetDirectoryName(source)! : Path.GetFullPath(options.OutputDirectory));
            if (!await Task.Run(() => Directory.Exists(parent), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false)) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_45C406D632F2"));
            temporary = Path.Combine(parent, ".orbit-media-track-" + Guid.NewGuid().ToString("N")); var working = temporary; var directory = parent;
            var creating = Task.Run(() => Directory.CreateDirectory(working), CancellationToken.None);
            try { await creating.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); }
            catch
            {
                temporary = null; _ = creating.ContinueWith(async task => { if (task.IsCompletedSuccessfully) await CleanupAsync(working, directory).ConfigureAwait(false); else _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap(); throw;
            }
            var extension = options.Format.ToLowerInvariant(); var prepared = Path.Combine(temporary, "result." + extension); var args = Input(source, selected.StreamIndex);
            args.AddRange(["-af", "asetpts=N/SR/TB", "-ar", rate.ToString(CultureInfo.InvariantCulture), "-ac", channels.ToString(CultureInfo.InvariantCulture)]);
            if (mp3) args.AddRange(["-c:a", "libmp3lame", "-b:a", options.BitrateKbps.ToString(CultureInfo.InvariantCulture) + "k", "-write_xing", "1"]);
            else if (options.Format == "WAV") args.AddRange(["-c:a", "pcm_s24le"]);
            else args.AddRange(["-c:a", "flac", "-sample_fmt", "s32", "-bits_per_raw_sample", "24"]);
            args.AddRange(["-map_metadata", "-1", "-map_metadata:s:a:0", "-1", "-map_chapters", "-1", "-metadata", "title=", "-metadata:s:a:0", "title=", "-metadata:s:a:0", "language=",
                "-t", Number(MaximumDurationSeconds + 1), "-fs", (MaximumOutputBytes + 1).ToString(CultureInfo.InvariantCulture), "-progress", "pipe:1", "-stats_period", "0.1", "-f", options.Format == "WAV" ? "wav" : extension, prepared]);
            double last = .2; Recheck(source, token);
            await RunAsync(ExecutablePath, args, token, line =>
            {
                if (!line.StartsWith("out_time_us=", StringComparison.Ordinal) || !long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us)) return;
                var value = .2 + .6 * Math.Clamp(us / 1_000_000d / sourceDuration, 0, 1); if (value > last) { last = value; progress?.Report(value); }
            }).ConfigureAwait(false);
            var bytes = new FileInfo(prepared).Length;
            if (bytes is <= 0 or > MaximumOutputBytes) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_0639A6098545"));
            progress?.Report(.85); var output = await ProbeAsync(prepared, bytes, token).ConfigureAwait(false);
            var actual = output.Streams.SingleOrDefault(); var codec = mp3 ? "mp3" : options.Format == "WAV" ? "pcm_s24le" : "flac";
            if (actual is not { Type: "Audio", IsAttachedPicture: false } || actual.Codec != codec || actual.SampleRate != rate || actual.Channels != channels || output.Format != options.Format
                || actual.Language.Length != 0 || actual.Title.Length != 0 || mp3 && actual.Bitrate != options.BitrateKbps * 1000L)
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_48EFDDEAF84A"));
            var outputSamples = await MeasureAsync(prepared, actual.StreamIndex, rate, token).ConfigureAwait(false);
            var duration = outputSamples / rate; var decodeTolerance = Math.Max(.001, 2d / rate + 2d / selected.SampleRate);
            if (outputSamples <= 0 || (mp3 ? Math.Abs(duration - sourceDuration) > decodeTolerance : outputSamples != sourceSamples)
                || Math.Abs(actual.DurationSeconds - sourceDuration) > (mp3 ? 2304d / rate + .01 : .001 + 2d / rate))
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_ECBD63E9E40A"));
            token.ThrowIfCancellationRequested(); var published = Commit(prepared, parent, source, selected.StreamIndex, extension, token); progress?.Report(1);
            return new MediaResult(published, bytes, 0, 0, options.Format, lease.Length);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw TimeoutError(); }
        catch (Exception exception) when (Expected(exception)) { throw Explain(exception); }
        finally { await CleanupAsync(temporary, parent).ConfigureAwait(false); }
    }

    private async Task<MediaTrackCatalog> ProbeAsync(string path, long bytes, CancellationToken token)
    {
        var raw = await RunAsync(ProbeExecutablePath, ["-v", "error", "-protocol_whitelist", "file,pipe", "-show_entries",
            "format=format_name,duration:format_tags=major_brand:stream=index,codec_type,codec_name,channels,sample_rate,width,height,duration,start_time,bit_rate,initial_padding:stream_tags=language,title,DURATION,duration:stream_disposition=attached_pic,default", "-of", "json", "-i", path], token).ConfigureAwait(false);
        try
        {
            using var json = JsonDocument.Parse(raw); var root = json.RootElement; var format = root.GetProperty("format"); var names = Text(format, "format_name").Split(',');
            if (!names.Any(Containers.Contains)) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_2EF6D654A650"));
            var native = root.GetProperty("streams"); if (native.GetArrayLength() is < 1 or > MaximumStreams) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_9FBE5231AD36"));
            var tracks = new List<MediaTrack>(); var indices = new HashSet<int>();
            foreach (var stream in native.EnumerateArray())
            {
                token.ThrowIfCancellationRequested(); var index = Integer(stream, "index", -1);
                if (index is < 0 or > 65535 || !indices.Add(index)) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_05920CE86EA7"));
                var type = Text(stream, "codec_type") switch { "audio" => "Audio", "video" => "Video", "subtitle" => "Subtitle", "data" => "Data", "attachment" => "Attachment", _ => throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_39A00E68B6EA")) };
                var codec = Label(Text(stream, "codec_name"), 64); if (codec.Length == 0) codec = "unknown";
                var language = ""; var title = "";
                if (stream.TryGetProperty("tags", out var tags)) { language = Label(Text(tags, "language"), 64); title = Label(Text(tags, "title"), 1024); }
                var disposition = stream.TryGetProperty("disposition", out var d) ? d : default;
                var duration = StreamDuration(stream); var rate = Integer(stream, "sample_rate");
                if (codec == "opus" && rate > 0) duration = Math.Max(0, duration - Numeric(stream, "initial_padding") / rate);
                if (duration > MaximumDurationSeconds) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_E58619AB2734"));
                tracks.Add(new MediaTrack(index, type, codec, language, title, Integer(stream, "channels"), rate, Integer(stream, "width"), Integer(stream, "height"), duration,
                    long.TryParse(Text(stream, "bit_rate"), NumberStyles.None, CultureInfo.InvariantCulture, out var bitrate) && bitrate >= 0 ? bitrate : 0,
                    disposition.ValueKind == JsonValueKind.Object && Integer(disposition, "attached_pic") != 0, disposition.ValueKind == JsonValueKind.Object && Integer(disposition, "default") != 0));
            }
            if (!tracks.Any(track => track.Type == "Audio" && track.Codec != "unknown" || track.Type == "Video" && !track.IsAttachedPicture && track.Width > 0 && track.Height > 0 && track.Codec != "unknown"))
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_83C0411177ED"));
            var durationAll = Math.Max(Numeric(format, "duration"), tracks.Max(track => track.DurationSeconds));
            if (!double.IsFinite(durationAll) || durationAll > MaximumDurationSeconds) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_6CED4D71B46F"));
            var brand = format.TryGetProperty("tags", out var formatTags) ? Text(formatTags, "major_brand").Trim() : "";
            var display = names.Contains("mov") ? brand == "qt" ? "MOV" : brand.StartsWith("M4A", StringComparison.OrdinalIgnoreCase) ? "M4A" : "MP4" : names.Contains("matroska") ? "MATROSKA/WEBM" : names[0].ToUpperInvariant();
            return new MediaTrackCatalog(path, "", bytes, display, durationAll, Array.AsReadOnly(tracks.OrderBy(track => track.StreamIndex).ToArray()));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_5BB5F6DB9199"), exception); }
    }

    private async Task<double> MeasureAsync(string path, int index, int rate, CancellationToken token)
    {
        if (rate <= 0) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_F07A56ACD4F1"));
        double samples = 0; var args = Input(path, index);
        args.AddRange(["-af", "astats=metadata=1:reset=0:measure_perchannel=none:measure_overall=Number_of_samples,ametadata=print:key=lavfi.astats.Overall.Number_of_samples:file=-:direct=1,asetpts=N/SR/TB",
            "-t", Number(MaximumDurationSeconds + 1), "-c:a", "pcm_f32le", "-f", "null", "-"]);
        await RunAsync(ExecutablePath, args, token, line =>
        {
            const string prefix = "lavfi.astats.Overall.Number_of_samples=";
            if (line.StartsWith(prefix, StringComparison.Ordinal) && double.TryParse(line.AsSpan(prefix.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)) samples = Math.Max(samples, value);
        }).ConfigureAwait(false); return samples;
    }

    private static List<string> Input(string path, int index) => ["-hide_banner", "-v", "error", "-nostdin", "-n", "-nostats", "-xerror", "-threads", "2", "-filter_threads", "2",
        "-protocol_whitelist", "file,pipe", "-i", path, "-map", "0:" + index.ToString(CultureInfo.InvariantCulture), "-vn", "-sn", "-dn"];
    private CancellationTokenSource Deadline(CancellationToken ct) { var result = CancellationTokenSource.CreateLinkedTokenSource(ct); result.CancelAfter(_timeout); return result; }
    private static async Task<string> HashAsync(FileStream input, CancellationToken token)
    { input.Position = 0; var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)); input.Position = 0; return hash; }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_9353B258D26C"));
        var full = Path.GetFullPath(path).Replace('/', Path.DirectorySeparatorChar);
        if (full.Length > 4096 || !Extensions.Contains(Path.GetExtension(full)) || full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal)
            || full.IndexOf(':', full.Length > 1 && full[1] == ':' ? 2 : 0) >= 0) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_801BABA95C57"));
        return full;
    }
    private static void Recheck(string path, CancellationToken token)
    {
        var depth = 0;
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            token.ThrowIfCancellationRequested(); if (++depth > 256) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_CBE1C8425D29")); var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || depth == 1 && (attributes & FileAttributes.Directory) != 0) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_889E61A331D5"));
        }
    }
    private static async Task<FileStream> OpenAsync(string path, CancellationToken token)
    {
        var opening = Task.Run(() =>
        {
            Recheck(path, token); var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.Asynchronous);
            try
            {
                Recheck(path, token); if (input.Length is <= 0 or > MaximumSourceBytes || (File.GetAttributes(input.SafeFileHandle) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_1FBF45A594D2")); return input;
            }
            catch { input.Dispose(); throw; }
        }, CancellationToken.None);
        try { return await opening.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); }
        catch { _ = opening.ContinueWith(task => { if (task.IsCompletedSuccessfully) task.Result.Dispose(); else _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default); throw; }
    }

    private static string Commit(string prepared, string directory, string source, int index, string extension, CancellationToken token)
    {
        var stem = Path.GetFileNameWithoutExtension(source); if (stem.Length > 180) { stem = stem[..180]; if (char.IsHighSurrogate(stem[^1])) stem = stem[..^1]; } stem += "_track-" + index.ToString(CultureInfo.InvariantCulture);
        for (var collision = 0; collision < 10000; collision++)
        {
            token.ThrowIfCancellationRequested(); var target = Path.Combine(directory, stem + (collision == 0 ? "" : "_" + collision.ToString(CultureInfo.InvariantCulture)) + "." + extension);
            if (target.Equals(source, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Move(prepared, target, false); return target; } catch (IOException) when (File.Exists(target) || Directory.Exists(target)) { }
        }
        throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_B317BD922F3D"));
    }

    private async Task<string> RunAsync(string executable, IReadOnlyList<string> args, CancellationToken token, Action<string>? callback = null)
    {
        token.ThrowIfCancellationRequested(); var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg); using var process = new Process { StartInfo = start };
        try { if (!process.Start()) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_55B839B558FD")); }
        catch (System.ComponentModel.Win32Exception exception) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_64D94FFD1025"), exception); }
        var stdout = callback is null ? BytesAsync(process.StandardOutput.BaseStream, MaximumProbeBytes, token) : LinesAsync(process.StandardOutput, callback, token);
        var stderr = BytesAsync(process.StandardError.BaseStream, 128 * 1024, token);
        try
        {
            NativeStarted?.Invoke(string.Join(' ', args)); await process.WaitForExitAsync(token).ConfigureAwait(false); await Task.WhenAll(stdout, stderr).WaitAsync(token).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false); var error = await stderr.ConfigureAwait(false); token.ThrowIfCancellationRequested();
            if (output.Length > MaximumProbeBytes || error.Length > 128 * 1024) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_F06AE2FA7319"));
            var details = Encoding.UTF8.GetString(error);
            if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(details)) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_96DCAF3A5F98") + details[..Math.Min(details.Length, 700)].Trim());
            return new UTF8Encoding(false, true).GetString(output);
        }
        finally
        {
            if (!process.HasExited) { try { process.Kill(true); } catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (Exception exception) when (exception is TimeoutException or InvalidOperationException) { }
            var drains = Task.WhenAll(stdout, stderr); try { await drains.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (TimeoutException) { process.StandardOutput.Dispose(); process.StandardError.Dispose(); Observe(drains); }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException) { }
        }
    }
    private static async Task<byte[]> BytesAsync(Stream input, int maximum, CancellationToken token)
    {
        using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0) if (output.Length <= maximum) output.Write(buffer, 0, (int)Math.Min(count, maximum + 1 - output.Length)); return output.ToArray();
    }
    private static async Task<byte[]> LinesAsync(StreamReader input, Action<string> callback, CancellationToken token)
    {
        var line = new StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
            for (var i = 0; i < count; i++) if (buffer[i] == '\n') { callback(line.ToString().TrimEnd('\r')); line.Clear(); } else if (line.Length < 4096) line.Append(buffer[i]);
        if (line.Length != 0) callback(line.ToString()); return [];
    }
    private static async Task CleanupAsync(string? temporary, string? parent)
    {
        if (temporary is null || parent is null || !string.Equals(Path.GetDirectoryName(temporary), Path.TrimEndingDirectorySeparator(parent), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(temporary).StartsWith(".orbit-media-track-", StringComparison.Ordinal)) return;
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
    private static string Label(string value, int maximum)
    { var result = new string(value.Where(character => !char.IsControl(character)).Take(maximum).ToArray()); return result.Length > 0 && char.IsHighSurrogate(result[^1]) ? result[..^1] : result; }
    private static string Text(JsonElement item, string key) => item.TryGetProperty(key, out var value) ? value.ToString() : "";
    private static int Integer(JsonElement item, string key, int fallback = 0) => int.TryParse(Text(item, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    private static double Numeric(JsonElement item, string key) => double.TryParse(Text(item, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value >= 0 ? value : 0;
    private static double StreamDuration(JsonElement stream)
    {
        var duration = Numeric(stream, "duration"); if (duration > 0) return duration; if (!stream.TryGetProperty("tags", out var tags)) return 0;
        var text = Text(tags, "DURATION"); if (text.Length == 0) text = Text(tags, "duration"); var parts = text.Split(':');
        if (parts.Length != 3 || !double.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) || !double.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || minutes is < 0 or >= 60 || seconds is < 0 or >= 60) return 0;
        var result = hours * 3600 + minutes * 60 + seconds - Numeric(stream, "start_time"); return double.IsFinite(result) && result > 0 ? result : 0;
    }
    private static string Number(double value) => value.ToString("0.###############", CultureInfo.InvariantCulture);
    private static bool Expected(Exception exception) => exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or TimeoutException or System.Security.SecurityException or DecoderFallbackException;
    private static MediaProcessingException Explain(Exception exception) => new(global::Orbit.Core.Localization.L.Get("Text_2E183A027044"), exception);
    private static MediaProcessingException TimeoutError() => new(global::Orbit.Core.Localization.L.Get("Text_50096171C1E2"));
    private static void Observe(Task task) => _ = task.ContinueWith(completed => _ = completed.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
