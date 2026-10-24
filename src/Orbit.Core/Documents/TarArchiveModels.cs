namespace Orbit.Core.Documents;

public sealed record TarCreateOptions
{
    public string Format { get; init; } = "TAR.GZ";
    public string Compression { get; init; } = "Optimal";
    public string? OutputDirectory { get; init; }
}

public sealed record TarArchiveEntry(string RelativeName, bool IsDirectory, long Bytes);
public sealed record TarArchiveListing(string SourcePath, string Format, IReadOnlyList<TarArchiveEntry> Entries, long TotalBytes, string SourceFingerprint = "");
