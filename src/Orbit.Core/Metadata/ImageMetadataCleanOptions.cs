namespace Orbit.Core.Metadata;

/// <summary>Creates a new same-format copy; never modifies the source image.</summary>
public sealed record ImageMetadataCleanOptions
{
    public string? OutputDirectory { get; init; }
}
