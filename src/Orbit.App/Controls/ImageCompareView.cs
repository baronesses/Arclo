using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Orbit.App.Controls;

public enum ComparisonMode { SideBySide, Wipe }

/// <summary>Displays caller-owned bitmap snapshots. No file paths, decoding, clipboard or source mutation.</summary>
public sealed class ImageCompareView : UserControl
{
    public const double AspectRatioTolerance = 0.00001;
    public static readonly DependencyProperty BeforeSourceProperty=DependencyProperty.Register(nameof(BeforeSource),typeof(BitmapSource),typeof(ImageCompareView),new FrameworkPropertyMetadata(null,OnSourceChanged));
    public static readonly DependencyProperty AfterSourceProperty=DependencyProperty.Register(nameof(AfterSource),typeof(BitmapSource),typeof(ImageCompareView),new FrameworkPropertyMetadata(null,OnSourceChanged));
    public static readonly DependencyProperty SplitPositionProperty=DependencyProperty.Register(nameof(SplitPosition),typeof(double),typeof(ImageCompareView),new FrameworkPropertyMetadata(.5,FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,OnSplitChanged,CoerceSplit));
    public static readonly DependencyProperty ModeProperty=DependencyProperty.Register(nameof(Mode),typeof(ComparisonMode),typeof(ImageCompareView),new FrameworkPropertyMetadata(ComparisonMode.SideBySide,OnModeChanged,CoerceMode),value=>value is ComparisonMode.SideBySide or ComparisonMode.Wipe);
    private static readonly DependencyPropertyKey CanWipePropertyKey=DependencyProperty.RegisterReadOnly(nameof(CanWipe),typeof(bool),typeof(ImageCompareView),new PropertyMetadata(false));
    public static readonly DependencyProperty CanWipeProperty=CanWipePropertyKey.DependencyProperty;
    public BitmapSource? BeforeSource {get=>(BitmapSource?)GetValue(BeforeSourceProperty);set=>SetValue(BeforeSourceProperty,value);}
    public BitmapSource? AfterSource {get=>(BitmapSource?)GetValue(AfterSourceProperty);set=>SetValue(AfterSourceProperty,value);}
    public double SplitPosition {get=>(double)GetValue(SplitPositionProperty);set=>SetValue(SplitPositionProperty,value);}
    public ComparisonMode Mode {get=>(ComparisonMode)GetValue(ModeProperty);set=>SetValue(ModeProperty,value);}
    public bool CanWipe => (bool)GetValue(CanWipeProperty);

    private readonly CompareSurface _surface;
    private readonly ToggleButton _sideBySide;
    private readonly ToggleButton _wipe;
    private readonly Slider _split;
    private readonly TextBlock _hint;
    private readonly TextBlock _beforeLabel;
    private readonly TextBlock _afterLabel;
    private static readonly Brush Ink=Brush("#202630");

