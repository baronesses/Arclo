using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Orbit.Core.Media;

/// <summary>First audio track to a new PCM24 WAV, with gain and linear fades.
/// Boosts use a sample-peak limiter at 0.98; this is not loudness normalization or restoration.</summary>
public sealed class AudioAdjustEngine
{
    public const long MaximumSourceBytes = 2L * 1024 * 1024 * 1024;
    public const long MaximumOutputBytes = 2L * 1024 * 1024 * 1024;
    public const double MaximumDurationSeconds = 24 * 60 * 60;
    public const int MaximumChannels = 8;
    public const int MaximumSampleRate = 192000;
    private readonly MediaEngine _media;
    private readonly TimeSpan _operationTimeout;

    public AudioAdjustEngine(string? ffmpegExecutable = null, TimeSpan? operationTimeout = null)
    {
        _media = new MediaEngine(ffmpegExecutable);
        _operationTimeout = operationTimeout ?? TimeSpan.FromHours(2);
        if (_operationTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
    }

    public string ExecutablePath => _media.ExecutablePath;
    public string ProbeExecutablePath => _media.ProbeExecutablePath;

    /// <summary>Returns first-track sample duration for editing fades, without creating any output.
    /// Compressed sources are decoded to a null sink; PCM uses its inspected duration.</summary>
    public async Task<double> GetDecodedDurationAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_operationTimeout);
        var token = deadline.Token;
        try
        {
            var sourcePath = Path.GetFullPath(path);
            await using var input = await OpenInputAsync(sourcePath, token).ConfigureAwait(false);
            var source = await _media.InspectAsync(sourcePath, token).WaitAsync(token).ConfigureAwait(false);
            if (source.Kind != "Audio" || !source.HasAudio)
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_C0A69336492F"));
            var audio = await ProbeAudioAsync(sourcePath, source.DurationSeconds, token).ConfigureAwait(false);
            var defaults = new AudioAdjustOptions();
            ValidateShape(audio, defaults);
            if (!audio.Codec.StartsWith("pcm_", StringComparison.Ordinal))
            {
                audio = audio with { Duration = await MeasureDecodedDurationAsync(sourcePath, audio.Rate, token).ConfigureAwait(false) };
                ValidateShape(audio, defaults);
            }
            token.ThrowIfCancellationRequested();
            return audio.Duration;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_5247031C8373")); }
        catch (UnauthorizedAccessException exception)
        { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_209568AD6CD3"), exception); }
        catch (IOException exception)
        { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_3CFFF89E5F33"), exception); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or System.Security.SecurityException)
        { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_A970E16BD3A7"), exception); }
    }

    public async Task<MediaResult> AdjustAsync(string path, AudioAdjustOptions options, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateValues(options);
        string? temporary = null, directory = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_operationTimeout);
        var token = deadline.Token;
        try
        {
            var sourcePath = Path.GetFullPath(path);
            progress?.Report(0);
            await using var input = await OpenInputAsync(sourcePath, token).ConfigureAwait(false);
            var source = await _media.InspectAsync(sourcePath, token).WaitAsync(token).ConfigureAwait(false);
            if (source.Kind != "Audio" || !source.HasAudio)
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_C0A69336492F"));
            var audio = await ProbeAudioAsync(sourcePath, source.DurationSeconds, token).ConfigureAwait(false);
            ValidateShape(audio, options);
            // Gapless decoders remove encoder padding that container duration can include.
            // Fades and lossy-source duration validation need actual decoded sample counts.
            var measuredDuration = options.FadeInSeconds > 0 || options.FadeOutSeconds > 0 || !audio.Codec.StartsWith("pcm_", StringComparison.Ordinal);
            if (measuredDuration)
            {
                audio = audio with { Duration = await MeasureDecodedDurationAsync(sourcePath, audio.Rate, token).ConfigureAwait(false) };
                ValidateShape(audio, options);
                progress?.Report(.1);
            }
            directory = string.IsNullOrWhiteSpace(options.OutputDirectory) ? Path.GetDirectoryName(sourcePath)!
                : Path.GetFullPath(options.OutputDirectory);
            if (!await Task.Run(() => Directory.Exists(directory), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false))
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_45C406D632F2"));
            temporary = Path.Combine(directory, ".orbit-audio-adjust-" + Guid.NewGuid().ToString("N"));
            var pendingDirectory = temporary;
            var creating = Task.Run(() => Directory.CreateDirectory(pendingDirectory), CancellationToken.None);
            try { await creating.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); }
            catch
            {
                // A late network-directory creation may only clean up its own generated path.
                temporary = null;
                _ = creating.ContinueWith(async task =>
                {
                    if (task.IsCompletedSuccessfully) await CleanupAsync(pendingDirectory, directory).ConfigureAwait(false);
                    else _ = task.Exception;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
                throw;
            }
            var prepared = Path.Combine(temporary, "result.wav");
            var filters = new List<string> { "asetpts=N/SR/TB" };
            if (options.VolumePercent != 100) filters.Add("volume=" + Number(options.VolumePercent / 100) + ":precision=double");
            if (options.VolumePercent > 100) filters.Add("alimiter=limit=0.98:level=false:latency=true");
            if (options.FadeInSeconds > 0) filters.Add("afade=t=in:st=0:d=" + Number(options.FadeInSeconds) + ":curve=tri");
            if (options.FadeOutSeconds > 0) filters.Add("afade=t=out:st=" + Number(audio.Duration - options.FadeOutSeconds) + ":d=" + Number(options.FadeOutSeconds) + ":curve=tri");
            var arguments = new List<string> { "-hide_banner", "-v", "error", "-nostdin", "-n", "-nostats", "-threads", "2",
                "-protocol_whitelist", "file,pipe", "-i", sourcePath, "-map", "0:a:0", "-vn", "-sn", "-dn", "-af", string.Join(',', filters),
                "-c:a", "pcm_s24le", "-ar", audio.Rate.ToString(CultureInfo.InvariantCulture), "-ac", audio.Channels.ToString(CultureInfo.InvariantCulture),
                "-map_metadata", "-1", "-map_chapters", "-1", "-fs", MaximumOutputBytes.ToString(CultureInfo.InvariantCulture),
                "-progress", "pipe:1", "-stats_period", "0.25", "-f", "wav", prepared };
            double lastProgress = measuredDuration ? .1 : 0;
            void OnProgress(string line)
            {
                const string prefix = "out_time_us=";
                if (!line.StartsWith(prefix, StringComparison.Ordinal) || !long.TryParse(line.AsSpan(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros)) return;
                var current = Math.Clamp(micros / 1_000_000d / audio.Duration, 0, 1) * (measuredDuration ? .88 : .98) + (measuredDuration ? .1 : 0);
                if (current > lastProgress) { lastProgress = current;progress?.Report(current); }
            }
            await RunAsync(ExecutablePath, arguments, token, OnProgress).ConfigureAwait(false);
            progress?.Report(.99);
            var bytes = new FileInfo(prepared).Length;
            if (bytes is <= 0 or > MaximumOutputBytes) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_D3C344F6CE09"));
            var output = await _media.InspectAsync(prepared, token).WaitAsync(token).ConfigureAwait(false);
            var actual = await ProbeAudioAsync(prepared, output.DurationSeconds, token).ConfigureAwait(false);
            if (output.Kind != "Audio" || output.Format != "WAV" || !output.HasAudio || actual.Codec != "pcm_s24le"
                || actual.Rate != audio.Rate || actual.Channels != audio.Channels || Math.Abs(actual.Duration - audio.Duration) > Math.Max(.05, 2d / audio.Rate)
                || Math.Abs(actual.Start) > 1d / audio.Rate)
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_0FC4013DF80E"));
            token.ThrowIfCancellationRequested();
            var published = Commit(prepared, directory, sourcePath, token);
            progress?.Report(1);
            return new MediaResult(published, bytes, 0, 0, "WAV", input.Length);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_19C3635955F9")); }
        catch (TimeoutException exception)
        { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_E1C332832A92"), exception); }
        catch (UnauthorizedAccessException exception)
        { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_CC2272A554F3"), exception); }
        catch (IOException exception)
        { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_B947488C5C8D"), exception); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or System.Security.SecurityException)
        { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_1BA6515EC8C3"), exception); }
        finally { await CleanupAsync(temporary, directory).ConfigureAwait(false); }
    }

    private static void ValidateValues(AudioAdjustOptions options)
    {
        if (!double.IsFinite(options.VolumePercent) || options.VolumePercent is < 0 or > 200)
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_273B94A95069"));
        if (!double.IsFinite(options.FadeInSeconds) || !double.IsFinite(options.FadeOutSeconds) || options.FadeInSeconds < 0 || options.FadeOutSeconds < 0)
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_9A8EC307BCD3"));
    }

    private static void ValidateShape(AudioInfo audio, AudioAdjustOptions options)
    {
        if (!double.IsFinite(audio.Duration) || audio.Duration is <= 0 or > MaximumDurationSeconds)
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_00356AE81682"));
        if (audio.Channels is < 1 or > MaximumChannels || audio.Rate is < 1 or > MaximumSampleRate)
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_E4722DBDDAC3"));
        if (options.FadeInSeconds + options.FadeOutSeconds > audio.Duration)
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_55DDC3B48225"));
        if (audio.Duration * audio.Rate * audio.Channels * 3 + 4096 > MaximumOutputBytes)
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_C1B331C18C28"));
    }

    private async Task<AudioInfo> ProbeAudioAsync(string path, double fallbackDuration, CancellationToken token)
    {
        var response = await RunAsync(ProbeExecutablePath, ["-v", "error", "-protocol_whitelist", "file,pipe", "-select_streams", "a:0",
            "-show_entries", "stream=codec_name,sample_rate,channels,start_time,duration:format=start_time,duration", "-of", "json", "-i", path], token).ConfigureAwait(false);
        try
        {
            using var json = JsonDocument.Parse(response);
            var streams = json.RootElement.GetProperty("streams");
            if (streams.GetArrayLength() != 1) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_768303AFA7CB"));
            var stream = streams[0];
            var duration = ReadNumber(stream, "duration");
            if (duration <= 0) duration = fallbackDuration;
            return new AudioInfo((int)ReadNumber(stream, "sample_rate"), (int)ReadNumber(stream, "channels"), duration,
                ReadNumber(stream, "start_time"), stream.TryGetProperty("codec_name", out var codec) ? codec.GetString() ?? "" : "");
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_E0FECC18793C"), exception); }
    }

    private async Task<double> MeasureDecodedDurationAsync(string path, int sampleRate, CancellationToken token)
    {
        double finalSamples = 0;
        await RunAsync(ExecutablePath, ["-hide_banner", "-v", "error", "-nostdin", "-nostats", "-threads", "2",
            "-protocol_whitelist", "file,pipe", "-i", path, "-map", "0:a:0", "-vn", "-sn", "-dn", "-af",
            "asetpts=N/SR/TB,astats=metadata=1:reset=0:measure_perchannel=none:measure_overall=Number_of_samples,ametadata=print:key=lavfi.astats.Overall.Number_of_samples:file=-:direct=1",
            "-t", Number(MaximumDurationSeconds + 1), "-c:a", "pcm_s24le", "-f", "null", "-"], token, line =>
            {
                const string prefix = "lavfi.astats.Overall.Number_of_samples=";
                if (line.StartsWith(prefix, StringComparison.Ordinal) && double.TryParse(line.AsSpan(prefix.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value))
                    finalSamples = Math.Max(finalSamples, value);
            }).ConfigureAwait(false);
        if (finalSamples <= 0) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_125B89A13905"));
        return finalSamples / sampleRate;
    }

    private static double ReadNumber(JsonElement item, string name) => item.TryGetProperty(name, out var value)
        && double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : 0;
    private static string Number(double value) => value.ToString("0.################", CultureInfo.InvariantCulture);

    private static async Task<FileStream> OpenInputAsync(string path, CancellationToken token)
    {
        var pending = Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            try
            {
                token.ThrowIfCancellationRequested();
                if (stream.Length is <= 0 or > MaximumSourceBytes) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_A6AA3F62E8F9"));
                return stream;
            }
            catch { stream.Dispose();throw; }
        }, CancellationToken.None);
        try { return await pending.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            _ = pending.ContinueWith(completed => { if (completed.IsCompletedSuccessfully) completed.Result.Dispose();else _ = completed.Exception; },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            if (exception is OperationCanceledException) throw;
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_DC6E15DF0947"), exception);
        }
    }

    private static async Task<string> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token, Action<string>? onProgress = null)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { if (!process.Start()) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_C1E88CD5FDA0")); }
        catch (System.ComponentModel.Win32Exception exception) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_870C005AD6DF"), exception); }
        var output = DrainAsync(process.StandardOutput, onProgress);var error = DrainAsync(process.StandardError, null);
        try { await process.WaitForExitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (TimeoutException) { }
            var readers = Task.WhenAll(output, error);
            try { await readers.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                process.StandardOutput.Dispose();process.StandardError.Dispose();
                _ = readers.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
            throw;
        }
        var stdout = await output.ConfigureAwait(false);var stderr = await error.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_3240B9871EB2") + stderr[..Math.Min(stderr.Length, 800)].Trim());
        if (stdout.Length > 65536) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_E3A4AD84D60E"));
        return stdout;
    }

    private static async Task<string> DrainAsync(StreamReader reader, Action<string>? onProgress)
    {
        var text = new StringBuilder();var buffer = new char[4096];var line = new StringBuilder();int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            if (onProgress is null)
            { if (text.Length <= 65536) text.Append(buffer, 0, Math.Min(count, 65537 - text.Length));continue; }
            for (int index = 0; index < count; index++)
                if (buffer[index] == '\n') { onProgress(line.ToString().TrimEnd('\r'));line.Clear(); }
                else if (line.Length < 4096) line.Append(buffer[index]);
        }
        return text.ToString();
    }

    private static string Commit(string prepared, string directory, string sourcePath, CancellationToken token)
    {
        var stem = Path.GetFileNameWithoutExtension(sourcePath) + "_adjusted_pcm24";
        for (var index = 0; index < 10000; index++)
        {
            token.ThrowIfCancellationRequested();
            var candidate = Path.Combine(directory, stem + (index == 0 ? "" : "_" + index.ToString(CultureInfo.InvariantCulture)) + ".wav");
            if (string.Equals(candidate, sourcePath, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Move(prepared, candidate, false);return candidate; }
            catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate)) { }
        }
        throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_986759DDACE5"));
    }

    private static async Task CleanupAsync(string? temporary, string? directory)
    {
        if (temporary is null || directory is null || !temporary.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(temporary).StartsWith(".orbit-audio-adjust-", StringComparison.Ordinal)) return;
        var deleting = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 11; attempt++)
            {
                try { if (Directory.Exists(temporary)) Directory.Delete(temporary, true);return; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                if (attempt < 10) await Task.Delay(Math.Min(300, 25 << Math.Min(attempt, 4))).ConfigureAwait(false);
            }
        }, CancellationToken.None);
        try { await deleting.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (TimeoutException)
        { _ = deleting.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default); }
    }

    private sealed record AudioInfo(int Rate, int Channels, double Duration, double Start, string Codec);
}
