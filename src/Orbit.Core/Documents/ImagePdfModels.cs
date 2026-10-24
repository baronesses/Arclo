namespace Orbit.Core.Documents;

public enum ImagePdfPageSize { A4, A4Landscape, FitImage }

public sealed record ImagePdfOptions
{
    public string? OutputDirectory { get; init; }
    public ImagePdfPageSize PageSize { get; init; } = ImagePdfPageSize.A4;
    public double MarginMillimeters { get; init; } = 10;
    public int JpegQuality { get; init; } = 90;
}