    public ImageCompareView()
    {
        MinWidth=180;MinHeight=200;Height=250;Focusable=false;HorizontalContentAlignment=HorizontalAlignment.Stretch;VerticalContentAlignment=VerticalAlignment.Stretch;
        var layout=new Grid{Background=Brushes.Transparent,UseLayoutRounding=true};
        layout.RowDefinitions.Add(new RowDefinition{Height=new GridLength(32)});
        layout.RowDefinitions.Add(new RowDefinition{Height=new GridLength(24)});
        layout.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
        layout.RowDefinitions.Add(new RowDefinition{Height=new GridLength(32)});
        var toolbar=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Top};
        _sideBySide=ModeButton(global::Orbit.Core.Localization.L.Get("Text_DE5C415B1D45"));_wipe=ModeButton(global::Orbit.Core.Localization.L.Get("Text_933D794EEE65"));toolbar.Children.Add(_sideBySide);toolbar.Children.Add(_wipe);layout.Children.Add(toolbar);
        _sideBySide.Click+=(_,_)=>{SetCurrentValue(ModeProperty,ComparisonMode.SideBySide);UpdateInterface();};
        _wipe.Click+=(_,_)=>{SetCurrentValue(ModeProperty,ComparisonMode.Wipe);UpdateInterface();};
        AutomationProperties.SetName(_sideBySide,global::Orbit.Core.Localization.L.Get("Text_D3667AF7A5A9"));AutomationProperties.SetName(_wipe,global::Orbit.Core.Localization.L.Get("Text_797E58BA38DF"));
        var labels=new Grid();labels.ColumnDefinitions.Add(new ColumnDefinition());labels.ColumnDefinitions.Add(new ColumnDefinition());
        _beforeLabel=Caption(global::Orbit.Core.Localization.L.Get("Text_44A2B1F65AE7"));_afterLabel=Caption(global::Orbit.Core.Localization.L.Get("Text_4997A5143D9F"));Grid.SetColumn(_afterLabel,1);labels.Children.Add(_beforeLabel);labels.Children.Add(_afterLabel);Grid.SetRow(labels,1);layout.Children.Add(labels);
        _surface=new CompareSurface(this);Grid.SetRow(_surface,2);layout.Children.Add(_surface);
        var footer=new Grid();Grid.SetRow(footer,3);
        _split=new Slider{Minimum=0,Maximum=1,SmallChange=.02,LargeChange=.1,Value=.5,TickFrequency=.1,IsMoveToPointEnabled=true,Height=26,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(5,0,5,0)};
        _split.SetBinding(RangeBase.ValueProperty,new Binding(nameof(SplitPosition)){Source=this,Mode=BindingMode.TwoWay});
        AutomationProperties.SetName(_split,global::Orbit.Core.Localization.L.Get("Text_1A4621E1ACB8"));AutomationProperties.SetHelpText(_split,global::Orbit.Core.Localization.L.Get("Text_7570669F6922"));
        _split.ToolTip=global::Orbit.Core.Localization.L.Get("Text_C353ACC88BE9");footer.Children.Add(_split);
        _hint=new TextBlock{FontFamily=new FontFamily("Segoe UI"),FontSize=11,Foreground=Brush("#606A76"),TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};Resource(_hint,TextBlock.ForegroundProperty,"GlassMutedText");footer.Children.Add(_hint);
        layout.Children.Add(footer);Content=layout;
        Unloaded+=(_,_)=>ReleaseOwnedCapture();
        IsVisibleChanged+=(_,_)=>{if(!IsVisible)ReleaseOwnedCapture();};
        IsEnabledChanged+=(_,_)=>{if(!IsEnabled)ReleaseOwnedCapture();};
        UpdateInterface();
    }

    private static ToggleButton ModeButton(string text)
    {
        var button=new ToggleButton{Content=text,MinWidth=72,Height=27,Padding=new Thickness(9,2,9,2),Margin=new Thickness(0,0,6,0),FontFamily=new FontFamily("Segoe UI"),FontSize=12,Foreground=Ink,Background=Brush("#FFFFFF"),BorderBrush=Brush("#C8CFD8"),BorderThickness=new Thickness(1),Cursor=Cursors.Hand};
        Resource(button,Control.ForegroundProperty,"TextBrush");Resource(button,Control.BackgroundProperty,"GlassControlFill");Resource(button,Control.BorderBrushProperty,"GlassEdge");
        var border=new FrameworkElementFactory(typeof(Border)){Name="CompareModeChrome"};border.SetValue(Border.CornerRadiusProperty,new CornerRadius(6));border.SetBinding(Border.BackgroundProperty,new Binding(nameof(Background)){RelativeSource=RelativeSource.TemplatedParent});border.SetBinding(Border.BorderBrushProperty,new Binding(nameof(BorderBrush)){RelativeSource=RelativeSource.TemplatedParent});border.SetBinding(Border.BorderThicknessProperty,new Binding(nameof(BorderThickness)){RelativeSource=RelativeSource.TemplatedParent});
        var presenter=new FrameworkElementFactory(typeof(ContentPresenter));presenter.SetValue(HorizontalAlignmentProperty,HorizontalAlignment.Center);presenter.SetValue(VerticalAlignmentProperty,VerticalAlignment.Center);border.AppendChild(presenter);
        var template=new ControlTemplate(typeof(ToggleButton)){VisualTree=border};
        var hover=new Trigger{Property=IsMouseOverProperty,Value=true};hover.Setters.Add(new Setter(Border.BackgroundProperty,Themed("GlassControlHover","#F0F3F6"),"CompareModeChrome"));hover.Setters.Add(new Setter(Control.ForegroundProperty,Themed("GlassSelectionText","#202630")));template.Triggers.Add(hover);
        var active=new Trigger{Property=ToggleButton.IsCheckedProperty,Value=true};active.Setters.Add(new Setter(Border.BackgroundProperty,Themed("GlassSelection","#D7E0EB"),"CompareModeChrome"));active.Setters.Add(new Setter(Border.BorderBrushProperty,Themed("GlassFocusEdge","#63758A"),"CompareModeChrome"));active.Setters.Add(new Setter(Control.ForegroundProperty,Themed("GlassSelectionText","#202630")));template.Triggers.Add(active);
        var focused=new Trigger{Property=IsKeyboardFocusedProperty,Value=true};focused.Setters.Add(new Setter(Border.BorderBrushProperty,Themed("GlassFocusEdge","#63758A"),"CompareModeChrome"));focused.Setters.Add(new Setter(Border.BorderThicknessProperty,new Thickness(2),"CompareModeChrome"));template.Triggers.Add(focused);
        var disabled=new Trigger{Property=IsEnabledProperty,Value=false};disabled.Setters.Add(new Setter(OpacityProperty,.45));template.Triggers.Add(disabled);button.Template=template;return button;
    }
    private static TextBlock Caption(string text){var caption=new TextBlock{Text=text,FontFamily=new FontFamily("Segoe UI"),FontSize=12,FontWeight=FontWeights.SemiBold,Foreground=Ink,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.NoWrap};Resource(caption,TextBlock.ForegroundProperty,"TextBrush");return caption;}
    private static Brush Brush(string hex){var brush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));brush.Freeze();return brush;}
    private static void Resource(FrameworkElement target,DependencyProperty property,string key){if(Application.Current?.TryFindResource(key) is Brush)target.SetResourceReference(property,key);}
    private static object Themed(string key,string fallback)=>Application.Current?.TryFindResource(key) is Brush?new DynamicResourceExtension(key):Brush(fallback);
    private static void OnSourceChanged(DependencyObject target,DependencyPropertyChangedEventArgs args)
    {
        var view=(ImageCompareView)target;view.ReleaseOwnedCapture();bool valid=Valid(view.BeforeSource)&&Valid(view.AfterSource)&&Math.Abs((double)view.BeforeSource!.PixelWidth/view.BeforeSource.PixelHeight-(double)view.AfterSource!.PixelWidth/view.AfterSource.PixelHeight)<=AspectRatioTolerance;
        view.SetValue(CanWipePropertyKey,valid);view.CoerceValue(ModeProperty);view.UpdateInterface();view._surface.InvalidateVisual();
    }
    private static object CoerceSplit(DependencyObject target,object value)=>double.IsNaN((double)value)?.5:Math.Clamp((double)value,0,1);
    private static object CoerceMode(DependencyObject target,object value)=>(ComparisonMode)value==ComparisonMode.Wipe&&!((ImageCompareView)target).CanWipe?ComparisonMode.SideBySide:value;
    private static void OnSplitChanged(DependencyObject target,DependencyPropertyChangedEventArgs args)=>((ImageCompareView)target)._surface.InvalidateVisual();
    private static void OnModeChanged(DependencyObject target,DependencyPropertyChangedEventArgs args){var view=(ImageCompareView)target;view.UpdateInterface();view._surface.InvalidateVisual();}
    private static bool Valid(BitmapSource? source)=>source is {PixelWidth:>0,PixelHeight:>0};
    private void UpdateInterface()
    {
        _sideBySide.IsChecked=Mode==ComparisonMode.SideBySide;_wipe.IsChecked=Mode==ComparisonMode.Wipe;_wipe.IsEnabled=CanWipe;
        bool wiping=Mode==ComparisonMode.Wipe&&CanWipe;_split.IsEnabled=wiping;_split.Visibility=wiping?Visibility.Visible:Visibility.Collapsed;_hint.Visibility=wiping?Visibility.Collapsed:Visibility.Visible;
        _hint.Text=!Valid(BeforeSource)||!Valid(AfterSource)?global::Orbit.Core.Localization.L.Get("Text_9E31173BDBBB"):CanWipe?global::Orbit.Core.Localization.L.Get("Text_120B94FB40B4"):global::Orbit.Core.Localization.L.Get("Text_893471D360F7");
        _wipe.ToolTip=CanWipe?global::Orbit.Core.Localization.L.Get("Text_0DC2EEEF201B"):global::Orbit.Core.Localization.L.Get("Text_C3233AC27501");
        _surface.Cursor=wiping?Cursors.SizeWE:Cursors.Arrow;
        if(!wiping)ReleaseOwnedCapture();
    }
    private void ReleaseOwnedCapture()
    {
        _surface.CancelDrag();
        // Native Slider thumbs can own capture too. Release only this view's visual descendants.
        if(Mouse.Captured is Visual captured&&(ReferenceEquals(captured,this)||IsAncestorOf(captured)))Mouse.Capture(null);
    }

    // Source-linked rendering tests use the exact image geometry without depending on theme pixels.
    internal Rect BeforeImageBounds=>_surface.BeforeBounds;
    internal Rect AfterImageBounds=>_surface.AfterBounds;
    internal Point SurfaceOrigin=>_surface.TranslatePoint(new Point(0,0),this);
    internal TextBlock BeforeCaption=>_beforeLabel;
    internal TextBlock AfterCaption=>_afterLabel;

    private sealed class CompareSurface : FrameworkElement
    {
        private readonly ImageCompareView _owner;
        private static readonly DependencyProperty PaneProperty=DependencyProperty.Register("Pane",typeof(Brush),typeof(CompareSurface),new FrameworkPropertyMetadata(Brush("#F3F5F7"),FrameworkPropertyMetadataOptions.AffectsRender));
        private static readonly DependencyProperty EdgeProperty=DependencyProperty.Register("Edge",typeof(Brush),typeof(CompareSurface),new FrameworkPropertyMetadata(Brush("#C8CFD8"),FrameworkPropertyMetadataOptions.AffectsRender));
        private static readonly DependencyProperty HintProperty=DependencyProperty.Register("Hint",typeof(Brush),typeof(CompareSurface),new FrameworkPropertyMetadata(Brush("#606A76"),FrameworkPropertyMetadataOptions.AffectsRender));
        private static readonly DependencyProperty FocusProperty=DependencyProperty.Register("Focus",typeof(Brush),typeof(CompareSurface),new FrameworkPropertyMetadata(Brush("#63758A"),FrameworkPropertyMetadataOptions.AffectsRender));
        private static readonly Brush Checker=Checkerboard();
        private bool _dragging;
        internal CompareSurface(ImageCompareView owner)
        {
            _owner=owner;ClipToBounds=true;Focusable=false;RenderOptions.SetBitmapScalingMode(this,BitmapScalingMode.HighQuality);
            Resource(this,PaneProperty,"GlassFieldFill");Resource(this,EdgeProperty,"GlassEdge");Resource(this,HintProperty,"GlassMutedText");Resource(this,FocusProperty,"GlassFocusEdge");
            Unloaded+=(_,_)=>CancelDrag();IsVisibleChanged+=(_,_)=>{if(!IsVisible)CancelDrag();};
            MouseLeftButtonDown+=(_,e)=>{if(_owner.Mode!=ComparisonMode.Wipe||!_owner.CanWipe||BeforeBounds.IsEmpty)return;_dragging=CaptureMouse();SetSplit(e.GetPosition(this));e.Handled=true;};
            MouseMove+=(_,e)=>{if(_dragging&&IsMouseCaptured)SetSplit(e.GetPosition(this));};
            MouseLeftButtonUp+=(_,e)=>{if(!_dragging)return;_dragging=false;ReleaseMouseCapture();e.Handled=true;};LostMouseCapture+=(_,_)=>_dragging=false;
        }
        internal Rect BeforeBounds=>Fit(_owner.BeforeSource,Frame(false));
        internal Rect AfterBounds=>_owner.Mode==ComparisonMode.Wipe?BeforeBounds:Fit(_owner.AfterSource,Frame(true));
        private Rect Frame(bool after)
        {
            if(ActualWidth<=0||ActualHeight<=0)return Rect.Empty;
            if(_owner.Mode==ComparisonMode.Wipe)return new Rect(0,0,ActualWidth,ActualHeight);
            double width=Math.Max(0,(ActualWidth-8)/2);return new Rect(after?width+8:0,0,width,ActualHeight);
        }
        private static Rect Fit(BitmapSource? source,Rect frame)
        {
            if(!Valid(source)||frame.IsEmpty||frame.Width<=4||frame.Height<=4)return Rect.Empty;
            double scale=Math.Min((frame.Width-4)/source!.PixelWidth,(frame.Height-4)/source.PixelHeight),width=source.PixelWidth*scale,height=source.PixelHeight*scale;
            return new Rect(frame.X+(frame.Width-width)/2,frame.Y+(frame.Height-height)/2,width,height);
        }
        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);if(ActualWidth<=0||ActualHeight<=0)return;
            if(_owner.Mode==ComparisonMode.Wipe&&_owner.CanWipe){
                var frame=Frame(false);DrawPane(drawing,frame);var bounds=BeforeBounds;if(bounds.IsEmpty)return;
                drawing.DrawRectangle(Checker,null,bounds);double split=bounds.Left+bounds.Width*_owner.SplitPosition;
                DrawClipped(drawing,_owner.BeforeSource!,bounds,new Rect(bounds.Left,bounds.Top,Math.Max(0,split-bounds.Left),bounds.Height));
                DrawClipped(drawing,_owner.AfterSource!,bounds,new Rect(split,bounds.Top,Math.Max(0,bounds.Right-split),bounds.Height));
                if(_owner.SplitPosition>0&&_owner.SplitPosition<1){drawing.DrawLine(new Pen(Brushes.White,1.5),new Point(split,bounds.Top),new Point(split,bounds.Bottom));drawing.DrawEllipse((Brush)GetValue(PaneProperty),new Pen((Brush)GetValue(FocusProperty),1.2),new Point(split,bounds.Top+bounds.Height/2),6,10);}
            }else{
                DrawSingle(drawing,_owner.BeforeSource,Frame(false),BeforeBounds,global::Orbit.Core.Localization.L.Get("Text_51EFE0F3AE6E"));DrawSingle(drawing,_owner.AfterSource,Frame(true),AfterBounds,global::Orbit.Core.Localization.L.Get("Text_7C1C1FF3AF73"));
            }
        }
        private void DrawPane(DrawingContext drawing,Rect frame){if(frame.IsEmpty||frame.Width<=1||frame.Height<=1)return;drawing.DrawRoundedRectangle((Brush)GetValue(PaneProperty),new Pen((Brush)GetValue(EdgeProperty),1),new Rect(frame.X+.5,frame.Y+.5,frame.Width-1,frame.Height-1),6,6);}
        private void DrawSingle(DrawingContext drawing,BitmapSource? source,Rect frame,Rect bounds,string empty)
        {
            DrawPane(drawing,frame);if(!bounds.IsEmpty){drawing.DrawRectangle(Checker,null,bounds);drawing.DrawImage(source,bounds);}
            else if(!frame.IsEmpty){var text=new FormattedText(empty,CultureInfo.GetCultureInfo("ru-RU"),FlowDirection.LeftToRight,new Typeface("Segoe UI"),11,(Brush)GetValue(HintProperty),1);drawing.DrawText(text,new Point(frame.Left+Math.Max(3,(frame.Width-text.Width)/2),frame.Top+Math.Max(0,(frame.Height-text.Height)/2)));}
        }
        private static void DrawClipped(DrawingContext drawing,BitmapSource source,Rect bounds,Rect clip){if(clip.Width<=0)return;drawing.PushClip(new RectangleGeometry(clip));drawing.DrawImage(source,bounds);drawing.Pop();}
        private void SetSplit(Point point){var bounds=BeforeBounds;if(!bounds.IsEmpty)_owner.SetCurrentValue(SplitPositionProperty,(point.X-bounds.Left)/bounds.Width);}
        internal void CancelDrag(){_dragging=false;if(IsMouseCaptured)ReleaseMouseCapture();}
        private static Brush Checkerboard()
        {
            var group=new DrawingGroup();using(var drawing=group.Open()){drawing.DrawRectangle(Brush("#DEE3E9"),null,new Rect(0,0,16,16));drawing.DrawRectangle(Brush("#F3F4F6"),null,new Rect(8,0,8,8));drawing.DrawRectangle(Brush("#F3F4F6"),null,new Rect(0,8,8,8));}group.Freeze();
            var brush=new DrawingBrush(group){TileMode=TileMode.Tile,ViewportUnits=BrushMappingMode.Absolute,Viewport=new Rect(0,0,16,16),ViewboxUnits=BrushMappingMode.Absolute,Viewbox=new Rect(0,0,16,16),Stretch=Stretch.None};brush.Freeze();return brush;
        }
    }
}
