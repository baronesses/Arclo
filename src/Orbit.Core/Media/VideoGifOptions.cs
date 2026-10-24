namespace Orbit.Core.Media;

public sealed record VideoGifOptions
{
    public string? OutputDirectory { get; init; }
    public double StartSeconds { get; init; }
    public double DurationSeconds { get; init; } = 3;
    /// <summary>Requested width. The whole frame is contained within 1280 pixels per side and one megapixel.</summary>
    public int Width { get; init; } = 480;
    public int FramesPerSecond { get; init; } = 12;
}
