[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Orbit.AudioExportSmoke")]

namespace Orbit.Core.Media;

public sealed record AudioExportOptions
{
    public string? OutputDirectory { get; init; }
    public string Format { get; init; } = "M4A";
    public int BitrateKbps { get; init; } = 192;
    /// <summary>Vorbis variable-bitrate quality (0..10); BitrateKbps is inactive for VORBIS.</summary>
    public int VorbisQuality { get; init; } = 5;
}
