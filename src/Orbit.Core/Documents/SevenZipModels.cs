namespace Orbit.Core.Documents;

public sealed record SevenZipCreateOptions
{
    public string Compression { get; init; } = "Optimal";
    public string? OutputDirectory { get; init; }
}
public sealed record SevenZipArchiveEntry(string RelativeName, bool IsDirectory, long Bytes);
public sealed record SevenZipArchiveListing(string SourcePath, string Format, IReadOnlyList<SevenZipArchiveEntry> Entries, long TotalBytes, string SourceFingerprint = "");
