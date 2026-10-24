namespace Orbit.Core;

public enum MediaOperation
{
    VideoMp4, VideoWebm, ExtractMp3, VideoFrame, VideoCompress,
    AudioMp3, AudioWav, AudioFlac, AudioCompress,
    VideoTrim, AudioTrim
}

public sealed record MediaOptions
{
    public string? OutputDirectory { get; init; }
    public int VideoCrf { get; init; } = 24;
    public int AudioBitrateKbps { get; init; } = 192;
    public double TrimStartSeconds { get; init; }
    /// <summary>Required for trim operations; exclusive end on the source timeline, in seconds.</summary>
    public double? TrimEndSeconds { get; init; }
}

public sealed record MediaMetadata(string Path, string Format, string Kind, int Width, int Height,
    double DurationSeconds, long Bytes, bool HasAudio);

public sealed record MediaResult(string OutputPath, long Bytes, int Width, int Height, string Format, long SourceBytes)
{
    public double SavedPercent => SourceBytes > 0 ? (1d - (double)Bytes / SourceBytes) * 100d : 0;
}

public sealed class MediaProcessingException : Exception
{
    public MediaProcessingException(string message) : base(message) { }
    public MediaProcessingException(string message, Exception innerException) : base(message, innerException) { }
}
