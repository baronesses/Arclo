namespace Orbit.Core.Documents;

public enum PdfOperation { PngPages, JpgPages, ExtractText }

public sealed record PdfOptions
{
    public string? OutputDirectory { get; init; }
    public int Dpi { get; init; } = 150;
}

public sealed record PdfMetadata(string Path, int Pages, long Bytes);
public sealed record ZipMetadata(string Path, int Entries, long Bytes, long ExpandedBytes);
public sealed record DocumentResult(string OutputPath, long Bytes, string Format, long SourceBytes);

public sealed class DocumentProcessingException : Exception
{
    public DocumentProcessingException(string message) : base(message) { }
    public DocumentProcessingException(string message, Exception innerException) : base(message, innerException) { }
}
