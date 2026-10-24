using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Orbit.Core.Comparison;

namespace Orbit.App.Controls;

/// <summary>Selections use the displayed image's already EXIF-oriented original pixel dimensions.</summary>
public sealed class CropPreview : FrameworkElement
{
    private static readonly DependencyProperty SurfaceBrushProperty = DependencyProperty.Register("SurfaceBrush", typeof(Brush), typeof(CropPreview), new FrameworkPropertyMetadata(Ink("#F3F5F7"), FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty SelectionEdgeProperty = DependencyProperty.Register("SelectionEdge", typeof(Brush), typeof(CropPreview), new FrameworkPropertyMetadata(Ink("#63758A"), FrameworkPropertyMetadataOptions.AffectsRender));
    private ImageSource? _source;
    private int _pixelWidth, _pixelHeight;
    private double? _aspectRatio;
    private CropPoint? _start;
    private Int32Rect _beforeDrag;
    public ImageSource? Source { get => _source;set { if (ReferenceEquals(_source, value)) return;CancelDrag();_source = value;InvalidateVisual(); } }
    public int PixelWidth { get => _pixelWidth;set { if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));if (_pixelWidth == value) return;CancelDrag();_pixelWidth = value;InvalidateVisual(); } }
    public int PixelHeight { get => _pixelHeight;set { if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));if (_pixelHeight == value) return;CancelDrag();_pixelHeight = value;InvalidateVisual(); } }
    public Int32Rect Selection { get; private set; }
    public Int32Rect CurrentSelection => Selection;
    public event Action<Int32Rect>? SelectionChanged;
    public double? AspectRatio
    {
        get => _aspectRatio;
        set
        {
            if (!CropSelectionMath.IsValidAspectRatio(value)) throw new ArgumentOutOfRangeException(nameof(value), global::Orbit.Core.Localization.L.Get("Text_5DB9B188DF40"));
            if (_aspectRatio == value) return;CancelDrag();_aspectRatio = value;
            if (PixelWidth > 0 && PixelHeight > 0) Publish(CropSelectionMath.Centered(PixelWidth, PixelHeight, ToCore(Selection), value));
        }
    }

    public CropPreview()
    {
        Height = 175;Cursor = Cursors.Cross;ClipToBounds = true;Focusable = true;
        if (Application.Current?.TryFindResource("GlassFieldFill") is Brush) SetResourceReference(SurfaceBrushProperty, "GlassFieldFill");
        if (Application.Current?.TryFindResource("GlassFocusEdge") is Brush) SetResourceReference(SelectionEdgeProperty, "GlassFocusEdge");
        MouseLeftButtonDown += Down;MouseMove += Move;MouseLeftButtonUp += Up;
        LostMouseCapture += LostCapture;Unloaded += OnUnloaded;PreviewKeyDown += Key;
    }

    public bool TrySetSelection(Int32Rect rectangle, out string? reason)
    {
        CancelDrag();
        if (!CropSelectionMath.TryValidate(PixelWidth, PixelHeight, ToCore(rectangle), AspectRatio, out reason)) return false;
        Publish(ToCore(rectangle));return true;
    }
    public void Reset()
    {
        CancelDrag();
        Publish(PixelWidth > 0 && PixelHeight > 0 ? CropSelectionMath.Centered(PixelWidth, PixelHeight, ratio: AspectRatio) : default);
    }

    private Rect Display
    {
        get { var fit = CropSelectionMath.FitDisplay(PixelWidth, PixelHeight, ActualWidth, ActualHeight);return fit.Width <= 0 || fit.Height <= 0 ? Rect.Empty : new Rect(fit.X, fit.Y, fit.Width, fit.Height); }
    }
    protected override void OnRender(DrawingContext drawing)
    {
        drawing.DrawRectangle((Brush)GetValue(SurfaceBrushProperty), null, new Rect(0, 0, ActualWidth, ActualHeight));
        var display = Display;if (display.IsEmpty || Source is null) return;drawing.DrawImage(Source, display);
        if (!CropSelectionMath.TryValidate(PixelWidth, PixelHeight, ToCore(Selection), null, out _)) return;
        var scale = display.Width / PixelWidth;var box = new Rect(display.X + Selection.X * scale, display.Y + Selection.Y * scale, Selection.Width * scale, Selection.Height * scale);
        var mask = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(display), new RectangleGeometry(box));
        drawing.DrawGeometry(new SolidColorBrush(Color.FromArgb(155, 32, 38, 48)), null, mask);
        drawing.DrawRectangle(null, new Pen(Brushes.White, 3), box);drawing.DrawRectangle(null, new Pen((Brush)GetValue(SelectionEdgeProperty), 1.5), box);
        if (box.Width <= 30 || box.Height <= 30) return;
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)), .7);
        for (var index = 1; index < 3; index++)
        {
            drawing.DrawLine(pen, new Point(box.X + box.Width * index / 3, box.Y), new Point(box.X + box.Width * index / 3, box.Bottom));
            drawing.DrawLine(pen, new Point(box.X, box.Y + box.Height * index / 3), new Point(box.Right, box.Y + box.Height * index / 3));
        }
    }
    private CropPoint ImagePoint(Point point)
    {
        var display = Display;return CropSelectionMath.ToImagePoint(PixelWidth, PixelHeight,
            new CropDisplayRect(display.X, display.Y, display.Width, display.Height), new CropPoint(point.X, point.Y));
    }
    private void Down(object sender, MouseButtonEventArgs args)
    {
        if (Display.IsEmpty || Source is null || Mouse.Captured is not null && !ReferenceEquals(Mouse.Captured, this)) return;
        BeginDrag(ImagePoint(args.GetPosition(this)));Focus();if (!CaptureMouse()) { CancelDrag();return; }args.Handled = true;
    }
    private void Move(object sender, MouseEventArgs args)
    {
        if (_start is null) return;if (args.LeftButton != MouseButtonState.Pressed) { CancelDrag();return; }
        UpdateDrag(ImagePoint(args.GetPosition(this)));args.Handled = true;
    }
    private void Up(object sender, MouseButtonEventArgs args)
    {
        if (_start is null) return;UpdateDrag(ImagePoint(args.GetPosition(this)));CancelDrag();args.Handled = true;
    }
    private void Key(object sender, KeyEventArgs args) { if (args.Key != System.Windows.Input.Key.Escape || _start is null) return;CancelDrag(restore: true);args.Handled = true; }
    private void LostCapture(object sender, MouseEventArgs args) => CancelDrag();
    private void OnUnloaded(object sender, RoutedEventArgs args) => CancelDrag();
    internal bool DragInProgress => _start is not null;
    internal void BeginDrag(CropPoint point) { CancelDrag();_beforeDrag = Selection;_start = point; }
    internal void UpdateDrag(CropPoint point) { if (_start is { } start) Publish(CropSelectionMath.FromDrag(PixelWidth, PixelHeight, start, point, AspectRatio)); }
    internal void CancelDrag(bool restore = false)
    {
        var hadDrag = _start is not null;_start = null;if (IsMouseCaptured) ReleaseMouseCapture();
        if (restore && hadDrag) Publish(ToCore(_beforeDrag));
    }
    private void Publish(PixelCropRect rectangle) { Selection = new Int32Rect(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);InvalidateVisual();SelectionChanged?.Invoke(Selection); }
    private static PixelCropRect ToCore(Int32Rect rectangle) => new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
    private static Brush Ink(string hex) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));brush.Freeze();return brush; }
}
