using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Orbit.App.Appearance;
using Orbit.Core;

namespace Orbit.App.Controls;

public enum RadialCommand { None, Convert, SaveAs, Copy, Trim, More, Cancel, Retry }

public sealed record RadialAction(ImageAction Action,string Label,string Description)
{
    public RadialCommand Command { get; init; }
}

public sealed class RadialMenu : Canvas
{
    public event Action<ImageAction>? ActionSelected;
    public event Action<RadialCommand>? CommandSelected;
    public event Action<ImageAction?>? ActionHovered;
    public event Action? CenterSelected;
    public event Action? SettingsSelected;
    public Button CenterCard { get; }
    public Button SettingsButton { get; }
    public Image Preview { get; }
    public TextBlock FileName { get; }
    public TextBlock CenterHint { get; }
    public TextBlock Metadata { get; }
    private SectorButton[] _sectors=Array.Empty<SectorButton>();
    private readonly List<UIElement> _ticks=new();
    private readonly System.Windows.Shapes.Ellipse _guide;
    private readonly RadialProgressRing _progressRing=new();
    private bool _available;
    public int ActionCount => _sectors.Length;
    public bool ProgressVisible => _progressRing.Visibility==Visibility.Visible;
    public double? ProgressFraction => _progressRing.Fraction;
    public RadialMenu()
    {
        Width = Height = 760;
        _guide = new System.Windows.Shapes.Ellipse { Width=690,Height=690,Stroke=new SolidColorBrush(Color.FromArgb(55,147,158,169)),StrokeThickness=.7,IsHitTestVisible=false };
        SetLeft(_guide,35); SetTop(_guide,35); Children.Add(_guide);
        SetZIndex(_progressRing,1);Children.Add(_progressRing);
        SetActions(new[] {
            new RadialAction(ImageAction.Jpg,"JPG",global::Orbit.Core.Localization.L.Get("Text_0D0B26764D28")),
            new RadialAction(ImageAction.Png,"PNG",global::Orbit.Core.Localization.L.Get("Text_3F5FA95D4B36")),
            new RadialAction(ImageAction.Crop,global::Orbit.Core.Localization.L.Get("Text_1FE990F27562"),global::Orbit.Core.Localization.L.Get("Text_AEE22D5289F8")),
            new RadialAction(ImageAction.Compress,global::Orbit.Core.Localization.L.Get("Text_A3A634E3ED93"),global::Orbit.Core.Localization.L.Get("Text_C3B1198D92FB")),
            new RadialAction(ImageAction.Resize,global::Orbit.Core.Localization.L.Get("Text_572095D4A787"),global::Orbit.Core.Localization.L.Get("Text_6346D7B31F1F")),
            new RadialAction(ImageAction.MoreTools,global::Orbit.Core.Localization.L.Get("Text_2071FB990504"),global::Orbit.Core.Localization.L.Get("Text_E5152A08207C")) });
        Preview=new Image { Width=76,Height=74,Stretch=Stretch.Uniform,Margin=new Thickness(0,0,0,6),IsHitTestVisible=false };
        FileName=new TextBlock { Text=global::Orbit.Core.Localization.L.Get("Text_62C9C02CA6FC"),FontSize=24,FontWeight=FontWeights.SemiBold,Foreground=Brush("#29323B"),TextAlignment=TextAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis,MaxWidth=202,IsHitTestVisible=false };
        CenterHint=new TextBlock { Text=global::Orbit.Core.Localization.L.Get("Text_3DDFE21A9EE0"),FontSize=20,Foreground=Brush("#4F5D6B"),Margin=new Thickness(0,6,0,0),HorizontalAlignment=HorizontalAlignment.Center,MaxWidth=180,TextTrimming=TextTrimming.CharacterEllipsis,IsHitTestVisible=false };
        Metadata=new TextBlock { Text=global::Orbit.Core.Localization.L.Get("Text_0A521629AD8B"),FontSize=16,Foreground=Brush("#647180"),Margin=new Thickness(0,7,0,0),HorizontalAlignment=HorizontalAlignment.Center,MaxWidth=146,TextTrimming=TextTrimming.CharacterEllipsis,IsHitTestVisible=false };
        // Reserve a footer for the settings button while keeping the filename readable at 380 DIP.
        var centerStack=new StackPanel { VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,0,0,44) };
        centerStack.Children.Add(Preview); centerStack.Children.Add(FileName);centerStack.Children.Add(CenterHint);centerStack.Children.Add(Metadata);
        CenterCard=new Button { Width=234,Height=234,Background=Brushes.Transparent,Content=centerStack,Padding=new Thickness(0),Cursor=Cursors.Hand,FocusVisualStyle=null,Template=CreateCenterTemplate(),ToolTip=global::Orbit.Core.Localization.L.Get("Text_6C7CBFDF8314"),TabIndex=100 };
        var centerClip=new EllipseGeometry(new Point(117,117),117,117);centerClip.Freeze();CenterCard.Clip=centerClip;
        AutomationProperties.SetName(CenterCard,global::Orbit.Core.Localization.L.Get("Text_6C7CBFDF8314"));
        AutomationProperties.SetHelpText(CenterCard,global::Orbit.Core.Localization.L.Get("Text_E5D987185D75"));
        SetLeft(CenterCard,263);SetTop(CenterCard,263);Children.Add(CenterCard);
        SetZIndex(CenterCard,2);
        CenterCard.Click += (_,_)=>CenterSelected?.Invoke();
        var settingsIcon=new System.Windows.Shapes.Path { Data=CreateSettingsIcon(),Width=34,Height=34,Stretch=Stretch.Uniform,IsHitTestVisible=false };
        settingsIcon.SetBinding(System.Windows.Shapes.Shape.FillProperty,new Binding(nameof(Control.Foreground)){RelativeSource=new RelativeSource(RelativeSourceMode.FindAncestor,typeof(Button),1)});
        SettingsButton=new Button { Width=56,Height=56,Background=Brushes.Transparent,Foreground=Brush("#3C4854"),Content=settingsIcon,Padding=new Thickness(0),Cursor=Cursors.Hand,FocusVisualStyle=null,Template=CreateCenterTemplate(),ToolTip=global::Orbit.Core.Localization.L.Get("Text_6E54C9C64B85"),TabIndex=101 };
        var settingsClip=new EllipseGeometry(new Point(28,28),28,28);settingsClip.Freeze();SettingsButton.Clip=settingsClip;
        AutomationProperties.SetName(SettingsButton,global::Orbit.Core.Localization.L.Get("Text_6E54C9C64B85"));
        AutomationProperties.SetHelpText(SettingsButton,global::Orbit.Core.Localization.L.Get("Text_4E148D6348D7"));
        SetLeft(SettingsButton,352);SetTop(SettingsButton,448);SetZIndex(SettingsButton,3);Children.Add(SettingsButton);
        SettingsButton.Click+=(_,_)=>SettingsSelected?.Invoke();
        // A vector document remains sharp at every DPI; a real thumbnail replaces it on selection.
        var drawing=new DrawingGroup();
        using(var d=drawing.Open()) {
            d.DrawGeometry(Brush("#DEE5EB"),new Pen(Brush("#9CAAB7"),1.3),Geometry.Parse("M 8,3 L 57,3 78,24 78,91 Q 78,98 71,98 L 8,98 Q 2,98 2,91 L 2,10 Q 2,3 8,3 Z"));
            d.DrawGeometry(Brush("#A8BAC9"),null,Geometry.Parse("M 3,74 L 23,48 45,70 60,59 78,76 78,96 3,96 Z"));
            d.DrawGeometry(Brush("#C1CED9"),null,Geometry.Parse("M 3,82 L 22,66 35,78 54,67 78,86 78,96 3,96 Z"));
            d.DrawEllipse(Brush("#F5F7F9"),null,new Point(30,30),3,3);
            d.DrawGeometry(Brush("#ECF0F3"),null,Geometry.Parse("M 57,3 L 57,24 78,24 Z"));
        }
        Preview.Source=new DrawingImage(drawing);
    }
    private static Geometry CreateSettingsIcon() {
        var teeth=new StreamGeometry();
        using(var context=teeth.Open()) {
            for(int i=0;i<32;i++) {
                double angle=(i*360d/32-90)*Math.PI/180;
                double radius=i%4 is 1 or 2?16:12.5;
                var point=new Point(17+Math.Cos(angle)*radius,17+Math.Sin(angle)*radius);
                if(i==0)context.BeginFigure(point,true,true);else context.LineTo(point,true,false);
            }
        }
        teeth.Freeze();
        var geometry=new GeometryGroup{FillRule=FillRule.EvenOdd};
        geometry.Children.Add(teeth);geometry.Children.Add(new EllipseGeometry(new Point(17,17),5.5,5.5));geometry.Freeze();return geometry;
    }
    private static ControlTemplate CreateCenterTemplate() {
        var root=new FrameworkElementFactory(typeof(Grid));
        var glass=new FrameworkElementFactory(typeof(GlassSurface),"Glass");
        root.AppendChild(glass);
        var parent=new RelativeSource(RelativeSourceMode.TemplatedParent);
        var content=new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetBinding(ContentPresenter.ContentProperty,new Binding(nameof(ContentControl.Content)){RelativeSource=parent});
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);
        content.SetValue(IsHitTestVisibleProperty,false);
        root.AppendChild(content);
        var template=new ControlTemplate(typeof(Button)){VisualTree=root};
        var hover=new Trigger{Property=IsMouseOverProperty,Value=true};
        hover.Setters.Add(new Setter(GlassSurface.IsHighlightedProperty,true,"Glass"));
        template.Triggers.Add(hover);
        var focus=new Trigger{Property=IsKeyboardFocusedProperty,Value=true};
        focus.Setters.Add(new Setter(GlassSurface.IsHighlightedProperty,true,"Glass"));
        template.Triggers.Add(focus);
        return template;
    }
    /// <summary>Uses opaque glass when transparency is reduced; inherited material state also reaches future sectors.</summary>
    public void SetGlassAppearance(bool reducedTransparency) {
        VerifyAccess();
        GlassMaterial.SetReducedTransparency(this,reducedTransparency);
        UpdatePalette();
        foreach(var sector in _sectors)sector.InvalidateVisual();
        CenterCard.ApplyTemplate();
        if(CenterCard.Template.FindName("Glass",CenterCard) is GlassSurface surface)surface.InvalidateVisual();
        SettingsButton.ApplyTemplate();
        if(SettingsButton.Template.FindName("Glass",SettingsButton) is GlassSurface settingsSurface)settingsSurface.InvalidateVisual();
    }
    /// <summary>Switches material and ink together without replacing actions or the selected file preview.</summary>
    public void SetTheme(OrbitTheme theme) {
        VerifyAccess();
        if(!Enum.IsDefined(theme))throw new ArgumentOutOfRangeException(nameof(theme));
        GlassMaterial.SetTheme(this,theme);UpdatePalette();
        foreach(var sector in _sectors)sector.InvalidateVisual();
    }
    /// <summary>Shows a ring inside the clear hub gap. Finite fractions are clamped; unknown values use an indeterminate arc.</summary>
    public void SetProgress(bool visible,double? fraction=null,string? description=null) {
        VerifyAccess();
        double? normalized=visible&&fraction is{} value&&double.IsFinite(value)?Math.Clamp(value,0,1):null;
        _progressRing.SetProgress(visible,normalized,description);
        AutomationProperties.SetItemStatus(CenterCard,visible?_progressRing.Description:string.Empty);
    }
    /// <summary>Places a frozen, already blurred frame in radial-local coordinates; only glass surfaces draw it.</summary>
    public void SetBackdropFrame(BitmapSource? frame,Rect imageBoundsInRadial) {
        VerifyAccess();
        if(frame is null) {
            GlassMaterial.SetBackdropFrame(this,null);GlassMaterial.SetBackdropBounds(this,Rect.Empty);return;
        }
        if(imageBoundsInRadial.IsEmpty||!double.IsFinite(imageBoundsInRadial.X)||!double.IsFinite(imageBoundsInRadial.Y)||!double.IsFinite(imageBoundsInRadial.Width)||!double.IsFinite(imageBoundsInRadial.Height)||imageBoundsInRadial.Width<=0||imageBoundsInRadial.Height<=0)
            throw new ArgumentOutOfRangeException(nameof(imageBoundsInRadial));
        if(!frame.IsFrozen) {frame=frame.CloneCurrentValue();frame.Freeze();}
        GlassMaterial.SetBackdropBounds(this,imageBoundsInRadial);GlassMaterial.SetBackdropFrame(this,frame);
    }
    private void UpdatePalette() {
        bool crimson=GlassMaterial.GetTheme(this)==OrbitTheme.LiquidGlassCrimson;
        FileName.Foreground=SystemParameters.HighContrast?SystemColors.WindowTextBrush:Brush(crimson?"#F3EFF2":"#29323B");
        CenterHint.Foreground=SystemParameters.HighContrast?SystemColors.WindowTextBrush:Brush(crimson?"#DBD2DA":"#4F5D6B");
        Metadata.Foreground=SystemParameters.HighContrast?SystemColors.WindowTextBrush:Brush(crimson?"#C4BAC6":"#647180");
        SettingsButton.Foreground=SystemParameters.HighContrast?SystemColors.WindowTextBrush:Brush(crimson?"#EEE7EC":"#3C4854");
        _guide.Stroke=SystemParameters.HighContrast?SystemColors.WindowTextBrush:new SolidColorBrush(crimson?Color.FromArgb(47,237,210,218):Color.FromArgb(55,147,158,169));
        foreach(var tick in _ticks)if(tick is System.Windows.Shapes.Line line)line.Stroke=TickBrush();
        _progressRing.InvalidateVisual();
    }
    private Brush TickBrush()=>SystemParameters.HighContrast?SystemColors.WindowTextBrush:new SolidColorBrush(GlassMaterial.GetTheme(this)==OrbitTheme.LiquidGlassCrimson?Color.FromArgb(130,237,221,227):Color.FromArgb(150,137,148,160));
    private SectorButton Make(RadialAction action,double angle,double sweep) {
        var b=new SectorButton(action.Action,angle,action.Label,sweep,action.Command) { ToolTip=action.Description,HasFile=_available };
        AutomationProperties.SetHelpText(b,action.Description);
        b.Click+=(_,_)=>{if(action.Command!=RadialCommand.None)CommandSelected?.Invoke(action.Command);else ActionSelected?.Invoke(action.Action);};
        b.MouseEnter+=(_,_)=>ActionHovered?.Invoke(action.Command==RadialCommand.None?action.Action:null);
        b.MouseLeave+=(_,_)=>ActionHovered?.Invoke(null);
        Children.Add(b);return b;
    }
    /// <summary>Replaces the visible menu with three to six context-specific actions.</summary>
    public void SetActions(IReadOnlyList<RadialAction> actions) {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(actions);
        if(actions.Count is <3 or >6)throw new ArgumentOutOfRangeException(nameof(actions),"Radial menus contain three to six actions.");
        var requested=new RadialAction[actions.Count];
        var seen=new HashSet<ImageAction>();
        var seenCommands=new HashSet<RadialCommand>();
        for(int i=0;i<requested.Length;i++) {
            var action=actions[i]??throw new ArgumentException("An action cannot be null.",nameof(actions));
            if(!Enum.IsDefined(action.Action)||!Enum.IsDefined(action.Command)
                ||(action.Command==RadialCommand.None?!seen.Add(action.Action):!seenCommands.Add(action.Command)))
                throw new ArgumentException("Actions and commands must be distinct, defined operations.",nameof(actions));
            if(string.IsNullOrWhiteSpace(action.Label))throw new ArgumentException("An action needs a visible label.",nameof(actions));
            requested[i]=action;
        }
        bool restoreFocus=false;
        foreach(var sector in _sectors){restoreFocus|=sector.IsKeyboardFocusWithin;Children.Remove(sector);}
        foreach(var tick in _ticks)Children.Remove(tick);
        _ticks.Clear();
        var sectors=new SectorButton[requested.Length];
        double step=360d/requested.Length;
        double first=requested.Length==6?-120:-90;
        for(int i=0;i<requested.Length;i++) {
            double angle=first+i*step;
            sectors[i]=Make(requested[i],angle,step-8);
            sectors[i].TabIndex=i;
            double a=angle*Math.PI/180;
            var tick=new System.Windows.Shapes.Line { X1=380+Math.Cos(a)*348,Y1=380+Math.Sin(a)*348,X2=380+Math.Cos(a)*355,Y2=380+Math.Sin(a)*355,Stroke=TickBrush(),StrokeThickness=1.5,IsHitTestVisible=false };
            _ticks.Add(tick);Children.Add(tick);
        }
        _sectors=sectors;
        if(restoreFocus)_sectors[0].Focus();
    }
    public void SetAvailable(bool available) { _available=available;foreach(var b in _sectors)b.HasFile=available; }
    public void SelectByKeyboard(int index) { if(_sectors.Length>0)_sectors[(index%_sectors.Length+_sectors.Length)%_sectors.Length].Focus(); }
    /// <summary>Returns the action under a point expressed in this radial menu's coordinates.</summary>
    public ImageAction? ActionAt(Point point) {
        if(!double.IsFinite(point.X)||!double.IsFinite(point.Y))return null;
        foreach(var sector in _sectors)
            if(sector.Contains(TranslatePoint(point,sector)))return sector.CommandKind==RadialCommand.None?sector.Action:null;
        return null;
    }
    internal static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));
}

