[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Orbit.MediaTracks.Tests")]

namespace Orbit.Core.Media;

public sealed record MediaTrack(int StreamIndex, string Type, string Codec, string Language, string Title,
    int Channels, int SampleRate, int Width, int Height, double DurationSeconds, long Bitrate,
    bool IsAttachedPicture, bool IsDefault);

public sealed record MediaTrackCatalog(string Path, string SourceSha256, long Bytes, string Format,
    double DurationSeconds, IReadOnlyList<MediaTrack> Streams);

public sealed record AudioTrackExtractOptions
{
    public int StreamIndex { get; init; }
    public string SourceSha256 { get; init; } = "";
    public string Format { get; init; } = "MP3";
    public int BitrateKbps { get; init; } = 192;
    public string? OutputDirectory { get; init; }
}
