using System;
using System.Windows;

namespace Orbit.App.Layout;

/// <summary>Popup dimensions in WPF units and its position in the virtual screen's physical pixels.</summary>
public readonly record struct PopupPlacement(
    double LeftPhysical,
    double TopPhysical,
    double WidthLogical,
    double HeightLogical,
    double WidthPhysical,
    double HeightPhysical)
{
    public Rect PhysicalBounds => new(LeftPhysical,TopPhysical,WidthPhysical,HeightPhysical);
    public Size LogicalSize => new(WidthLogical,HeightLogical);
}

/// <summary>Pure placement calculations. The caller supplies the cursor monitor's physical working area and DPI.</summary>
public static class PopupPlacementMath
{
    /// <summary>
    /// Centers a requested logical popup at a captured physical cursor position, then keeps it within the working area.
    /// Width and height are capped independently; a Viewbox can retain a radial menu's circular proportions.
    /// Negative monitor coordinates are preserved. No window, cursor, or system DPI state is read here.
    /// </summary>
    public static PopupPlacement CenterAtCursor(
        Point cursorPhysical,
        Rect workAreaPhysical,
        Size desiredLogicalSize,
        double dpiScaleX,
        double dpiScaleY,
        double marginLogical=8)
    {
        if(!double.IsFinite(cursorPhysical.X)||!double.IsFinite(cursorPhysical.Y))
            throw new ArgumentOutOfRangeException(nameof(cursorPhysical),"Cursor coordinates must be finite physical pixels.");
        if(workAreaPhysical.IsEmpty||!double.IsFinite(workAreaPhysical.Left)||!double.IsFinite(workAreaPhysical.Top)||
           !PositiveFinite(workAreaPhysical.Width)||!PositiveFinite(workAreaPhysical.Height)||
           !double.IsFinite(workAreaPhysical.Right)||!double.IsFinite(workAreaPhysical.Bottom))
            throw new ArgumentOutOfRangeException(nameof(workAreaPhysical),"The physical working area must have positive finite dimensions.");
        if(desiredLogicalSize.IsEmpty||!PositiveFinite(desiredLogicalSize.Width)||!PositiveFinite(desiredLogicalSize.Height))
            throw new ArgumentOutOfRangeException(nameof(desiredLogicalSize),"The desired logical size must have positive finite dimensions.");
        if(!PositiveFinite(dpiScaleX))throw new ArgumentOutOfRangeException(nameof(dpiScaleX));
        if(!PositiveFinite(dpiScaleY))throw new ArgumentOutOfRangeException(nameof(dpiScaleY));
        if(!double.IsFinite(marginLogical)||marginLogical<0)throw new ArgumentOutOfRangeException(nameof(marginLogical));

        // Even a tiny supplied working area retains usable space when its requested margin is too large.
        var marginX=Math.Min(marginLogical*dpiScaleX,Math.Max(0,(workAreaPhysical.Width-1)/2));
        var marginY=Math.Min(marginLogical*dpiScaleY,Math.Max(0,(workAreaPhysical.Height-1)/2));
        var availableWidth=workAreaPhysical.Width-2*marginX;
        var availableHeight=workAreaPhysical.Height-2*marginY;
        var widthLogical=Math.Min(desiredLogicalSize.Width,availableWidth/dpiScaleX);
        var heightLogical=Math.Min(desiredLogicalSize.Height,availableHeight/dpiScaleY);
        var widthPhysical=Math.Min(availableWidth,widthLogical*dpiScaleX);
        var heightPhysical=Math.Min(availableHeight,heightLogical*dpiScaleY);
        var minimumLeft=workAreaPhysical.Left+marginX;
        var minimumTop=workAreaPhysical.Top+marginY;
        var maximumLeft=Math.Max(minimumLeft,workAreaPhysical.Right-marginX-widthPhysical);
        var maximumTop=Math.Max(minimumTop,workAreaPhysical.Bottom-marginY-heightPhysical);
        var left=Math.Clamp(cursorPhysical.X-widthPhysical/2,minimumLeft,maximumLeft);
        var top=Math.Clamp(cursorPhysical.Y-heightPhysical/2,minimumTop,maximumTop);

        return new PopupPlacement(left,top,widthLogical,heightLogical,widthPhysical,heightPhysical);
    }

    private static bool PositiveFinite(double value) => double.IsFinite(value)&&value>0;
}