public sealed class SectorButton : Button
{
    private const double OuterRadius=344;
    // The center card has radius117; this leaves a26-unit clear gap around it.
    private const double InnerRadius=143;
    private const double ContentRadius=(OuterRadius+InnerRadius)/2;
    private readonly Geometry _shape;
    private readonly Geometry _innerEdge;
    private readonly double _angle;
    private readonly string _label;
    private readonly ImageAction _action;
    private readonly RadialCommand _command;
    private Point? _pointer;
    private long _pointerTick;
    public ImageAction Action => _action;
    public RadialCommand CommandKind => _command;
    public bool HasFile { get; set; }
    public SectorButton(ImageAction action,double angle,string label,double sweep=52,RadialCommand command=RadialCommand.None)
    {
        if(!Enum.IsDefined(action))throw new ArgumentOutOfRangeException(nameof(action));
        if(!Enum.IsDefined(command))throw new ArgumentOutOfRangeException(nameof(command));
        if(!double.IsFinite(angle))throw new ArgumentOutOfRangeException(nameof(angle));
        if(string.IsNullOrWhiteSpace(label))throw new ArgumentException("An action needs a visible label.",nameof(label));
        _action=action;_command=command;_angle=angle;_label=label;
        Width=Height=760; Cursor=Cursors.Hand; Background=Brushes.Transparent;
        if(!double.IsFinite(sweep)||sweep<=0||sweep>=180)throw new ArgumentOutOfRangeException(nameof(sweep));
        _shape=CreateWedge(angle-sweep/2,angle+sweep/2,OuterRadius,InnerRadius);
        _innerEdge=GlassMaterial.CreateInnerEdge(_shape);
        Template=new ControlTemplate(typeof(Button));
        AutomationProperties.SetName(this,label);
        MouseEnter+=(_,_)=>InvalidateVisual();
        MouseLeave+=(_,_)=>{_pointer=null;InvalidateVisual();};
        MouseMove+=(_,e)=>{
            if(GlassMaterial.GetTheme(this)!=OrbitTheme.LiquidGlassCrimson||GlassMaterial.GetReducedTransparency(this)||!SystemParameters.ClientAreaAnimation)return;
            var point=e.GetPosition(this);long now=Environment.TickCount64;
            if(now-_pointerTick<20||_pointer is{} previous&&(point-previous).LengthSquared<16)return;
            _pointer=point;_pointerTick=now;InvalidateVisual();
        };
        IsKeyboardFocusedChanged+=(_,_)=>InvalidateVisual();
    }
    /// <summary>Tests the filled wedge using a point in this button's local coordinates.</summary>
    public bool Contains(Point point) => _shape.FillContains(point);
    protected override HitTestResult? HitTestCore(PointHitTestParameters p) => Contains(p.HitPoint)?new PointHitTestResult(this,p.HitPoint):null;
    protected override void OnRender(DrawingContext dc)
    {
        bool hot=IsMouseOver||IsKeyboardFocused;
        GlassMaterial.Draw(dc,_shape,_innerEdge,hot,GlassMaterial.GetReducedTransparency(this),_pointer,this);
        var a=_angle*Math.PI/180; var x=380+Math.Cos(a)*ContentRadius;var y=380+Math.Sin(a)*ContentRadius;
        bool crimson=GlassMaterial.GetTheme(this)==OrbitTheme.LiquidGlassCrimson;
        var ink=SystemParameters.HighContrast?SystemColors.WindowTextBrush:RadialMenu.Brush(crimson?(hot?"#FFF7F4":"#E7E6EC"):(hot?"#2A4054":"#3C4854"));
        dc.PushTransform(new TranslateTransform(x-21,y-39));
        var pen=new Pen(ink,2.5) { StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round };
        if(_command!=RadialCommand.None) {
            DrawCommandIcon(dc,pen,ink,_command);
        } else if(_action is ImageAction.Jpg or ImageAction.WebP) {
            dc.DrawRoundedRectangle(null,pen,new Rect(3,3,36,34),5,5);
            dc.DrawEllipse(null,pen,new Point(13,13),3,3);
            dc.DrawGeometry(null,pen,Geometry.Parse("M 6,33 L 17,22 24,29 30,21 38,30"));
        } else if(_action==ImageAction.Avif) {
            dc.DrawGeometry(null,pen,Geometry.Parse("M 3,35 L 20,5 37,35 Z M 13,35 L 28,12 M 16,21 L 24,35"));
        } else if(_action==ImageAction.Crop) {
            dc.DrawGeometry(null,pen,Geometry.Parse("M 10,2 L 10,30 Q 10,33 13,33 L 39,33 M 2,10 L 30,10 Q 33,10 33,13 L 33,41"));
        } else if(_action==ImageAction.Resize) {
            dc.DrawGeometry(null,pen,Geometry.Parse("M 18,5 L 36,5 36,23 M 36,5 L 16,25 M 11,12 L 5,12 Q 3,12 3,15 L 3,35 Q 3,38 6,38 L 28,38 Q 31,38 31,35 L 31,29"));
        } else if(_action is ImageAction.Compress or ImageAction.VideoCompress or ImageAction.AudioCompress) {
            dc.DrawGeometry(null,pen,Geometry.Parse("M 21,3 L 21,27 M 12,18 L 21,28 30,18 M 4,37 L 38,37 M 6,23 L 4,25 M 36,23 L 38,25"));
        } else if(_action is ImageAction.VideoMp4 or ImageAction.VideoWebm or ImageAction.DownloadVideo) {
            DrawVideoIcon(dc,pen);
        } else if(_action==ImageAction.VideoFrame) {
            DrawFrameIcon(dc,pen);
        } else if(_action is ImageAction.ExtractMp3 or ImageAction.AudioMp3 or ImageAction.AudioWav or ImageAction.AudioFlac or ImageAction.DownloadAudio) {
            DrawMusicIcon(dc,pen);
        } else if(_action is ImageAction.PdfPng or ImageAction.PdfJpg or ImageAction.PdfText or ImageAction.PdfMerge or ImageAction.PdfExtractPages) {
            DrawPdfIcon(dc,pen);
        } else if(_action==ImageAction.MoreTools) {
            dc.DrawEllipse(ink,null,new Point(7,21),2.7,2.7);dc.DrawEllipse(ink,null,new Point(21,21),2.7,2.7);dc.DrawEllipse(ink,null,new Point(35,21),2.7,2.7);
        } else if(_action is ImageAction.ArchiveZip or ImageAction.ExtractZip or ImageAction.ArchiveList or ImageAction.ArchiveTar or ImageAction.ExtractTar or ImageAction.TarList or ImageAction.Archive7z or ImageAction.Extract7z or ImageAction.SevenZipList) {
            DrawArchiveIcon(dc,pen,_action is ImageAction.ExtractZip or ImageAction.ExtractTar or ImageAction.Extract7z,_action is ImageAction.ArchiveList or ImageAction.TarList or ImageAction.SevenZipList);
        } else if(_action==ImageAction.FileHash) {
            DrawHashIcon(dc,pen);
        } else if(_action==ImageAction.FileInfo) {
            DrawInfoIcon(dc,pen,ink);
        } else {
            DrawDocumentIcon(dc,pen);
        }
        dc.Pop();
        var text=new FormattedText(_label,CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),24,ink,VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth=190,MaxLineCount=1,Trimming=TextTrimming.CharacterEllipsis };
        dc.DrawText(text,new Point(x-text.Width/2,y+14));
    }
    private static void DrawCommandIcon(DrawingContext dc,Pen pen,Brush ink,RadialCommand command) {
        switch(command) {
            case RadialCommand.Convert:
                dc.DrawGeometry(null,pen,Geometry.Parse("M 5,12 L 35,12 M 28,5 L 35,12 28,19 M 37,30 L 7,30 M 14,23 L 7,30 14,37"));
                break;
            case RadialCommand.SaveAs:
                dc.DrawGeometry(null,pen,Geometry.Parse("M 3,14 L 3,8 Q 3,5 6,5 L 16,5 21,11 36,11 Q 39,11 39,14 L 39,35 Q 39,38 36,38 L 6,38 Q 3,38 3,35 Z M 4,15 L 38,15 M 21,19 L 21,32 M 15,26 L 21,32 27,26"));
                break;
            case RadialCommand.Copy:
                dc.DrawGeometry(null,pen,Geometry.Parse("M 8,29 L 5,29 5,3 28,3 28,7 M 13,10 L 37,10 37,39 13,39 Z"));
                break;
            case RadialCommand.Trim:
                dc.DrawEllipse(null,pen,new Point(9,11),6,6);dc.DrawEllipse(null,pen,new Point(9,31),6,6);
                dc.DrawGeometry(null,pen,Geometry.Parse("M 14,14 L 37,37 M 14,28 L 37,5"));
                break;
            case RadialCommand.More:
                dc.DrawEllipse(ink,null,new Point(7,21),2.7,2.7);dc.DrawEllipse(ink,null,new Point(21,21),2.7,2.7);dc.DrawEllipse(ink,null,new Point(35,21),2.7,2.7);
                break;
            case RadialCommand.Cancel:
                dc.DrawGeometry(null,pen,Geometry.Parse("M 9,9 L 33,33 M 33,9 L 9,33"));
                break;
            case RadialCommand.Retry:
                dc.DrawGeometry(null,pen,Geometry.Parse("M 35,17 C 33,7 20,3 11,11 C 1,21 10,37 22,37 C 29,37 34,33 37,27 M 36,6 L 36,18 24,18"));
                break;
        }
    }
    private static void DrawDocumentIcon(DrawingContext dc,Pen pen) {
        dc.DrawGeometry(null,pen,Geometry.Parse("M 7,2 L 27,2 38,13 38,39 7,39 Z M 27,2 L 27,13 38,13"));
    }
    private static void DrawVideoIcon(DrawingContext dc,Pen pen) {
        dc.DrawRoundedRectangle(null,pen,new Rect(2,6,38,30),4,4);
        dc.DrawGeometry(null,pen,Geometry.Parse("M 17,14 L 29,21 17,28 Z M 8,6 L 8,36 M 34,6 L 34,36"));
    }
    private static void DrawFrameIcon(DrawingContext dc,Pen pen) {
        dc.DrawRoundedRectangle(null,pen,new Rect(2,5,38,32),4,4);
        dc.DrawEllipse(null,pen,new Point(12,15),3,3);
        dc.DrawGeometry(null,pen,Geometry.Parse("M 5,32 L 16,21 24,29 31,21 38,29 M 8,1 L 8,5 M 18,1 L 18,5 M 28,1 L 28,5 M 38,1 L 38,5"));
    }
    private static void DrawMusicIcon(DrawingContext dc,Pen pen) {
        dc.DrawGeometry(null,pen,Geometry.Parse("M 14,31 L 14,9 35,4 35,26 M 14,16 L 35,11"));
        dc.DrawEllipse(null,pen,new Point(8,32),6,5);
        dc.DrawEllipse(null,pen,new Point(29,27),6,5);
    }
    private static void DrawPdfIcon(DrawingContext dc,Pen pen) {
        DrawDocumentIcon(dc,pen);
        dc.DrawGeometry(null,pen,Geometry.Parse("M 13,20 L 31,20 M 13,26 L 31,26 M 13,32 L 24,32"));
    }
    private static void DrawArchiveIcon(DrawingContext dc,Pen pen,bool extract=false,bool list=false) {
        dc.DrawRoundedRectangle(null,pen,new Rect(4,10,34,28),2,2);
        dc.DrawGeometry(null,pen,Geometry.Parse("M 2,4 L 40,4 40,12 2,12 Z"));
        if(extract)dc.DrawGeometry(null,pen,Geometry.Parse("M 21,17 L 21,32 M 14,25 L 21,32 28,25"));
        else if(list)dc.DrawGeometry(null,pen,Geometry.Parse("M 11,20 L 13,20 M 18,20 L 31,20 M 11,28 L 13,28 M 18,28 L 31,28"));
        else dc.DrawGeometry(null,pen,Geometry.Parse("M 21,4 L 21,17 M 18,20 L 24,20 24,27 18,27 Z M 18,11 L 24,11 M 18,16 L 24,16"));
    }
    private static void DrawHashIcon(DrawingContext dc,Pen pen) {
        dc.DrawGeometry(null,pen,Geometry.Parse("M 15,3 L 10,39 M 30,3 L 25,39 M 4,15 L 39,15 M 2,28 L 37,28"));
    }
    private static void DrawInfoIcon(DrawingContext dc,Pen pen,Brush ink) {
        dc.DrawEllipse(null,pen,new Point(21,21),17,17);
        dc.DrawEllipse(ink,null,new Point(21,12),1.8,1.8);
        dc.DrawGeometry(null,pen,Geometry.Parse("M 18,20 L 21,20 21,31 M 17,31 L 25,31"));
    }
    private static Point P(double r,double angle) {var a=angle*Math.PI/180;return new Point(380+r*Math.Cos(a),380+r*Math.Sin(a));}
    private static Geometry CreateWedge(double start,double end,double ro,double ri)
    {
        double co=1.0,ci=1.65,c=6;
        var g=new StreamGeometry();
        using(var d=g.Open()) {
            d.BeginFigure(P(ro,start+co),true,true);
            d.ArcTo(P(ro,end-co),new Size(ro,ro),0,false,SweepDirection.Clockwise,true,false);
            d.QuadraticBezierTo(P(ro,end),P(ro-c,end),true,false);
            d.LineTo(P(ri+c,end),true,false);
            d.QuadraticBezierTo(P(ri,end),P(ri,end-ci),true,false);
            d.ArcTo(P(ri,start+ci),new Size(ri,ri),0,false,SweepDirection.Counterclockwise,true,false);
            d.QuadraticBezierTo(P(ri,start),P(ri+c,start),true,false);
            d.LineTo(P(ro-c,start),true,false);
            d.QuadraticBezierTo(P(ro,start),P(ro,start+co),true,false);
        }g.Freeze();return g;
    }
}

