using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Orbit.VideoExportSmoke")]

namespace Orbit.Core.Media;

/// <summary>A new H.264 MP4 with square pixels. Sources are never modified.</summary>
public sealed record VideoExportOptions
{
    public string? OutputDirectory { get; init; }
    /// <summary>Display width after rotation; null keeps native display size within the 3840 px output bounds.</summary>
    public int? Width { get; init; }
    public bool NoUpscale { get; init; } = true;
    public bool RemoveAudio { get; init; }
    public int VideoCrf { get; init; } = 24;
    /// <summary>Null keeps the original timing; otherwise a constant frame rate from 1 to 60.</summary>
    public int? FramesPerSecond { get; init; }
}
