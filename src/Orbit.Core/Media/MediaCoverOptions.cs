using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Orbit.MediaCover.Tests")]

namespace Orbit.Core.Media;

public sealed record MediaCoverOptions
{
    public string? OutputDirectory { get; init; }
}
