namespace Orbit.Core.Watermarks;

public enum WatermarkMode { Text, Logo }
public enum WatermarkPosition { TopLeft, Top, TopRight, Left, Center, Right, BottomLeft, Bottom, BottomRight }
public sealed record WatermarkOptions
{
    public WatermarkMode Mode { get; init; } = WatermarkMode.Text;
    public string Text { get; init; } = "";
    public string FontFamily { get; init; } = "SegoeUI";
    public string Color { get; init; } = "#FFFFFF";
    public double RelativeSizePercent { get; init; } = 5;
    public WatermarkPosition Position { get; init; } = WatermarkPosition.BottomRight;
    public double OpacityPercent { get; init; } = 50;
    public double MarginPercent { get; init; } = 3;
    public string? LogoPath { get; init; }
    public string? LogoSha256 { get; init; }
    public double LogoWidthPercent { get; init; } = 20;
    public string? OutputDirectory { get; init; }
}
public sealed record WatermarkLogoInfo(string Path, string Sha256, ImageMetadata Image);
