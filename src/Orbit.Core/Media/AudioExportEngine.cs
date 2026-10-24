using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Orbit.Core.Media;

/// <summary>First audio track to new AAC/M4A, Ogg/Opus or Ogg/Vorbis. Cover art is omitted;
/// mono/stereo are retained and multichannel inputs are explicitly downmixed to stereo.</summary>
public sealed class AudioExportEngine
{
    public const long MaximumSourceBytes = 2L * 1024 * 1024 * 1024;
    public const long MaximumOutputBytes = 2L * 1024 * 1024 * 1024;
    public const double MaximumDurationSeconds = 86400;
    public const int MaximumChannels = 8;
    public const int MaximumSampleRate = 192000;
    private static readonly int[] AacRates = [7350, 8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000, 64000, 88200, 96000];
    private static readonly int[] AacBitrates = [64, 96, 128, 160, 192, 256, 320];
    private static readonly int[] OpusBitrates = [32, 64, 96, 128, 160, 192, 256];
    private readonly MediaEngine _media;
    private readonly TimeSpan _timeout;
    internal Action<string>? NativeStarted { get; set; }

    public AudioExportEngine(string? ffmpegExecutable = null, TimeSpan? operationTimeout = null)
    {
        _media = new MediaEngine(ffmpegExecutable); _timeout = operationTimeout ?? TimeSpan.FromHours(2);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromHours(24)) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
    }

    public string ExecutablePath => _media.ExecutablePath;
    public string ProbeExecutablePath => _media.ProbeExecutablePath;

    public async Task<MediaResult> ExportAsync(string path, AudioExportOptions options, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options); ct.ThrowIfCancellationRequested();
        if (options.Format is not ("M4A" or "OPUS" or "VORBIS")) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_6B7CBDFA0657"));
        var opus = options.Format == "OPUS";var vorbis = options.Format == "VORBIS";
        if (options.VorbisQuality is < 0 or > 10) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_EB0D38CADE7B"));
        if (!(opus ? OpusBitrates : AacBitrates).Contains(options.BitrateKbps))
            throw new MediaProcessingException(opus ? global::Orbit.Core.Localization.L.Get("Text_E30D95E90882") : global::Orbit.Core.Localization.L.Get("Text_9AE1FE4CF334"));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(_timeout);
        var token = deadline.Token; string? temporary = null, parent = null;
        try
        {
            var source = NormalizePath(path); progress?.Report(0);
            await using var lease = await OpenInputAsync(source, token).ConfigureAwait(false);
            var input = await ProbeAsync(source, token).ConfigureAwait(false); ValidateSource(input, Path.GetExtension(source));
            var codec = opus ? "libopus" : vorbis ? "libvorbis" : "aac";
            var codecHelp = await RunAsync(ExecutablePath, ["-hide_banner", "-h", "encoder=" + codec], token).ConfigureAwait(false);
            if (!codecHelp.Output.Contains("Encoder " + codec + " ", StringComparison.Ordinal))
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_92894F6E4139") + codec + global::Orbit.Core.Localization.L.Get("Text_CF522FBF708B"));
            var sourceDuration = await MeasureAsync(source, input.Rate, token).ConfigureAwait(false);
            if (!double.IsFinite(sourceDuration) || sourceDuration is <= 0 or > MaximumDurationSeconds)
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_8FF50B6773B9"));
            progress?.Report(.15);
            var rate = opus ? 48000 : vorbis ? Math.Max(8000, input.Rate) : AacRates.MinBy(candidate => Math.Abs(candidate - input.Rate));
            var channels = Math.Min(input.Channels, 2);
            parent = Path.TrimEndingDirectorySeparator(string.IsNullOrWhiteSpace(options.OutputDirectory) ? Path.GetDirectoryName(source)! : Path.GetFullPath(options.OutputDirectory));
            if (!await Task.Run(() => Directory.Exists(parent), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false))
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_45C406D632F2"));
            temporary = Path.Combine(parent, ".orbit-audio-export-" + Guid.NewGuid().ToString("N"));
            var working = temporary; var directory = parent; var creating = Task.Run(() => Directory.CreateDirectory(working), CancellationToken.None);
            try { await creating.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); }
            catch
            {
                temporary = null;
                _ = creating.ContinueWith(async task => { if (task.IsCompletedSuccessfully) await CleanupAsync(working, directory).ConfigureAwait(false); else _ = task.Exception; },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap(); throw;
            }
            var prepared = Path.Combine(temporary, opus ? "result.opus" : vorbis ? "result.ogg" : "result.m4a"); var args = Input(source);
            args.AddRange(["-af", "asetpts=N/SR/TB", "-c:a", codec, "-ar", rate.ToString(CultureInfo.InvariantCulture), "-ac", channels.ToString(CultureInfo.InvariantCulture),
                "-map_metadata", "-1", "-map_chapters", "-1", "-t", Number(MaximumDurationSeconds + 1),
                "-fs", (MaximumOutputBytes + 1).ToString(CultureInfo.InvariantCulture), "-progress", "pipe:1", "-stats_period", "0.1"]);
            if (vorbis)
            {
                // Flushing each audio packet gives the demuxer the first granule separately.
                // Larger pages can make FFmpeg discard the tail of short Vorbis recordings.
                args.AddRange(["-q:a", options.VorbisQuality.ToString(CultureInfo.InvariantCulture), "-page_duration", "1", "-f", "ogg"]);
            }
            else args.AddRange(["-b:a", options.BitrateKbps.ToString(CultureInfo.InvariantCulture) + "k"]);
            if (opus) args.AddRange(["-application", "audio", "-vbr", "on", "-f", "opus"]);
            else if (!vorbis) args.AddRange(["-movflags", "+faststart", "-f", "ipod"]);
            args.Add(prepared); double last = .15;
            Recheck(source, token);
            await RunAsync(ExecutablePath, args, token, line =>
            {
                if (!line.StartsWith("out_time_us=", StringComparison.Ordinal) || !long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us)) return;
                var current = .15 + .68 * Math.Clamp(us / 1_000_000d / sourceDuration, 0, 1);
                if (current > last) { last = current; progress?.Report(current); }
            }).ConfigureAwait(false);
            var bytes = new FileInfo(prepared).Length;
            if (bytes is <= 0 or > MaximumOutputBytes) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_035F5D0EFCD7"));
            progress?.Report(.85);
            var actual = await ProbeAsync(prepared, token).ConfigureAwait(false);
            var decodedDuration = await MeasureAsync(prepared, actual.Rate, token).ConfigureAwait(false);
            // AAC emits complete1024-sample frames; its last decoded frame may include padding
            // even when the M4A edit-list duration is exact. Opus granules trim its padding.
            var tolerance = vorbis ? 2d / input.Rate + 2d / rate : opus ? Math.Max(.005, 4d / input.Rate + 4d / rate) : Math.Max(.025, 2048d / rate + 1d / input.Rate);
            if (actual.Streams != 1 || actual.Codec != (opus ? "opus" : vorbis ? "vorbis" : "aac") || actual.Rate != rate || actual.Channels != channels
                || !(opus || vorbis ? actual.Container.Split(',').Contains("ogg", StringComparer.Ordinal) : actual.Container.Split(',').Contains("mov", StringComparer.Ordinal) && actual.Brand.StartsWith("M4A", StringComparison.Ordinal))
                || !double.IsFinite(decodedDuration) || decodedDuration <= 0 || Math.Abs(decodedDuration - sourceDuration) > tolerance
                || actual.Duration <= 0 || Math.Abs(actual.Duration - sourceDuration) > tolerance || Math.Abs(actual.Start) > tolerance)
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_E443E69FAAF6"));
            token.ThrowIfCancellationRequested(); var published = Commit(prepared, parent, source, options.Format, token);
            progress?.Report(1); return new MediaResult(published, bytes, 0, 0, vorbis ? "OGG" : options.Format, lease.Length);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_6AF06A9DFD81")); }
        catch (TimeoutException exception) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_4E0DDE44AEA4"), exception); }
        catch (UnauthorizedAccessException exception) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_743335624268"), exception); }
        catch (IOException exception) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_25225EEDCDE9"), exception); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or System.Security.SecurityException)
        { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_83CE68344A04"), exception); }
        finally { await CleanupAsync(temporary, parent).ConfigureAwait(false); }
    }

    private async Task<double> MeasureAsync(string path, int rate, CancellationToken token)
    {
        if (rate <= 0) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_206EDA1BBD3A"));
        double samples = 0; var args = Input(path);
        args.AddRange(["-af", "astats=metadata=1:reset=0:measure_perchannel=none:measure_overall=Number_of_samples,ametadata=print:key=lavfi.astats.Overall.Number_of_samples:file=-:direct=1,asetpts=N/SR/TB",
            "-t", Number(MaximumDurationSeconds + 1), "-c:a", "pcm_f32le", "-f", "null", "-"]);
        await RunAsync(ExecutablePath, args, token, line =>
        {
            const string prefix = "lavfi.astats.Overall.Number_of_samples=";
            if (line.StartsWith(prefix, StringComparison.Ordinal) && double.TryParse(line.AsSpan(prefix.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)) samples = Math.Max(samples, value);
        }).ConfigureAwait(false);
        return samples / rate;
    }

    private async Task<AudioInfo> ProbeAsync(string path, CancellationToken token)
    {
        var run = await RunAsync(ProbeExecutablePath, ["-v", "error", "-protocol_whitelist", "file,pipe", "-show_entries",
            "format=format_name,duration:format_tags=major_brand:stream=codec_type,codec_name,sample_rate,channels,duration,start_time,initial_padding:stream_disposition=attached_pic:stream_tags=DURATION,duration", "-of", "json", "-i", path], token).ConfigureAwait(false);
        try
        {
            using var json = JsonDocument.Parse(run.Output); var root = json.RootElement; var format = root.GetProperty("format"); JsonElement? first = null; var streams = root.GetProperty("streams");
            foreach (var stream in streams.EnumerateArray())
            {
                var kind = Text(stream, "codec_type");
                if (kind == "video" && (!stream.TryGetProperty("disposition", out var disposition) || Numeric(disposition, "attached_pic") == 0))
                    throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_2F66D2396B55"));
                if (kind == "audio" && first is null) first = stream;
            }
            if (first is not { } audio) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_08621B189DEB"));
            var duration = Numeric(audio, "duration");
            if (duration <= 0 && audio.TryGetProperty("tags", out var tags))
            {
                var text = Text(tags, "DURATION"); if (text.Length == 0) text = Text(tags, "duration"); var parts = text.Split(':');
                if (parts.Length == 3 && double.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
                    && double.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                    && minutes is >= 0 and < 60 && seconds is >= 0 and < 60) duration = hours * 3600 + minutes * 60 + seconds - Numeric(audio, "start_time");
            }
            if (duration <= 0) duration = Numeric(format, "duration");
            // Ogg's final granule includes OpusHead pre-skip (normally312samples).
            // ffprobe reports3.0065s for exactly3s of decoded48kHz audio.
            var rate = Numeric(audio, "sample_rate");
            if (Text(audio, "codec_name") == "opus" && rate > 0) duration -= Numeric(audio, "initial_padding") / rate;
            return new AudioInfo(Text(format, "format_name"), format.TryGetProperty("tags", out var formatTags) ? Text(formatTags, "major_brand") : "",
                Text(audio, "codec_name"), (int)rate, (int)Numeric(audio, "channels"), duration, Numeric(audio, "start_time"), streams.GetArrayLength());
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_CE0CA2DDE2CB"), exception); }
    }

    private static void ValidateSource(AudioInfo input, string extension)
    {
        var allowed = new[] { "mov", "mp4", "m4a", "3gp", "3g2", "mj2", "matroska", "webm", "mp3", "wav", "flac", "aac", "ogg", "asf", "aiff" };
        if (!input.Container.Split(',').Any(allowed.Contains) || input.Codec is "" or "unknown") throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_9034645365E9"));
        var family = extension.ToLowerInvariant() switch
        {
            ".wav" => "wav", ".flac" => "flac", ".mp3" => "mp3", ".aac" => "aac", ".ogg" or ".opus" => "ogg",
            ".wma" => "asf", ".aif" or ".aiff" => "aiff", ".mkv" or ".mka" or ".webm" => "matroska", _ => "mov"
        };
        if (!input.Container.Split(',').Contains(family, StringComparer.Ordinal) || extension.Equals(".opus", StringComparison.OrdinalIgnoreCase) && input.Codec != "opus")
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_4141C93690B0"));
        if (input.Rate is < 1 or > MaximumSampleRate || input.Channels is < 1 or > MaximumChannels) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_ABCE57516D9B"));
        if (!double.IsFinite(input.Duration) || input.Duration > MaximumDurationSeconds) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_59BB57090CB6"));
    }

    private static List<string> Input(string path) => ["-hide_banner", "-v", "error", "-nostdin", "-n", "-nostats", "-xerror", "-threads", "2", "-filter_threads", "2",
        "-protocol_whitelist", "file,pipe", "-i", path, "-map", "0:a:0", "-vn", "-sn", "-dn"];

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path); var full = Path.GetFullPath(path).Replace('/', Path.DirectorySeparatorChar);
        if (full.Length > 4096 || full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal)
            || full.IndexOf(':', full.Length > 1 && full[1] == ':' ? 2 : 0) >= 0)
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_0C9CDEF5F3FA"));
        return full;
    }

    private static bool Signature(byte[] header, int count, string extension)
    {
        bool Starts(string value) => header.AsSpan(0, count).StartsWith(Encoding.ASCII.GetBytes(value));
        return extension.ToLowerInvariant() switch
        {
            ".wav" => count >= 12 && (Starts("RIFF") || Starts("RF64") || Starts("BW64")) && header.AsSpan(8, 4).SequenceEqual("WAVE"u8),
            ".flac" => Starts("fLaC"),
            ".mp3" or ".aac" => Starts("ID3") || count >= 2 && header[0] == 255 && (header[1] & 0xe0) == 0xe0,
            ".ogg" or ".opus" => Starts("OggS"),
            ".aif" or ".aiff" => count >= 12 && Starts("FORM") && (header.AsSpan(8, 4).SequenceEqual("AIFF"u8) || header.AsSpan(8, 4).SequenceEqual("AIFC"u8)),
            ".mkv" or ".mka" or ".webm" => header.AsSpan(0, count).StartsWith(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 }),
            ".wma" => header.AsSpan(0, count).StartsWith(new byte[] { 0x30, 0x26, 0xb2, 0x75, 0x8e, 0x66, 0xcf, 0x11 }),
            ".m4a" or ".mp4" or ".mov" => count >= 8 && new[] { "ftyp", "moov", "mdat", "wide", "free", "skip" }.Any(atom => header.AsSpan(4, 4).SequenceEqual(Encoding.ASCII.GetBytes(atom))),
            _ => false
        };
    }

    private static async Task<FileStream> OpenInputAsync(string path, CancellationToken token)
    {
        var opening = Task.Run(() =>
        {
            Recheck(path, token); var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            try
            {
                Recheck(path, token);
                if (input.Length is <= 0 or > MaximumSourceBytes || (File.GetAttributes(input.SafeFileHandle) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                    throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_1992F76E811F"));
                return input;
            }
            catch { input.Dispose(); throw; }
        }, CancellationToken.None);
        FileStream stream;
        try { stream = await opening.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); }
        catch { _ = opening.ContinueWith(task => { if (task.IsCompletedSuccessfully) task.Result.Dispose(); else _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default); throw; }
        try
        {
            var header = new byte[32]; var count = await stream.ReadAsync(header, token).ConfigureAwait(false);
            if (!Signature(header, count, Path.GetExtension(path))) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_3FED491ED045"));
            stream.Position = 0; return stream;
        }
        catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private static void Recheck(string path, CancellationToken token)
    {
        var depth = 0;
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            token.ThrowIfCancellationRequested(); if (++depth > 256) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_77BCA2D5D580")); var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || depth == 1 && (attributes & FileAttributes.Directory) != 0) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_4E48521B798B"));
        }
    }

    private static string Commit(string prepared, string directory, string source, string format, CancellationToken token)
    {
        var stem = Path.GetFileNameWithoutExtension(source); if (stem.Length > 180) { stem = stem[..180]; if (char.IsHighSurrogate(stem[^1])) stem = stem[..^1]; }
        stem += format == "OPUS" ? "_opus" : format == "VORBIS" ? "_vorbis" : "_aac";
        var extension = format == "OPUS" ? ".opus" : format == "VORBIS" ? ".ogg" : ".m4a";
        for (var index = 0; index < 10000; index++)
        {
            token.ThrowIfCancellationRequested(); var target = Path.Combine(directory, stem + (index == 0 ? "" : "_" + index.ToString(CultureInfo.InvariantCulture)) + extension);
            if (target.Equals(source, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Move(prepared, target, false); return target; } catch (IOException) when (File.Exists(target) || Directory.Exists(target)) { }
        }
        throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_99421130842B"));
    }

    private async Task<NativeResult> RunAsync(string executable, IReadOnlyList<string> args, CancellationToken token, Action<string>? line = null)
    {
        token.ThrowIfCancellationRequested(); var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in args) start.ArgumentList.Add(argument); using var process = new Process { StartInfo = start };
        try { if (!process.Start()) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_A8947B1A7B1F")); }
        catch (System.ComponentModel.Win32Exception exception) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_64D94FFD1025"), exception); }
        var output = DrainAsync(process.StandardOutput, 2 * 1024 * 1024, token, line); var error = DrainAsync(process.StandardError, 128 * 1024, token);
        try
        {
            NativeStarted?.Invoke(string.Join(' ', args)); await process.WaitForExitAsync(token).ConfigureAwait(false); await Task.WhenAll(output, error).WaitAsync(token).ConfigureAwait(false);
            var stdout = await output.ConfigureAwait(false); var stderr = await error.ConfigureAwait(false); token.ThrowIfCancellationRequested();
            if (stdout.Length > 2 * 1024 * 1024 || stderr.Length > 128 * 1024) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_B7243DD0F992"));
            if (process.ExitCode != 0) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_8CA04B8BFFB8") + stderr[..Math.Min(stderr.Length, 700)].Trim());
            return new NativeResult(stdout);
        }
        finally
        {
            if (!process.HasExited) { try { process.Kill(true); } catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (Exception exception) when (exception is InvalidOperationException or TimeoutException) { }
            var drains = Task.WhenAll(output, error);
            try { await drains.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (TimeoutException) { process.StandardOutput.Dispose(); process.StandardError.Dispose(); Observe(drains); }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException) { }
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader, int maximum, CancellationToken token, Action<string>? callback = null)
    {
        var output = new StringBuilder(); var line = new StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            if (callback is null) { if (output.Length <= maximum) output.Append(buffer, 0, Math.Min(count, maximum + 1 - output.Length)); continue; }
            for (var index = 0; index < count; index++)
                if (buffer[index] == '\n') { callback(line.ToString().TrimEnd('\r')); line.Clear(); } else if (line.Length < 4096) line.Append(buffer[index]);
        }
        if (callback is not null && line.Length != 0) callback(line.ToString()); return output.ToString();
    }

    private static async Task CleanupAsync(string? temporary, string? directory)
    {
        if (temporary is null || directory is null || !string.Equals(Path.GetDirectoryName(temporary), Path.TrimEndingDirectorySeparator(directory), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(temporary).StartsWith(".orbit-audio-export-", StringComparison.Ordinal)) return;
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
    private static void Observe(Task task) => _ = task.ContinueWith(completed => _ = completed.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private static string Text(JsonElement item, string key) => item.TryGetProperty(key, out var value) ? value.ToString() : "";
    private static double Numeric(JsonElement item, string key) => double.TryParse(Text(item, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : 0;
    private static string Number(double value) => value.ToString("0.###############", CultureInfo.InvariantCulture);
    private sealed record AudioInfo(string Container, string Brand, string Codec, int Rate, int Channels, double Duration, double Start, int Streams);
    private sealed record NativeResult(string Output);
}
