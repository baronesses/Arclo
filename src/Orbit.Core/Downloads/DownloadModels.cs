namespace Orbit.Core.Downloads;

public enum DownloadMode { AudioMp3, VideoMp4 }

public sealed record DownloadRequest
{
    public string Url { get; init; } = "";
    public string OutputDirectory { get; init; } = "";
    public DownloadMode Mode { get; init; } = DownloadMode.AudioMp3;
}

public sealed record DownloadProgress(double? Fraction, string Stage, double? SpeedBytesPerSecond = null, double? EtaSeconds = null);
public sealed record DownloadResult(string OutputPath, long Bytes, DownloadMode Mode, string Format, int Width = 0, int Height = 0, double DurationSeconds = 0);
public sealed class DownloadException(string message) : Exception(message);
