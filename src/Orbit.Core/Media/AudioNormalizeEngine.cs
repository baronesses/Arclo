using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Orbit.Core.Media;

/// <summary>Two-pass loudnorm to new PCM24 WAV, followed by an independent ebur128 true-peak scan.</summary>
public sealed class AudioNormalizeEngine
{
    public const long MaximumSourceBytes = 2L * 1024 * 1024 * 1024;
    public const long MaximumOutputBytes = 2L * 1024 * 1024 * 1024;
    public const double MinimumDurationSeconds = 3;
    public const double MaximumDurationSeconds = 24 * 60 * 60;
    public const int MaximumChannels = 8;
    public const int MaximumSampleRate = 192000;
    public const double IntegratedToleranceLufs = .5;
    public const double TruePeakToleranceDb = .2;
    private static readonly HashSet<string> Containers = new(StringComparer.Ordinal)
        { "mov", "mp4", "m4a", "3gp", "3g2", "mj2", "matroska", "webm", "avi", "asf", "mpeg", "mpegts", "m4v", "mp3", "wav", "flac", "aac", "ogg", "aiff" };
    private readonly MediaEngine _media;
    private readonly TimeSpan _operationTimeout;

    public AudioNormalizeEngine(string? ffmpegExecutable = null, TimeSpan? operationTimeout = null)
    {
        _media = new MediaEngine(ffmpegExecutable); _operationTimeout = operationTimeout ?? TimeSpan.FromHours(2);
        if (_operationTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
    }

    public string ExecutablePath => _media.ExecutablePath;
    public string ProbeExecutablePath => _media.ProbeExecutablePath;

    /// <summary>Measures the first audio track without creating files. Video and unmeasurable/silent/short audio fail clearly.</summary>
    public async Task<AudioLoudnessAnalysis> AnalyzeAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var deadline = Deadline(ct);
        try
        {
            var source = NormalizePath(path); await using var lease = await OpenInputAsync(source, deadline.Token).ConfigureAwait(false);
            var audio = await ProbeAsync(source, deadline.Token).ConfigureAwait(false); ValidateShape(audio, checkOutputBytes: false);
            return await AnalyzeCoreAsync(source, audio, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw TimeoutError(); }
        catch (Exception exception) when (Expected(exception)) { throw Explain(exception); }
    }

    public async Task<MediaResult> NormalizeAsync(string path, AudioNormalizeOptions options, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options); ct.ThrowIfCancellationRequested(); ValidateOptions(options);
        using var deadline = Deadline(ct); var token = deadline.Token; string? temporary = null; string? directory = null;
        try
        {
            var source = NormalizePath(path); await using var lease = await OpenInputAsync(source, token).ConfigureAwait(false);
            var audio = await ProbeAsync(source, token).ConfigureAwait(false); ValidateShape(audio, checkOutputBytes: true);
            double last = 0; progress?.Report(0);
            Action<string> Report(double start, double span, double duration) => line =>
            {
                if (!line.StartsWith("out_time_us=", StringComparison.Ordinal) || !long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us)) return;
                var value = start + span * Math.Clamp(us / 1_000_000d / duration, 0, 1);
                if (value > last) { last = value; progress?.Report(value); }
            };
            var target = TargetFilter(options);
            var measuredRun = await RunAsync(ExecutablePath, NullArguments(source, "asetpts=N/SR/TB," + target + ":print_format=json"), token,
                Report(0, .28, audio.Duration)).ConfigureAwait(false);
            var measured = ParseLoudnorm(measuredRun.Stderr);
            if (measuredRun.Samples <= 0) throw Unmeasurable();
            audio = audio with { Duration = measuredRun.Samples / audio.Rate }; ValidateShape(audio, checkOutputBytes: true);
            if (measured.I is < -99 or > 0 || measured.Peak is < -99 or > 99 || measured.Range is < 0 or > 99
                || measured.Threshold is < -99 or > 0 || Math.Abs(measured.Offset) > 99)
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_D325EA0B7335"));
            directory = Path.TrimEndingDirectorySeparator(string.IsNullOrWhiteSpace(options.OutputDirectory) ? Path.GetDirectoryName(source)! : Path.GetFullPath(options.OutputDirectory));
            if (!await Task.Run(() => Directory.Exists(directory), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false))
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_45C406D632F2"));
            temporary = Path.Combine(directory, ".orbit-audio-normalize-" + Guid.NewGuid().ToString("N"));
            var pendingDirectory = temporary; var creating = Task.Run(() => Directory.CreateDirectory(pendingDirectory), CancellationToken.None);
            try { await creating.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); }
            catch
            {
                temporary = null;
                _ = creating.ContinueWith(async task =>
                {
                    if (task.IsCompletedSuccessfully) await CleanupAsync(pendingDirectory, directory).ConfigureAwait(false); else _ = task.Exception;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap(); throw;
            }
            var prepared = Path.Combine(temporary, "result.wav");
            var filter = $"asetpts=N/SR/TB,{target}:measured_I={Number(measured.I)}:measured_TP={Number(measured.Peak)}:measured_LRA={Number(measured.Range)}:measured_thresh={Number(measured.Threshold)}:offset={Number(measured.Offset)}:linear=true:print_format=json";
            var arguments = Input(source);
            arguments.AddRange(["-af", filter, "-ar", audio.Rate.ToString(CultureInfo.InvariantCulture), "-ac", audio.Channels.ToString(CultureInfo.InvariantCulture),
                "-c:a", "pcm_s24le", "-map_metadata", "-1", "-map_chapters", "-1", "-t", Number(MaximumDurationSeconds + 1),
                "-fs", (MaximumOutputBytes + 1).ToString(CultureInfo.InvariantCulture), "-progress", "pipe:1", "-stats_period", "0.1", "-f", "wav", prepared]);
            progress?.Report(last = Math.Max(last, .30));
            var normalizedRun = await RunAsync(ExecutablePath, arguments, token, Report(.30, .45, audio.Duration)).ConfigureAwait(false);
            var mode = ParseLoudnorm(normalizedRun.Stderr).Mode;
            if (mode is not ("linear" or "dynamic")) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_CA2836D6634E"));
            var bytes = new FileInfo(prepared).Length;
            if (bytes is <= 0 or > MaximumOutputBytes) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_FBB03B640A46"));
            var actual = await ProbeAsync(prepared, token).ConfigureAwait(false);
            if (actual.Codec != "pcm_s24le" || actual.Rate != audio.Rate || actual.Channels != audio.Channels
                || Math.Abs(actual.Duration - audio.Duration) > Math.Max(.01, 4d / audio.Rate) || Math.Abs(actual.Start) > 1d / audio.Rate)
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_496874366172"));
            progress?.Report(last = Math.Max(last, .78));
            var verification = await AnalyzeCoreAsync(prepared, actual, token, Report(.78, .19, actual.Duration)).ConfigureAwait(false);
            var correction = options.TargetLufs - verification.IntegratedLufs;
            if ((Math.Abs(correction) > IntegratedToleranceLufs || verification.TruePeakDb > options.TruePeakDb + TruePeakToleranceDb)
                && correction <= 3 && verification.TruePeakDb + correction <= options.TruePeakDb - .1)
            {
                // loudnorm and the independent R128 scanner can gate/frame borderline material
                // differently, including multichannel material. One measured attenuation is
                // safe at any level; upward correction is limited to 3 LU and verified peak
                // headroom. Never relax final acceptance or add an undisclosed limiter.
                var calibrated = Path.Combine(temporary, "calibrated.wav"); var correctionArgs = Input(prepared);
                correctionArgs.AddRange(["-af", "asetpts=N/SR/TB,volume=" + Number(correction) + "dB:precision=double",
                    "-ar", audio.Rate.ToString(CultureInfo.InvariantCulture), "-ac", audio.Channels.ToString(CultureInfo.InvariantCulture), "-c:a", "pcm_s24le",
                    "-map_metadata", "-1", "-map_chapters", "-1", "-fs", (MaximumOutputBytes + 1).ToString(CultureInfo.InvariantCulture),
                    "-progress", "pipe:1", "-stats_period", "0.1", "-f", "wav", calibrated]);
                progress?.Report(last = Math.Max(last, .97));
                await RunAsync(ExecutablePath, correctionArgs, token, Report(.97, .01, audio.Duration)).ConfigureAwait(false);
                var calibratedInfo = await ProbeAsync(calibrated, token).ConfigureAwait(false); bytes = new FileInfo(calibrated).Length;
                if (bytes is <= 0 or > MaximumOutputBytes || calibratedInfo.Codec != "pcm_s24le" || calibratedInfo.Rate != audio.Rate || calibratedInfo.Channels != audio.Channels
                    || Math.Abs(calibratedInfo.Duration - audio.Duration) > Math.Max(.01, 4d / audio.Rate) || Math.Abs(calibratedInfo.Start) > 1d / audio.Rate)
                    throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_E50DB8DA5702"));
                prepared = calibrated;
                verification = await AnalyzeCoreAsync(prepared, calibratedInfo, token, Report(.98, .01, calibratedInfo.Duration)).ConfigureAwait(false);
            }
            // Independent R128 implementations round their reports differently, and returning from
            // loudnorm's 192kHz true-peak processing to the source rate can move reconstructed peaks.
            // Never broaden these tolerances on a mismatch: reject the prepared file instead.
            if (Math.Abs(verification.IntegratedLufs - options.TargetLufs) > IntegratedToleranceLufs + 1e-9
                || verification.TruePeakDb > options.TruePeakDb + TruePeakToleranceDb + 1e-9)
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Format("Text_35678E1D2DE2",(object?)(Number(verification.IntegratedLufs)),(object?)(Number(verification.TruePeakDb)),(object?)(Number(options.TargetLufs)),(object?)(Number(options.TruePeakDb))));
            token.ThrowIfCancellationRequested(); var published = Commit(prepared, directory, source, mode, token);
            progress?.Report(1); return new MediaResult(published, bytes, 0, 0, "WAV", lease.Length);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw TimeoutError(); }
        catch (Exception exception) when (Expected(exception)) { throw Explain(exception); }
        finally { await CleanupAsync(temporary, directory).ConfigureAwait(false); }
    }

    private async Task<AudioLoudnessAnalysis> AnalyzeCoreAsync(string path, AudioInfo audio, CancellationToken token, Action<string>? progress = null)
    {
        var run = await RunAsync(ExecutablePath, NullArguments(path, "asetpts=N/SR/TB,ebur128=peak=true:framelog=quiet"), token, progress, progressOutput: true).ConfigureAwait(false);
        var start = run.Stderr.LastIndexOf("Summary:", StringComparison.Ordinal);
        var summary = start >= 0 ? run.Stderr[start..] : "";
        double Match(string pattern)
        {
            var match = Regex.Match(summary, pattern, RegexOptions.CultureInvariant);
            if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value)) throw Unmeasurable();
            return value;
        }
        var integrated = Match(@"Integrated loudness:\s*I:\s*(\S+)\s+LUFS");
        var range = Match(@"Loudness range:\s*LRA:\s*(\S+)\s+LU");
        var peak = Match(@"True peak:\s*Peak:\s*(\S+)\s+dBFS");
        if (run.Samples <= 0) throw Unmeasurable();
        var duration = run.Samples / audio.Rate;
        ValidateShape(audio with { Duration = duration }, checkOutputBytes: false);
        if (integrated <= -70 || range < 0) throw Unmeasurable();
        return new AudioLoudnessAnalysis(integrated, peak, range, audio.Rate, audio.Channels, duration);
    }

    private static Measurement ParseLoudnorm(string stderr)
    {
        var start = stderr.LastIndexOf('{'); var end = stderr.LastIndexOf('}');
        if (start < 0 || end < start) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_77ED4880EC65"));
        try
        {
            using var json = JsonDocument.Parse(stderr[start..(end + 1)]); var root = json.RootElement;
            double Read(string name)
            {
                if (!root.TryGetProperty(name, out var item) || !double.TryParse(item.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value)) throw Unmeasurable();
                return value;
            }
            return new Measurement(Read("input_i"), Read("input_tp"), Read("input_lra"), Read("input_thresh"), Read("target_offset"),
                root.TryGetProperty("normalization_type", out var mode) ? mode.GetString() ?? "" : "");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_392968490B09"), exception); }
    }

    private async Task<AudioInfo> ProbeAsync(string path, CancellationToken token)
    {
        var run = await RunAsync(ProbeExecutablePath, ["-v", "error", "-protocol_whitelist", "file,pipe", "-show_entries",
            "format=format_name,duration:stream=codec_type,codec_name,sample_rate,channels,duration,start_time:stream_disposition=attached_pic:stream_tags=DURATION,duration", "-of", "json", "-i", path], token).ConfigureAwait(false);
        try
        {
            using var json = JsonDocument.Parse(run.Stdout); var root = json.RootElement;
            var format = root.GetProperty("format");
            if (!Text(format, "format_name").Split(',').Any(Containers.Contains)) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_9034645365E9"));
            JsonElement? first = null;
            foreach (var stream in root.GetProperty("streams").EnumerateArray())
            {
                var kind = Text(stream, "codec_type");
                if (kind == "video" && (!stream.TryGetProperty("disposition", out var disposition) || Numeric(disposition, "attached_pic") == 0))
                    throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_0339545E6DE9"));
                if (kind == "audio" && first is null) first = stream;
            }
            if (first is not { } audio) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_92D41E3CF75E"));
            var duration = Numeric(audio, "duration");
            if (duration <= 0 && audio.TryGetProperty("tags", out var tags))
            {
                var text = Text(tags, "DURATION"); if (text.Length == 0) text = Text(tags, "duration");
                var parts = text.Split(':');
                if (parts.Length == 3 && double.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
                    && double.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
                    && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                    && minutes is >= 0 and < 60 && seconds is >= 0 and < 60)
                    duration = hours * 3600 + minutes * 60 + seconds - Numeric(audio, "start_time");
            }
            if (duration <= 0) duration = Numeric(format, "duration");
            return new AudioInfo((int)Numeric(audio, "sample_rate"), (int)Numeric(audio, "channels"), duration, Numeric(audio, "start_time"), Text(audio, "codec_name"));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_5BA48CEB8ED2"), exception); }
    }

    private static void ValidateOptions(AudioNormalizeOptions options)
    {
        if (!double.IsFinite(options.TargetLufs) || options.TargetLufs is < -24 or > -9) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_DDF3F20ECAF1"));
        if (!double.IsFinite(options.TruePeakDb) || options.TruePeakDb is < -3 or > -.1) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_3FBE11CCE316"));
        if (!double.IsFinite(options.LoudnessRange) || options.LoudnessRange is < 1 or > 20) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_67C4E477412B"));
    }

    private static void ValidateShape(AudioInfo audio, bool checkOutputBytes)
    {
        if (!double.IsFinite(audio.Duration) || audio.Duration is < MinimumDurationSeconds or > MaximumDurationSeconds)
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_9A81F237BB40"));
        if (audio.Rate is < 1 or > MaximumSampleRate || audio.Channels is < 1 or > MaximumChannels || audio.Codec is "" or "unknown")
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_A58625C53410"));
        if (checkOutputBytes && audio.Duration * audio.Rate * audio.Channels * 3 + 4096 > MaximumOutputBytes)
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_C1B331C18C28"));
    }

    private static List<string> Input(string path) => ["-hide_banner", "-v", "info", "-nostdin", "-n", "-nostats", "-threads", "2", "-filter_threads", "2",
        "-protocol_whitelist", "file,pipe", "-i", path, "-map", "0:a:0", "-vn", "-sn", "-dn"];
    private static List<string> NullArguments(string path, string filters)
    {
        // Progress timestamps describe the last frame's PTS (ebur128 can report 7.9s for an
        // actual8s stream). Count decoded samples before any loudnorm upsampling instead.
        const string samples = "astats=metadata=1:reset=0:measure_perchannel=none:measure_overall=Number_of_samples,ametadata=print:key=lavfi.astats.Overall.Number_of_samples:file=-:direct=1";
        var args = Input(path); args.AddRange(["-af", samples + "," + filters, "-t", Number(MaximumDurationSeconds + 1), "-c:a", "pcm_f64le", "-progress", "pipe:1", "-stats_period", "0.1", "-f", "null", "-"]); return args;
    }
    private static string TargetFilter(AudioNormalizeOptions options) => $"loudnorm=I={Number(options.TargetLufs)}:TP={Number(options.TruePeakDb)}:LRA={Number(options.LoudnessRange)}";
    private CancellationTokenSource Deadline(CancellationToken ct) { var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(_operationTimeout); return deadline; }
    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path); var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal)
            || full.IndexOf(':', full.Length > 1 && full[1] == ':' ? 2 : 0) >= 0) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_F23E07BA5469"));
        return full;
    }

    private static async Task<FileStream> OpenInputAsync(string path, CancellationToken token)
    {
        var opening = Task.Run(() =>
        {
            Recheck(path, token); var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            try
            {
                Recheck(path, token);
                if (input.Length is <= 0 or > MaximumSourceBytes || (File.GetAttributes(input.SafeFileHandle) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_102904AD6E7D"));
                return input;
            }
            catch { input.Dispose(); throw; }
        }, CancellationToken.None);
        try { return await opening.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); }
        catch
        {
            _ = opening.ContinueWith(task => { if (task.IsCompletedSuccessfully) task.Result.Dispose(); else _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default); throw;
        }
    }

    private static void Recheck(string path, CancellationToken token)
    {
        var depth = 0;
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            token.ThrowIfCancellationRequested(); if (++depth > 256) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_7715C07C18BD"));
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (depth == 1 && (attributes & FileAttributes.Directory) != 0))
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_8E1302F7E2F2"));
        }
    }

    private static string Commit(string prepared, string directory, string source, string mode, CancellationToken token)
    {
        var stem = Path.GetFileNameWithoutExtension(source); if (stem.Length > 180) { stem = stem[..180]; if (char.IsHighSurrogate(stem[^1])) stem = stem[..^1]; }
        stem += "_normalized_" + mode + "_pcm24";
        for (var index = 0; index < 10000; index++)
        {
            token.ThrowIfCancellationRequested(); var target = Path.Combine(directory, stem + (index == 0 ? "" : "_" + index.ToString(CultureInfo.InvariantCulture)) + ".wav");
            if (target.Equals(source, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Move(prepared, target, false); return target; }
            catch (IOException) when (File.Exists(target) || Directory.Exists(target)) { }
        }
        throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_FDEBC25FE5AA"));
    }

    private static async Task<RunResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token, Action<string>? progress = null, bool progressOutput = false)
    {
        token.ThrowIfCancellationRequested(); var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        try { if (!process.Start()) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_8D6EC8A55E2F")); }
        catch (System.ComponentModel.Win32Exception exception) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_FD423628FAF8"), exception); }
        double duration = 0, samples = 0;
        void Line(string line)
        {
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us)) duration = Math.Max(duration, us / 1_000_000d);
            const string samplePrefix = "lavfi.astats.Overall.Number_of_samples=";
            if (line.StartsWith(samplePrefix, StringComparison.Ordinal) && double.TryParse(line.AsSpan(samplePrefix.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out var count) && double.IsFinite(count)) samples = Math.Max(samples, count);
            progress?.Invoke(line);
        }
        var stdout = DrainAsync(process.StandardOutput, progress is not null || progressOutput ? Line : null);
        var stderr = DrainAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false); await Task.WhenAll(stdout, stderr).WaitAsync(token).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false); var error = await stderr.ConfigureAwait(false); token.ThrowIfCancellationRequested();
            if (output.Length > 2 * 1024 * 1024 || error.Length > 2 * 1024 * 1024) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_FE66200BCE51"));
            if (process.ExitCode != 0) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_3240B9871EB2") + error[..Math.Min(error.Length, 900)].Trim());
            return new RunResult(output, error, duration, samples);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
                var exit = process.WaitForExitAsync(); try { await exit.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (TimeoutException) { Observe(exit); }
            }
            var drains = Task.WhenAll(stdout, stderr);
            try { await drains.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (TimeoutException) { process.StandardOutput.Dispose(); process.StandardError.Dispose(); Observe(drains); }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader, Action<string>? lineCallback = null)
    {
        var text = new StringBuilder(); var line = new StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            if (lineCallback is null) { if (text.Length <= 2 * 1024 * 1024) text.Append(buffer, 0, Math.Min(count, 2 * 1024 * 1024 + 1 - text.Length)); continue; }
            for (var index = 0; index < count; index++)
                if (buffer[index] == '\n') { lineCallback(line.ToString().TrimEnd('\r')); line.Clear(); }
                else if (line.Length < 4096) line.Append(buffer[index]);
        }
        return text.ToString();
    }

    private static async Task CleanupAsync(string? temporary, string? directory)
    {
        if (temporary is null || directory is null
            || !string.Equals(Path.GetDirectoryName(temporary), Path.TrimEndingDirectorySeparator(directory), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(temporary).StartsWith(".orbit-audio-normalize-", StringComparison.Ordinal)) return;
        var deleting = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 11; attempt++)
            {
                try { if (Directory.Exists(temporary) && (File.GetAttributes(temporary) & FileAttributes.ReparsePoint) == 0) Directory.Delete(temporary, true); return; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                if (attempt < 10) await Task.Delay(Math.Min(300, 25 << Math.Min(attempt, 4))).ConfigureAwait(false);
            }
        });
        try { await deleting.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (TimeoutException) { Observe(deleting); }
    }
    private static void Observe(Task task) => _ = task.ContinueWith(completed => _ = completed.Exception, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private static string Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) ? value.ToString() : "";
    private static double Numeric(JsonElement item, string name) => double.TryParse(Text(item, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : 0;
    private static string Number(double value) => value.ToString("0.###############", CultureInfo.InvariantCulture);
    private static bool Expected(Exception exception) => exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or TimeoutException or System.Security.SecurityException;
    private static MediaProcessingException Explain(Exception exception) => new(exception is TimeoutException
        ? global::Orbit.Core.Localization.L.Get("Text_AD6BEF037D97")
        : global::Orbit.Core.Localization.L.Get("Text_243B80D4E5B8"), exception);
    private static MediaProcessingException TimeoutError() => new(global::Orbit.Core.Localization.L.Get("Text_22AFF5CE29C6"));
    private static MediaProcessingException Unmeasurable() => new(global::Orbit.Core.Localization.L.Get("Text_C02320BA7B78"));
    private sealed record AudioInfo(int Rate, int Channels, double Duration, double Start, string Codec);
    private sealed record Measurement(double I, double Peak, double Range, double Threshold, double Offset, string Mode);
    private sealed record RunResult(string Stdout, string Stderr, double Duration, double Samples);
}
