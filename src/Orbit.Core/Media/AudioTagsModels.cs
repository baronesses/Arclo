namespace Orbit.Core.Media;

public sealed record AudioTagMetadata(string Path, string Format, string Title, string Artist, string Album);
public sealed record AudioTagOptions
{
    public bool SetTitle { get; init; }
    public string Title { get; init; } = "";
    public bool SetArtist { get; init; }
    public string Artist { get; init; } = "";
    public bool SetAlbum { get; init; }
    public string Album { get; init; } = "";
    public string? OutputDirectory { get; init; }
}