/// <summary>Noninteractive progress in the hub gap. Only the indeterminate arc rotates; no timer or per-frame layout is used.</summary>
internal sealed class RadialProgressRing : FrameworkElement
{
    private const double Radius=130;
    private readonly RotateTransform _rotation=new(0,380,380);
    private Geometry _arc=Arc(100d/360);
    private bool _rotating;
    private bool _subscribed;
    public double? Fraction { get; private set; }
    public string Description { get; private set; }=string.Empty;

    public RadialProgressRing() {
        Width=Height=760;Visibility=Visibility.Collapsed;IsHitTestVisible=false;Focusable=false;RenderTransform=_rotation;
        AutomationProperties.SetName(this,global::Orbit.Core.Localization.L.Get("Text_5E4B83CB801F"));
        Loaded+=(_,_)=>{
            if(!_subscribed){SystemParameters.StaticPropertyChanged+=ParametersChanged;_subscribed=true;}
            UpdateAnimation();
        };
        Unloaded+=(_,_)=>{
            if(_subscribed){SystemParameters.StaticPropertyChanged-=ParametersChanged;_subscribed=false;}
            StopAnimation();
        };
        IsVisibleChanged+=(_,_)=>UpdateAnimation();
    }
    public void SetProgress(bool visible,double? fraction,string? description) {
        Fraction=visible?fraction:null;
        Description=visible?(string.IsNullOrWhiteSpace(description)?global::Orbit.Core.Localization.L.Get("Text_599CA07B8DD2"):description)+
            (fraction is{} value?" · "+value.ToString("P0",CultureInfo.CurrentUICulture):string.Empty):string.Empty;
        AutomationProperties.SetItemStatus(this,Description);
        _arc=Arc(Fraction??100d/360);
        Visibility=visible?Visibility.Visible:Visibility.Collapsed;
        UpdateAnimation();InvalidateVisual();
    }
    private void ParametersChanged(object? sender,System.ComponentModel.PropertyChangedEventArgs e) {
        if(e.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.HighContrast)) {
            if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke(new Action(()=>{UpdateAnimation();InvalidateVisual();}));return;}
            UpdateAnimation();InvalidateVisual();
        }
    }
    private void UpdateAnimation() {
        bool animate=IsLoaded&&IsVisible&&Fraction is null&&SystemParameters.ClientAreaAnimation&&!SystemParameters.HighContrast;
        if(animate&&!_rotating) {
            _rotation.BeginAnimation(RotateTransform.AngleProperty,new DoubleAnimation(0,360,TimeSpan.FromSeconds(1.4)){RepeatBehavior=RepeatBehavior.Forever});
            _rotating=true;
        } else if(!animate)StopAnimation();
    }
    private void StopAnimation() {
        if(!_rotating)return;
        _rotation.BeginAnimation(RotateTransform.AngleProperty,null);_rotation.Angle=0;_rotating=false;
    }
    protected override void OnRender(DrawingContext dc) {
        bool crimson=GlassMaterial.GetTheme(this)==OrbitTheme.LiquidGlassCrimson;
        bool reduced=GlassMaterial.GetReducedTransparency(this);
        Brush track=SystemParameters.HighContrast?SystemColors.GrayTextBrush:
            new SolidColorBrush(crimson?Color.FromArgb(reduced?(byte)255:(byte)105,180,153,169):Color.FromArgb(reduced?(byte)255:(byte)150,174,185,195));
        Brush accent=SystemParameters.HighContrast?SystemColors.WindowTextBrush:RadialMenu.Brush(crimson?"#E13B65":"#566F87");
        dc.DrawEllipse(null,new Pen(track,2.5),new Point(380,380),Radius,Radius);
        var pen=new Pen(accent,5){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};
        if(Fraction is >=1)dc.DrawEllipse(null,pen,new Point(380,380),Radius,Radius);
        else if(Fraction is null or >0)dc.DrawGeometry(null,pen,_arc);
    }
    private static Geometry Arc(double fraction) {
        var geometry=new StreamGeometry();
        if(fraction>0&&fraction<1) {
            double radians=(-90+fraction*360)*Math.PI/180;
            using var drawing=geometry.Open();
            drawing.BeginFigure(new Point(380,380-Radius),false,false);
            drawing.ArcTo(new Point(380+Radius*Math.Cos(radians),380+Radius*Math.Sin(radians)),new Size(Radius,Radius),0,fraction>.5,SweepDirection.Clockwise,true,false);
        }
        geometry.Freeze();return geometry;
    }
}
