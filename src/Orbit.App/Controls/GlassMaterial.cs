using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Orbit.App.Appearance;

namespace Orbit.App.Controls;

/// <summary>Vector surfaces with an optional private backdrop frame supplied by the owning window.</summary>
public static class GlassMaterial
{
    public static readonly DependencyProperty ReducedTransparencyProperty=DependencyProperty.RegisterAttached(
        "ReducedTransparency",typeof(bool),typeof(GlassMaterial),new FrameworkPropertyMetadata(false,
            FrameworkPropertyMetadataOptions.Inherits|FrameworkPropertyMetadataOptions.AffectsRender));

    public static void SetReducedTransparency(DependencyObject target,bool value)=>target.SetValue(ReducedTransparencyProperty,value);
    public static bool GetReducedTransparency(DependencyObject target)=>(bool)target.GetValue(ReducedTransparencyProperty);

    public static readonly DependencyProperty ThemeProperty=DependencyProperty.RegisterAttached(
        "Theme",typeof(OrbitTheme),typeof(GlassMaterial),new FrameworkPropertyMetadata(OrbitTheme.Light,
            FrameworkPropertyMetadataOptions.Inherits|FrameworkPropertyMetadataOptions.AffectsRender),value=>value is OrbitTheme theme&&Enum.IsDefined(theme));
    public static void SetTheme(DependencyObject target,OrbitTheme value)=>target.SetValue(ThemeProperty,value);
    public static OrbitTheme GetTheme(DependencyObject target)=>(OrbitTheme)target.GetValue(ThemeProperty);
    public static readonly DependencyProperty BackdropFrameProperty=DependencyProperty.RegisterAttached(
        "BackdropFrame",typeof(BitmapSource),typeof(GlassMaterial),new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.Inherits|FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BackdropBoundsProperty=DependencyProperty.RegisterAttached(
        "BackdropBounds",typeof(Rect),typeof(GlassMaterial),new FrameworkPropertyMetadata(Rect.Empty,
            FrameworkPropertyMetadataOptions.Inherits|FrameworkPropertyMetadataOptions.AffectsRender));
    public static void SetBackdropFrame(DependencyObject target,BitmapSource? value)=>target.SetValue(BackdropFrameProperty,value);
    public static BitmapSource? GetBackdropFrame(DependencyObject target)=>(BitmapSource?)target.GetValue(BackdropFrameProperty);
    public static void SetBackdropBounds(DependencyObject target,Rect value)=>target.SetValue(BackdropBoundsProperty,value);
    public static Rect GetBackdropBounds(DependencyObject target)=>(Rect)target.GetValue(BackdropBoundsProperty);

    private static readonly Brush NormalSurface=Solid(Color.FromArgb(230,249,250,251));
    private static readonly Brush HoverSurface=Solid(Color.FromArgb(242,229,236,242));
    private static readonly Brush ReducedSurface=Solid(Color.FromRgb(247,248,249));
    private static readonly Brush ReducedHoverSurface=Solid(Color.FromRgb(229,235,241));
    private static readonly Pen Edge=FrozenPen(Color.FromArgb(205,161,171,181),1.2);
    private static readonly Pen HoverEdge=FrozenPen(Color.FromArgb(225,115,135,155),1.4);
    private static readonly Brush CrimsonSurface=Gradient(
        (0,Color.FromArgb(199,15,18,27)),(.48,Color.FromArgb(186,2,6,14)),(1,Color.FromArgb(195,48,6,23)));
    private static readonly Brush CrimsonHoverSurface=Gradient(
        (0,Color.FromArgb(199,27,28,37)),(.46,Color.FromArgb(178,13,9,21)),(1,Color.FromArgb(200,90,3,31)));
    private static readonly Brush CrimsonReducedSurface=Gradient(
        (0,Color.FromRgb(15,19,28)),(.5,Color.FromRgb(7,11,19)),(1,Color.FromRgb(35,10,23)));
    private static readonly Brush CrimsonReducedHoverSurface=Gradient(
        (0,Color.FromRgb(29,29,39)),(.5,Color.FromRgb(15,12,24)),(1,Color.FromRgb(68,9,30)));
    private static readonly Pen CrimsonEdge=GradientPen(1.15,(0,Color.FromArgb(207,255,241,238)),(.34,Color.FromArgb(107,227,210,216)),(1,Color.FromArgb(97,220,125,151)));
    private static readonly Pen CrimsonHoverEdge=GradientPen(1.65,(0,Color.FromArgb(246,255,247,244)),(.4,Color.FromArgb(170,242,224,231)),(1,Color.FromArgb(190,222,62,108)));
    private static readonly Pen CrimsonInnerEdge=GradientPen(.7,(0,Color.FromArgb(103,255,246,241)),(.44,Color.FromArgb(18,246,229,235)),(1,Color.FromArgb(53,197,3,55)));
    private static readonly Brush TopReflection=Gradient((0,Color.FromArgb(35,255,247,244)),(.32,Color.FromArgb(4,255,247,244)),(.7,Colors.Transparent),(1,Color.FromArgb(8,197,3,55)));
    private static readonly Brush PearlReflection=Glow(Color.FromArgb(33,255,248,243));
    private static readonly Brush CrimsonReflection=Glow(Color.FromArgb(30,197,3,55));
    private static readonly Brush PointerReflection=Glow(Color.FromArgb(43,255,243,238));

    public static Geometry CreateInnerEdge(Geometry shape,double inset=2.4)
    {
        var boundary=shape.GetWidenedPathGeometry(new Pen(Brushes.Black,inset*2));
        var inside=Geometry.Combine(shape,boundary,GeometryCombineMode.Exclude,null);
        inside.Freeze();return inside;
    }

    public static void Draw(DrawingContext dc,Geometry shape,Geometry innerEdge,bool highlighted,bool reducedTransparency,Point? pointer=null,FrameworkElement? owner=null)
    {
        if(SystemParameters.HighContrast) {
            dc.DrawGeometry(SystemColors.WindowBrush,new Pen(SystemColors.WindowTextBrush,highlighted?2:1.3),shape);
            return;
        }
        var bounds=shape.Bounds;
        if(bounds.IsEmpty||bounds.Width<=0||bounds.Height<=0)return;
        bool crimson=owner is not null&&GetTheme(owner)==OrbitTheme.LiquidGlassCrimson;
        if(!reducedTransparency&&owner is not null)DrawBackdrop(dc,shape,owner);
        if(!crimson){
            dc.DrawGeometry(reducedTransparency?(highlighted?ReducedHoverSurface:ReducedSurface):(highlighted?HoverSurface:NormalSurface),highlighted?HoverEdge:Edge,shape);
            return;
        }
        dc.DrawGeometry(reducedTransparency?(highlighted?CrimsonReducedHoverSurface:CrimsonReducedSurface):(highlighted?CrimsonHoverSurface:CrimsonSurface),highlighted?CrimsonHoverEdge:CrimsonEdge,shape);
        dc.PushClip(shape);
        dc.DrawRectangle(TopReflection,null,bounds);
        dc.DrawEllipse(PearlReflection,null,new Point(bounds.Left+bounds.Width*.23,bounds.Top+bounds.Height*.08),bounds.Width*.65,bounds.Height*.34);
        if(!reducedTransparency)dc.DrawEllipse(CrimsonReflection,null,new Point(bounds.Right-bounds.Width*.12,bounds.Bottom-bounds.Height*.10),bounds.Width*.60,bounds.Height*.42);
        if(highlighted&&!reducedTransparency&&SystemParameters.ClientAreaAnimation&&pointer is{} location)
            dc.DrawEllipse(PointerReflection,null,location,Math.Max(24,bounds.Width*.35),Math.Max(18,bounds.Height*.35));
        dc.Pop();
        dc.DrawGeometry(null,CrimsonInnerEdge,innerEdge);
    }

    private static void DrawBackdrop(DrawingContext dc,Geometry shape,FrameworkElement owner)
    {
        var frame=GetBackdropFrame(owner);var bounds=GetBackdropBounds(owner);
        if(frame is null||bounds.IsEmpty||bounds.Width<=0||bounds.Height<=0)return;
        DependencyObject? parent=owner;
        while(parent is not null&&parent is not RadialMenu)parent=VisualTreeHelper.GetParent(parent);
        if(parent is not RadialMenu radial)return;
        var origin=owner.TranslatePoint(new Point(),radial);
        dc.PushClip(shape);
        dc.DrawImage(frame,new Rect(bounds.X-origin.X,bounds.Y-origin.Y,bounds.Width,bounds.Height));
        dc.Pop();
    }

    private static Brush Solid(Color color)
    {
        var brush=new SolidColorBrush(color);
        brush.Freeze();return brush;
    }
    private static Pen FrozenPen(Color color,double width)
    {
        var pen=new Pen(new SolidColorBrush(color),width);pen.Freeze();return pen;
    }
    private static Brush Gradient(params (double Offset,Color Color)[] stops)
    {
        var brush=new LinearGradientBrush{StartPoint=new Point(.18,0),EndPoint=new Point(.78,1)};
        foreach(var stop in stops)brush.GradientStops.Add(new GradientStop(stop.Color,stop.Offset));
        brush.Freeze();return brush;
    }
    private static Pen GradientPen(double width,params (double Offset,Color Color)[] stops)
    {var pen=new Pen(Gradient(stops),width);pen.Freeze();return pen;}
    private static Brush Glow(Color color)
    {
        var brush=new RadialGradientBrush(color,Color.FromArgb(0,color.R,color.G,color.B)){RadiusX=.5,RadiusY=.5};
        brush.Freeze();return brush;
    }
}

/// <summary>The center uses the same material without raster assets or a second interactive surface.</summary>
public sealed class GlassSurface : FrameworkElement
{
    public static readonly DependencyProperty IsHighlightedProperty=DependencyProperty.Register(
        nameof(IsHighlighted),typeof(bool),typeof(GlassSurface),new FrameworkPropertyMetadata(false,FrameworkPropertyMetadataOptions.AffectsRender));
    public bool IsHighlighted{get=>(bool)GetValue(IsHighlightedProperty);set=>SetValue(IsHighlightedProperty,value);}
    private Geometry? _shape;
    private Geometry? _innerEdge;
    private Size _drawnSize;
    protected override HitTestResult? HitTestCore(PointHitTestParameters p)
    {
        if(ActualWidth<=0||ActualHeight<=0)return null;
        double dx=(p.HitPoint.X-ActualWidth/2)/(ActualWidth/2);
        double dy=(p.HitPoint.Y-ActualHeight/2)/(ActualHeight/2);
        return dx*dx+dy*dy<=1?new PointHitTestResult(this,p.HitPoint):null;
    }
    protected override void OnRender(DrawingContext dc)
    {
        if(ActualWidth<=0||ActualHeight<=0)return;
        if(_shape==null||_drawnSize!=RenderSize) {
            _drawnSize=RenderSize;
            var ellipse=new EllipseGeometry(new Point(ActualWidth/2,ActualHeight/2),Math.Max(0,ActualWidth/2-.9),Math.Max(0,ActualHeight/2-.9));
            ellipse.Freeze();_shape=ellipse;_innerEdge=GlassMaterial.CreateInnerEdge(ellipse);
        }
        GlassMaterial.Draw(dc,_shape,_innerEdge!,IsHighlighted,GlassMaterial.GetReducedTransparency(this),owner:this);
    }
}
