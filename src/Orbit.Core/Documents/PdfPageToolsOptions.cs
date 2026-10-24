namespace Orbit.Core.Documents;

public sealed record PdfRotateOptions
{
    public string? OutputDirectory { get; init; }
    /// <summary>Relative clockwise rotation, added once to each selected page's existing rotation.</summary>
    public int RotationDegrees { get; init; } = 90;
    public string PageSelection { get; init; } = "1";
}

public sealed record PdfSplitOptions
{
    public string? OutputDirectory { get; init; }
    public int PagesPerFile { get; init; } = 1;
}
