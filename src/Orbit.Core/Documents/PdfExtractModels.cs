using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Orbit.PdfExtractSmoke")]

namespace Orbit.Core.Documents;

public sealed record PdfTextOptions
{
    public string? OutputDirectory { get; init; }
    public bool PreserveLayout { get; init; } = true;
}

public sealed record PdfEmbeddedImagesOptions
{
    public string? OutputDirectory { get; init; }
}
