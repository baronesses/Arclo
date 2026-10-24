namespace Orbit.Core;

/// <summary>EXIF auto-orientation, then clockwise rotation, then flips in the rotated image's coordinates.</summary>
public sealed record ImageTransformOptions
{
    /// <summary>Only 0, 90, 180 and 270 degrees are supported.</summary>
    public int RotationDegrees { get; init; }
    public bool FlipHorizontal { get; init; }
    public bool FlipVertical { get; init; }
    public string? OutputDirectory { get; init; }
}
