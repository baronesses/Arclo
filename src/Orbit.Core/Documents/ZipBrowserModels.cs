namespace Orbit.Core.Documents;

public sealed record ZipBrowserEntry(string RelativeName, bool IsDirectory, long UncompressedBytes, long CompressedBytes)
{
    public string Id => RelativeName;
}

public sealed record ZipBrowserListing(string SourcePath, string SourceFingerprint,
    IReadOnlyList<ZipBrowserEntry> Entries, long TotalBytes)
{
    public string SourceSha256 => SourceFingerprint;
}
