namespace Orbit.Core.Media;

public sealed record AudioAdjustOptions
{
    public string? OutputDirectory { get; init; }
    public double VolumePercent { get; init; } = 100;
    public double FadeInSeconds { get; init; }
    public double FadeOutSeconds { get; init; }
}
