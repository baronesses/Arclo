namespace Orbit.Core.Ocr;

public sealed record OcrOptions
{
    public string Language { get; init; } = "rus+eng";
    public int Dpi { get; init; } = 200;
    public string? OutputDirectory { get; init; }
}
