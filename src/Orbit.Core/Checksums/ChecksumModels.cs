using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Orbit.Checksum.Tests")]

namespace Orbit.Core.Checksums;

public enum ChecksumStatus { Match, Mismatch, Missing, Error }
public sealed record ChecksumFileResult(string Path, string ExpectedSha256, string? ActualSha256, ChecksumStatus Status, long Bytes, string? Message);
public sealed record ChecksumVerificationResult(IReadOnlyList<ChecksumFileResult> Entries, bool AllMatch, long TotalBytes);
public class ChecksumProcessingException : Exception
{
    public ChecksumProcessingException(string message) : base(message) { }
    public ChecksumProcessingException(string message, Exception innerException) : base(message, innerException) { }
}
