namespace Orbit.Core.Comparison;

public readonly record struct PixelCropRect(int X, int Y, int Width, int Height);
public readonly record struct CropPoint(double X, double Y);
public readonly record struct CropDisplayRect(double X, double Y, double Width, double Height);

/// <summary>Image-edge coordinates in the already oriented original's pixel space; no UI or EXIF decoding.</summary>
public static class CropSelectionMath
{
    public static bool IsValidAspectRatio(double? ratio) => ratio is null || double.IsFinite(ratio.Value) && ratio > 0;

    public static bool TryValidate(int width, int height, PixelCropRect rectangle, double? ratio, out string? reason)
    {
        reason = null;
        if (width <= 0 || height <= 0) reason = global::Orbit.Core.Localization.L.Get("Text_1EC491CA3925");
        else if (!IsValidAspectRatio(ratio)) reason = global::Orbit.Core.Localization.L.Get("Text_5DB9B188DF40");
        else if (rectangle.X < 0 || rectangle.Y < 0 || rectangle.Width < 1 || rectangle.Height < 1
            || (long)rectangle.X + rectangle.Width > width || (long)rectangle.Y + rectangle.Height > height)
            reason = global::Orbit.Core.Localization.L.Get("Text_B839FD619254");
        else if (ratio is { } value && Math.Abs(rectangle.Width - rectangle.Height * value) > 1.000000001
            && Math.Abs(rectangle.Height - rectangle.Width / value) > 1.000000001)
            reason = global::Orbit.Core.Localization.L.Get("Text_ACCBCB36367A");
        return reason is null;
    }

    public static PixelCropRect Centered(int width, int height, PixelCropRect? current = null, double? ratio = null)
    {
        ValidateImage(width, height, ratio);
        var bounds = current is { } candidate && TryValidate(width, height, candidate, null, out _) ? candidate : new PixelCropRect(0, 0, width, height);
        if (ratio is null) return bounds;
        var (w, h) = FitSize(bounds.Width, bounds.Height, ratio.Value);
        return new PixelCropRect(bounds.X + (bounds.Width - w) / 2, bounds.Y + (bounds.Height - h) / 2, w, h);
    }

    /// <summary>Anchored drag into any quadrant. The dominant mouse axis controls ratio size.</summary>
    public static PixelCropRect FromDrag(int width, int height, CropPoint start, CropPoint end, double? ratio = null)
    {
        ValidateImage(width, height, ratio);
        if (!double.IsFinite(start.X) || !double.IsFinite(start.Y) || !double.IsFinite(end.X) || !double.IsFinite(end.Y))
            throw new ArgumentOutOfRangeException(nameof(start), global::Orbit.Core.Localization.L.Get("Text_09CDB5E53872"));
        var sx = Math.Clamp(start.X, 0, width);var sy = Math.Clamp(start.Y, 0, height);
        var ex = Math.Clamp(end.X, 0, width);var ey = Math.Clamp(end.Y, 0, height);
        var anchorX = Round(sx, width);var anchorY = Round(sy, height);
        var right = ex >= sx;var down = ey >= sy;
        if (right ? anchorX == width : anchorX == 0) right = !right;
        if (down ? anchorY == height : anchorY == 0) down = !down;
        var availableWidth = right ? width - anchorX : anchorX;var availableHeight = down ? height - anchorY : anchorY;
        int w, h;
        if (ratio is null)
        {
            w = Math.Clamp(Round(Math.Abs(ex - sx), availableWidth), 1, availableWidth);
            h = Math.Clamp(Round(Math.Abs(ey - sy), availableHeight), 1, availableHeight);
        }
        else
        {
            var (capWidth, capHeight) = ContinuousFit(availableWidth, availableHeight, ratio.Value);
            var normalizedWidth = capWidth > 0 ? Math.Abs(ex - sx) / capWidth : ex == sx ? 0 : double.PositiveInfinity;
            var normalizedHeight = capHeight > 0 ? Math.Abs(ey - sy) / capHeight : ey == sy ? 0 : double.PositiveInfinity;
            var scale = Math.Min(1, Math.Max(normalizedWidth, normalizedHeight));
            w = Math.Clamp(Round(capWidth * scale, availableWidth), 1, availableWidth);
            h = Math.Clamp(Round(capHeight * scale, availableHeight), 1, availableHeight);
        }
        return new PixelCropRect(right ? anchorX : anchorX - w, down ? anchorY : anchorY - h, w, h);
    }

    public static CropDisplayRect FitDisplay(int width, int height, double viewportWidth, double viewportHeight)
    {
        if (width <= 0 || height <= 0 || !double.IsFinite(viewportWidth) || !double.IsFinite(viewportHeight) || viewportWidth <= 0 || viewportHeight <= 0) return default;
        var scale = Math.Min(viewportWidth / width, viewportHeight / height);var w = width * scale;var h = height * scale;
        return new CropDisplayRect((viewportWidth - w) / 2, (viewportHeight - h) / 2, w, h);
    }

    public static CropPoint ToImagePoint(int width, int height, CropDisplayRect display, CropPoint point)
    {
        if (width <= 0 || height <= 0 || display.Width <= 0 || display.Height <= 0 || !double.IsFinite(display.X) || !double.IsFinite(display.Y)
            || !double.IsFinite(display.Width) || !double.IsFinite(display.Height) || !double.IsFinite(point.X) || !double.IsFinite(point.Y))
            throw new ArgumentOutOfRangeException(nameof(display), global::Orbit.Core.Localization.L.Get("Text_3468F629CAFA"));
        return new CropPoint(Math.Clamp((point.X - display.X) / display.Width * width, 0, width), Math.Clamp((point.Y - display.Y) / display.Height * height, 0, height));
    }

    private static (double Width, double Height) ContinuousFit(int width, int height, double ratio) => ratio >= width / (double)height ? (width, width / ratio) : (height * ratio, height);
    private static (int Width, int Height) FitSize(int width, int height, double ratio)
    { var fit = ContinuousFit(width, height, ratio);return (Math.Clamp(Round(fit.Width, width), 1, width), Math.Clamp(Round(fit.Height, height), 1, height)); }
    private static int Round(double value, int maximum) => (int)Math.Round(Math.Clamp(value, 0, maximum), MidpointRounding.AwayFromZero);
    private static void ValidateImage(int width, int height, double? ratio)
    { if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));if (!IsValidAspectRatio(ratio)) throw new ArgumentOutOfRangeException(nameof(ratio)); }
}
