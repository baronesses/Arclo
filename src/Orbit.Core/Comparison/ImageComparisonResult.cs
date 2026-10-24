namespace Orbit.Core.Comparison;

/// <summary>A coherent read of the current two files, not a historical job snapshot.</summary>
public sealed record ImageComparisonResult(ImageMetadata Original, ImageMetadata Result,
    byte[] BeforePng, byte[] AfterPng, bool AspectCompatible)
{
    public long OriginalBytes => Original.Bytes;
    public long ResultBytes => Result.Bytes;
}
