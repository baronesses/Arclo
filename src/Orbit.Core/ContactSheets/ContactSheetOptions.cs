using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Orbit.ContactSheetSmoke")]

namespace Orbit.Core.ContactSheets;

public sealed record ContactSheetOptions
{
    public string? OutputDirectory { get; init; }
    public int Columns { get; init; } = 3;
    public int ThumbnailSize { get; init; } = 320;
    /// <summary>Space between cells and around the outer border, in pixels.</summary>
    public int Gap { get; init; } = 12;
    public bool WhiteBackground { get; init; } = true;
}
