using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Orbit.VideoFramesSmoke")]

namespace Orbit.Core.Media;

public sealed record VideoFramesOptions
{
    public string? OutputDirectory { get; init; }
    public double StartSeconds { get; init; }
    public double DurationSeconds { get; init; } = 3;
    public int FramesPerSecond { get; init; } = 1;
    public int Width { get; init; } = 1280;
}
