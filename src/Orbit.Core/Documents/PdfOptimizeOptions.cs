[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Orbit.PdfOptimizeSmoke")]

namespace Orbit.Core.Documents;

public sealed record PdfOptimizeOptions
{
    public string? OutputDirectory { get; init; }
    public bool Linearize { get; init; }
}
