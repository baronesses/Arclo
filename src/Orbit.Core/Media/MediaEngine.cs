using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Orbit.Core;

/// <summary>Local FFmpeg operations. Source files are read only; outputs are validated before an atomic, non-overwriting move.</summary>
public sealed class MediaEngine
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v", ".mpeg", ".mpg", ".ts", ".mts", ".m2ts", ".wmv", ".3gp", ".3g2",
        ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".aif", ".aiff"
    };
    private static readonly HashSet<string> Containers = new(StringComparer.Ordinal)
    {
        "mov", "mp4", "m4a", "3gp", "3g2", "mj2", "matroska", "webm", "avi", "asf", "mpeg", "mpegts", "m4v",
        "mp3", "wav", "flac", "aac", "ogg", "aiff"
    };
    private readonly string? _configuredExecutable;
    private readonly TimeSpan _operationTimeout;

    public MediaEngine(string? executablePath = null, TimeSpan? operationTimeout = null)
    {
        _configuredExecutable = executablePath;
        _operationTimeout = operationTimeout ?? TimeSpan.FromHours(2);
        if (_operationTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
    }

    public string ExecutablePath => ResolveFfmpeg();
    public string ProbeExecutablePath => ResolveFfprobe();

    public async Task<MediaMetadata> InspectAsync(string path, CancellationToken cancellationToken = default)
        => (await ProbeAsync(ValidatePath(path), false, cancellationToken).ConfigureAwait(false)).Metadata;

    /// <summary>A PNG thumbnail of the first decoded video frame. Audio files, including cover-art-only audio, are rejected.</summary>
    public async Task<byte[]> CreatePreviewAsync(string path, int maxSize = 640, CancellationToken cancellationToken = default)
    {
        if (maxSize is < 1 or > 2048) throw new ArgumentOutOfRangeException(nameof(maxSize));
        var source = await ProbeAsync(ValidatePath(path), false, cancellationToken).ConfigureAwait(false);
        if (source.Metadata.Kind != "Video") throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_E3FC3D0028F5"));
        var arguments = InputArguments(source.Metadata.Path);
        arguments.AddRange(["-map", "0:" + Invariant(source.VideoIndex!.Value), "-an", "-sn", "-dn", "-frames:v", "1",
            "-vf", FormattableString.Invariant($"scale=iw*sar:ih,setsar=1,scale=w='min({maxSize},iw)':h='min({maxSize},ih)':force_original_aspect_ratio=decrease"),
            "-c:v", "png", "-threads:v", "1", "-f", "image2pipe", "pipe:1"]);
        var result = await RunAsync(ResolveFfmpeg(), arguments, TimeSpan.FromSeconds(45), cancellationToken,
            maxOutputBytes: 32 * 1024 * 1024).ConfigureAwait(false);
        if (result.Length < 8 || !result.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_8297890E73A8"));
        return result;
    }

    public async Task<MediaResult> ExecuteAsync(string path, MediaOperation operation, MediaOptions options,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(operation)) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_FD13E3E7751A"));
        if (options.VideoCrf is < 0 or > 51) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_3125D1338EE8"));
        if (options.AudioBitrateKbps is < 32 or > 320) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_CEDE99FA9813"));
        Report(progress, 0);
        var source = await ProbeAsync(ValidatePath(path), false, cancellationToken).ConfigureAwait(false);
        ValidateOperation(source.Metadata, operation);
        var trimDuration = ValidateTrimRange(source.Metadata, operation, options);
        var extension = OutputExtension(operation);
        string directory;
        try
        {
            directory = string.IsNullOrWhiteSpace(options.OutputDirectory)
                ? System.IO.Path.GetDirectoryName(source.Metadata.Path)!
                : System.IO.Path.GetFullPath(options.OutputDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_B6B9551CC936"), exception); }
        if (!Directory.Exists(directory)) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_45C406D632F2"));
        var temporaryDirectory = System.IO.Path.Combine(directory, ".orbit-media-" + Guid.NewGuid().ToString("N"));
        var temporaryPath = System.IO.Path.Combine(temporaryDirectory, "result." + extension);
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            var arguments = InputArguments(source.Metadata.Path);
            AddOperationArguments(arguments, source, operation, options);
            arguments.AddRange(["-map_metadata", "-1", "-map_chapters", "-1", "-progress", "pipe:1", "-stats_period", "0.25", temporaryPath]);
            var progressDuration = trimDuration ?? (operation == MediaOperation.ExtractMp3 && source.AudioDurationSeconds > 0 ? source.AudioDurationSeconds : source.Metadata.DurationSeconds);
            var sawMicroseconds = false;
            double lastProgress = 0;
            void OnProgressLine(string line)
            {
                var divider = line.IndexOf('=');
                if (divider <= 0 || progressDuration <= 0) return;
                var key = line[..divider];
                if (key == "out_time_us") sawMicroseconds = true;
                if (key != "out_time_us" && (key != "out_time_ms" || sawMicroseconds)) return;
                if (!long.TryParse(line.AsSpan(divider + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds)) return;
                // FFmpeg's out_time_ms is historically named but also measured in microseconds.
                var value = Math.Clamp(microseconds / 1_000_000d / progressDuration, 0, 0.98);
                if (value > lastProgress) { lastProgress = value; Report(progress, value); }
            }
            await RunAsync(ResolveFfmpeg(), arguments, _operationTimeout, cancellationToken, OnProgressLine).ConfigureAwait(false);
            Report(progress, 0.99);
            if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length == 0)
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_276BC49367E4"));
            var output = await ProbeAsync(temporaryPath, operation == MediaOperation.VideoFrame, cancellationToken).ConfigureAwait(false);
            if (trimDuration is { } expectedTrimDuration) ValidateTrimOutput(source, output, operation, options, expectedTrimDuration);
            else ValidateOutput(source, output.Metadata, operation);
            cancellationToken.ThrowIfCancellationRequested();
            var finalPath = MoveToUniquePath(temporaryPath, directory, source.Metadata.Path, operation, extension);
            Report(progress, 1);
            return new MediaResult(finalPath, output.Metadata.Bytes, output.Metadata.Width, output.Metadata.Height,
                extension.ToUpperInvariant(), source.Metadata.Bytes);
        }
        catch (UnauthorizedAccessException exception)
        { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_78FA12B4FBB7"), exception); }
        catch (IOException exception)
        { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_735204634B4B"), exception); }
        finally
        {
            try { if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void ValidateOperation(MediaMetadata source, MediaOperation operation)
    {
        var requiresVideo = operation is MediaOperation.VideoMp4 or MediaOperation.VideoWebm or MediaOperation.VideoFrame or MediaOperation.VideoCompress or MediaOperation.ExtractMp3 or MediaOperation.VideoTrim;
        if (requiresVideo && source.Kind != "Video") throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_8E0D9FD46BC4"));
        if (!requiresVideo && source.Kind != "Audio") throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_00543D915D81"));
        if (operation is MediaOperation.ExtractMp3 or MediaOperation.AudioMp3 or MediaOperation.AudioWav or MediaOperation.AudioFlac or MediaOperation.AudioCompress or MediaOperation.AudioTrim && !source.HasAudio)
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_AEA9D553A84E"));
    }

    private static double? ValidateTrimRange(MediaMetadata source, MediaOperation operation, MediaOptions options)
    {
        if (operation is not (MediaOperation.VideoTrim or MediaOperation.AudioTrim)) return null;
        if (!double.IsFinite(source.DurationSeconds) || source.DurationSeconds <= 0)
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_A48721F1F5CF"));
        if (!double.IsFinite(options.TrimStartSeconds) || options.TrimEndSeconds is not { } end || !double.IsFinite(end)
            || options.TrimStartSeconds < 0 || end <= options.TrimStartSeconds)
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_485A25DA39F1"));
        if (end > source.DurationSeconds)
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_6C8A4CDB644B"));
        return end - options.TrimStartSeconds;
    }

    private static void ValidateTrimOutput(ProbeResult input, ProbeResult output, MediaOperation operation, MediaOptions options, double expectedDuration)
    {
        var video = operation == MediaOperation.VideoTrim;
        var metadata = output.Metadata;
        if (video && (metadata.Kind != "Video" || metadata.Width <= 0 || metadata.Height <= 0 || metadata.Format != "MP4"))
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_20C71331CD4A"));
        if (!video && (metadata.Kind != "Audio" || !metadata.HasAudio || metadata.Format != "WAV"))
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_3BFF08E2FF99"));
        var expectsAudio = !video || AudioOverlaps(input, options.TrimStartSeconds, options.TrimEndSeconds!.Value);
        if (expectsAudio && !metadata.HasAudio)
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_C9523ADC17FC"));
        // Video boundaries are quantized to actual decoded frames; audio is trimmed at sample boundaries.
        var tolerance = video ? Math.Max(0.1, input.VideoFrameDurationSeconds * 1.1) : 0.05;
        if (metadata.DurationSeconds <= 0 || Math.Abs(metadata.DurationSeconds - expectedDuration) > tolerance)
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_4346EA76D352"));
        if (Math.Abs(output.StartTimeSeconds) > tolerance)
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_C7DA1171DE64"));
    }

    private static void ValidateOutput(ProbeResult input, MediaMetadata output, MediaOperation operation)
    {
        var source = input.Metadata;
        var video = operation is MediaOperation.VideoMp4 or MediaOperation.VideoWebm or MediaOperation.VideoCompress or MediaOperation.VideoFrame;
        if (video && (output.Kind != "Video" || output.Width <= 0 || output.Height <= 0))
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_82B0167EFA77"));
        if (!video && (output.Kind != "Audio" || !output.HasAudio))
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_4EE97339CEF7"));
        if (video && operation != MediaOperation.VideoFrame && source.HasAudio && !output.HasAudio)
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_102217C69448"));
        var expectedDuration = operation == MediaOperation.ExtractMp3 ? input.AudioDurationSeconds : source.DurationSeconds;
        if (operation != MediaOperation.VideoFrame && expectedDuration > 0 &&
            (output.DurationSeconds <= 0 || output.DurationSeconds + Math.Max(1.5, expectedDuration * 0.02) < expectedDuration))
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_35C8DF54A36A"));
    }

    private static List<string> InputArguments(string path) =>
        ["-hide_banner", "-v", "error", "-nostdin", "-n", "-nostats", "-threads", "2", "-protocol_whitelist", "file,pipe", "-i", path];

    private static void AddOperationArguments(List<string> args, ProbeResult source, MediaOperation operation, MediaOptions options)
    {
        if (operation is MediaOperation.VideoTrim or MediaOperation.AudioTrim)
        {
            AddTrimArguments(args, source, operation, options);
            return;
        }
        var video = operation is MediaOperation.VideoMp4 or MediaOperation.VideoWebm or MediaOperation.VideoCompress or MediaOperation.VideoFrame;
        args.AddRange(["-sn", "-dn"]);
        if (video)
        {
            args.AddRange(["-map", "0:" + Invariant(source.VideoIndex!.Value)]);
            if (operation == MediaOperation.VideoFrame)
            {
                args.AddRange(["-an", "-frames:v", "1", "-vf", "scale=iw*sar:ih,setsar=1", "-c:v", "png", "-threads:v", "1", "-update", "1"]);
                return;
            }
            if (source.AudioIndex is not null) args.AddRange(["-map", "0:" + Invariant(source.AudioIndex.Value)]);
            args.AddRange(["-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-pix_fmt", "yuv420p", "-threads:v", "2"]);
            if (operation == MediaOperation.VideoWebm)
            {
                args.AddRange(["-c:v", "libvpx-vp9", "-crf", Invariant(options.VideoCrf), "-b:v", "0", "-deadline", "good", "-cpu-used", "4"]);
                if (source.AudioIndex is not null) args.AddRange(["-c:a", "libopus", "-b:a", Invariant(options.AudioBitrateKbps) + "k", "-ar", "48000"]);
            }
            else
            {
                args.AddRange(["-c:v", "libx264", "-preset", "medium", "-crf", Invariant(operation == MediaOperation.VideoCompress ? 28 : options.VideoCrf), "-movflags", "+faststart"]);
                if (source.AudioIndex is not null) args.AddRange(["-c:a", "aac", "-b:a", Invariant(operation == MediaOperation.VideoCompress ? 128 : options.AudioBitrateKbps) + "k"]);
            }
            return;
        }
        args.AddRange(["-map", "0:" + Invariant(source.AudioIndex!.Value), "-vn"]);
        switch (operation)
        {
            case MediaOperation.ExtractMp3:
            case MediaOperation.AudioMp3:
            case MediaOperation.AudioCompress:
                args.AddRange(["-c:a", "libmp3lame", "-b:a", Invariant(operation == MediaOperation.AudioCompress ? 128 : options.AudioBitrateKbps) + "k"]);
                if (source.AudioChannels > 2) args.AddRange(["-ac", "2"]);
                break;
            case MediaOperation.AudioWav:
                args.AddRange(["-c:a", "pcm_s16le"]);
                break;
            case MediaOperation.AudioFlac:
                args.AddRange(["-c:a", "flac", "-compression_level", "8"]);
                break;
        }
    }

    private static void AddTrimArguments(List<string> args, ProbeResult source, MediaOperation operation, MediaOptions options)
    {
        var start = Seconds(options.TrimStartSeconds);
        var end = Seconds(options.TrimEndSeconds!.Value);
        var length = Seconds(options.TrimEndSeconds.Value - options.TrimStartSeconds);
        args.AddRange(["-sn", "-dn"]);
        if (operation == MediaOperation.AudioTrim)
        {
            args.AddRange(["-map", "0:" + Invariant(source.AudioIndex!.Value), "-vn", "-af",
                $"atrim=start={start}:end={end},asetpts=PTS-{start}/TB", "-c:a", "pcm_f32le", "-t", length]);
            return;
        }
        args.AddRange(["-map", "0:" + Invariant(source.VideoIndex!.Value), "-vf",
            $"trim=start={start}:end={end},setpts=PTS-{start}/TB,pad=ceil(iw/2)*2:ceil(ih/2)*2",
            "-pix_fmt", "yuv420p", "-threads:v", "2", "-c:v", "libx264", "-preset", "medium", "-crf", Invariant(options.VideoCrf), "-movflags", "+faststart", "-t", length]);
        if (AudioOverlaps(source, options.TrimStartSeconds, options.TrimEndSeconds.Value))
            args.AddRange(["-map", "0:" + Invariant(source.AudioIndex!.Value), "-af",
                $"atrim=start={start}:end={end},asetpts=PTS-{start}/TB", "-c:a", "aac", "-b:a", Invariant(options.AudioBitrateKbps) + "k"]);
        else args.Add("-an");
    }

    private static bool AudioOverlaps(ProbeResult source, double start, double end) => source.AudioIndex is not null
        && (source.AudioDurationSeconds <= 0 || (end > source.AudioStartSeconds && start < source.AudioStartSeconds + source.AudioDurationSeconds));

    private static string Seconds(double value) => value.ToString("0.################", CultureInfo.InvariantCulture);

    private async Task<ProbeResult> ProbeAsync(string path, bool allowStillImage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var args = new List<string>
        {
            "-hide_banner", "-v", "error", "-protocol_whitelist", "file,pipe", "-show_entries",
            "format=format_name,duration,start_time:format_tags=major_brand:stream=index,codec_type,codec_name,width,height,duration,start_time,avg_frame_rate,sample_rate,channels:stream_disposition=attached_pic:stream_tags=rotate:stream_side_data=rotation",
            "-of", "json", "-i", path
        };
        var bytes = await RunAsync(ResolveFfprobe(), args, TimeSpan.FromSeconds(30), cancellationToken, maxOutputBytes: 2 * 1024 * 1024).ConfigureAwait(false);
        try
        {
            using var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;
            if (!root.TryGetProperty("format", out var formatElement) || !root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_9D19D2EE911D"));
            var formatNames = Text(formatElement, "format_name");
            var names = formatNames.Split(',');
            if (!names.Any(Containers.Contains) && !(allowStillImage && names.Contains("png_pipe")))
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_AC84858E874B"));
            int? videoIndex = null, audioIndex = null;
            var width = 0;
            var height = 0;
            var channels = 0;
            double audioDuration = 0;
            double audioStart = 0;
            double videoFrameDuration = 0;
            var formatStart = Number(formatElement, "start_time");
            var duration = Number(formatElement, "duration");
            foreach (var stream in streams.EnumerateArray())
            {
                duration = Math.Max(duration, Number(stream, "duration"));
                var codec = Text(stream, "codec_name");
                if (codec.Length == 0 || codec == "unknown") continue;
                var index = Integer(stream, "index", -1);
                if (index < 0) continue;
                var type = Text(stream, "codec_type");
                var attachedPicture = stream.TryGetProperty("disposition", out var disposition) && Integer(disposition, "attached_pic") != 0;
                if (type == "video" && !attachedPicture && videoIndex is null)
                {
                    var candidateWidth = Integer(stream, "width");
                    var candidateHeight = Integer(stream, "height");
                    if (candidateWidth <= 0 || candidateHeight <= 0) continue;
                    width = candidateWidth;
                    height = candidateHeight;
                    var rotation = Rotation(stream);
                    if (Math.Abs(rotation % 180) > 45 && Math.Abs(rotation % 180) < 135) (width, height) = (height, width);
                    videoIndex = index;
                    var rate = Text(stream, "avg_frame_rate").Split('/');
                    if (rate.Length == 2 && double.TryParse(rate[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var rateNumerator)
                        && double.TryParse(rate[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var rateDenominator)
                        && rateNumerator > 0 && rateDenominator > 0 && double.IsFinite(rateNumerator) && double.IsFinite(rateDenominator))
                        videoFrameDuration = rateDenominator / rateNumerator;
                }
                if (type == "audio" && audioIndex is null && Integer(stream, "channels") > 0 && Number(stream, "sample_rate") > 0)
                {
                    audioIndex = index;
                    channels = Integer(stream, "channels");
                    audioDuration = Number(stream, "duration");
                    audioStart = Number(stream, "start_time") - formatStart;
                }
            }
            if (videoIndex is null && audioIndex is null) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_F26AFBF41667"));
            var metadata = new MediaMetadata(path, DisplayFormat(names, formatElement), videoIndex is null ? "Audio" : "Video",
                width, height, double.IsFinite(duration) && duration > 0 ? duration : 0, new FileInfo(path).Length, audioIndex is not null);
            return new ProbeResult(metadata, videoIndex, audioIndex, channels, audioDuration, audioStart, formatStart, videoFrameDuration);
        }
        catch (JsonException exception) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_34A33BAC35BE"), exception); }
    }

    private static string DisplayFormat(string[] names, JsonElement format)
    {
        if (names.Contains("png_pipe")) return "PNG";
        if (names.Contains("mov"))
        {
            var brand = format.TryGetProperty("tags", out var tags) ? Text(tags, "major_brand").Trim() : "";
            return brand == "qt" ? "MOV" : brand.StartsWith("M4A", StringComparison.OrdinalIgnoreCase) ? "M4A" : "MP4";
        }
        // FFprobe deliberately reports Matroska/WebM together; preserve that ambiguity for source metadata.
        if (names.Contains("matroska")) return "MATROSKA/WEBM";
        return names[0].ToUpperInvariant();
    }

    private static double Rotation(JsonElement stream)
    {
        if (stream.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array)
            foreach (var entry in sideData.EnumerateArray())
                if (entry.TryGetProperty("rotation", out _)) return Number(entry, "rotation");
        return stream.TryGetProperty("tags", out var tags) ? Number(tags, "rotate") : 0;
    }

    private static string Text(JsonElement item, string key) => item.TryGetProperty(key, out var value)
        ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString() : "";
    private static double Number(JsonElement item, string key) => double.TryParse(Text(item, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : 0;
    private static int Integer(JsonElement item, string key, int fallback = 0) => int.TryParse(Text(item, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_49EE14CFB4D4"));
        string fullPath;
        try { fullPath = System.IO.Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_6597F72A922E"), exception); }
        if (!Extensions.Contains(System.IO.Path.GetExtension(fullPath))) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_1FDCD055D2D3"));
        if (!File.Exists(fullPath)) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_9D96D1202997"));
        return fullPath;
    }

    private static string OutputExtension(MediaOperation operation) => operation switch
    {
        MediaOperation.VideoMp4 or MediaOperation.VideoCompress or MediaOperation.VideoTrim => "mp4",
        MediaOperation.VideoWebm => "webm",
        MediaOperation.VideoFrame => "png",
        MediaOperation.AudioWav or MediaOperation.AudioTrim => "wav",
        MediaOperation.AudioFlac => "flac",
        _ => "mp3"
    };

    private static string MoveToUniquePath(string temporaryPath, string directory, string sourcePath, MediaOperation operation, string extension)
    {
        var label = operation switch
        {
            MediaOperation.VideoCompress => "compressed_h264_crf28",
            MediaOperation.AudioCompress => "compressed_mp3_128k",
            MediaOperation.VideoFrame => "frame",
            MediaOperation.ExtractMp3 => "audio",
            MediaOperation.VideoTrim => "trimmed",
            MediaOperation.AudioTrim => "trimmed_f32",
            _ => "converted"
        };
        var stem = System.IO.Path.GetFileNameWithoutExtension(sourcePath) + "_" + label;
        for (var index = 0; index < 10000; index++)
        {
            var candidate = System.IO.Path.Combine(directory, stem + (index == 0 ? "" : "_" + Invariant(index)) + "." + extension);
            if (string.Equals(candidate, sourcePath, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Move(temporaryPath, candidate, overwrite: false); return candidate; }
            catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate)) { }
        }
        throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_90E83DE2EF24"));
    }

    private string ResolveFfmpeg()
    {
        var configured = ExecutableFromSetting(_configuredExecutable, "ffmpeg");
        var environment = ExecutableFromSetting(Environment.GetEnvironmentVariable("ORBIT_FFMPEG"), "ffmpeg");
        return ResolveExisting([configured, System.IO.Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", ExecutableName("ffmpeg")), environment,
            @"C:\ffmpeg\bin\ffmpeg.exe"], "ffmpeg")
            ?? throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_58328ADE8581"));
    }

    private string ResolveFfprobe()
    {
        var ffmpeg = ResolveFfmpeg();
        // Require the matching probe from the same build instead of silently mixing installations.
        var candidate = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ffmpeg)!, ExecutableName("ffprobe"));
        if (File.Exists(candidate)) return candidate;
        throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_3FAD659BD232"));
    }

    private static string ExecutableName(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;
    private static string? ExecutableFromSetting(string? setting, string name) => string.IsNullOrWhiteSpace(setting) ? null
        : Directory.Exists(setting) ? System.IO.Path.Combine(setting, ExecutableName(name)) : setting;
    private static string? ResolveExisting(IEnumerable<string?> candidates, string executable)
    {
        foreach (var candidate in candidates)
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate)) return System.IO.Path.GetFullPath(candidate);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator))
        {
            try
            {
                var candidate = System.IO.Path.Combine(directory.Trim('"'), ExecutableName(executable));
                if (File.Exists(candidate)) return System.IO.Path.GetFullPath(candidate);
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    private static async Task<byte[]> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout,
        CancellationToken cancellationToken, Action<string>? progressLine = null, int maxOutputBytes = 65536)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        try { if (!process.Start()) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_B46C4B91FF72")); }
        catch (System.ComponentModel.Win32Exception exception) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_FD423628FAF8"), exception); }
        var stdoutTask = progressLine is null ? ReadLimitedBytesAsync(process.StandardOutput.BaseStream, maxOutputBytes) : ReadProgressAsync(process.StandardOutput, progressLine);
        var stderrTask = ReadLimitedBytesAsync(process.StandardError.BaseStream, 65536);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try { await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_03A109A2F45D"));
        }
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            var error = Encoding.UTF8.GetString(stderr);
            if (error.Length > 1200) error = error[..1200];
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_7D7128522CDA") + error.Trim());
        }
        if (stdout.Length > maxOutputBytes) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_1030A1075DAA"));
        return stdout;
    }

    private static async Task<byte[]> ReadLimitedBytesAsync(Stream stream, int maximumBytes)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer).ConfigureAwait(false)) != 0)
            if (output.Length <= maximumBytes) output.Write(buffer, 0, (int)Math.Min(count, maximumBytes + 1L - output.Length));
        return output.ToArray();
    }

    private static async Task<byte[]> ReadProgressAsync(StreamReader reader, Action<string> onLine)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line) onLine(line);
        return [];
    }

    private static void Report(IProgress<double>? progress, double value) => progress?.Report(value);
    private sealed record ProbeResult(MediaMetadata Metadata, int? VideoIndex, int? AudioIndex, int AudioChannels,
        double AudioDurationSeconds, double AudioStartSeconds, double StartTimeSeconds, double VideoFrameDurationSeconds);
}
