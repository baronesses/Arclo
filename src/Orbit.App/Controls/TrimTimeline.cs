using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Orbit.App.Controls;

/// <summary>A literal time range, without generated waveform or media decoding. Native thumbs own drag capture.</summary>
public sealed class TrimTimeline : Panel
{
    private static DependencyProperty Palette(string name,string fallback)=>DependencyProperty.Register(name,typeof(Brush),typeof(TrimTimeline),
        new FrameworkPropertyMetadata(Ink(fallback),FrameworkPropertyMetadataOptions.AffectsRender,PaletteChanged));
    private static readonly DependencyProperty FieldProperty=Palette("Field","#F3F5F7");
    private static readonly DependencyProperty SelectionProperty=Palette("Selection","#D7E0EB");
    private static readonly DependencyProperty EdgeProperty=Palette("Edge","#A6B2BF");
    private static readonly DependencyProperty AccentProperty=Palette("Accent","#63758A");
    private static readonly DependencyProperty TextProperty=Palette("Text","#202630");
    private static readonly DependencyProperty MutedProperty=Palette("Muted","#606A76");
    private static readonly DependencyProperty HandleProperty=Palette("Handle","#FFFFFF");
    private readonly HandleThumb _start;
    private readonly HandleThumb _end;
    private double _startSeconds,_endSeconds;
    private Size _layoutSize;
    private bool _lastWasStart=true,_subscribed;
    public double DurationSeconds { get; }
    public double StartSeconds=>_startSeconds;
    public double EndSeconds=>_endSeconds;
    public event EventHandler? SelectionChanged;
    internal Thumb StartThumb=>_start;
    internal Thumb EndThumb=>_end;
    internal double MinimumIntervalSeconds=>Math.Min(.001,DurationSeconds/2);
    internal Rect TrackBounds=>new(12,Math.Max(0,_layoutSize.Height-66),Math.Max(0,_layoutSize.Width-24),24);

    public TrimTimeline(double duration)
    {
        if(!double.IsFinite(duration)||duration<=0)throw new ArgumentOutOfRangeException(nameof(duration),global::Orbit.Core.Localization.L.Get("Text_ACD4269567EF"));
        DurationSeconds=duration;_endSeconds=duration;Height=104;MinHeight=90;MinWidth=120;ClipToBounds=true;
        Background=Brushes.Transparent;Focusable=false;UseLayoutRounding=true;
        _start=new HandleThumb(this,true){Tag="TrimTimelineStart",TabIndex=0};
        _end=new HandleThumb(this,false){Tag="TrimTimelineEnd",TabIndex=1};
        Children.Add(_start);Children.Add(_end);
        SetResourceReference(FieldProperty,"GlassFieldFill");SetResourceReference(SelectionProperty,"GlassSelection");
        SetResourceReference(EdgeProperty,"GlassEdge");SetResourceReference(AccentProperty,"GlassFocusEdge");
        SetResourceReference(TextProperty,"TextBrush");SetResourceReference(MutedProperty,"GlassMutedText");SetResourceReference(HandleProperty,"GlassControlFill");
        AutomationProperties.SetName(this,global::Orbit.Core.Localization.L.Get("Text_E95024A0335B"));
        AutomationProperties.SetHelpText(this,global::Orbit.Core.Localization.L.Get("Text_C65E8046E679"));
        PreviewMouseLeftButtonDown+=TrackDown;
        Loaded+=(_,_)=>{if(!_subscribed){SystemParameters.StaticPropertyChanged+=ParametersChanged;_subscribed=true;}RefreshVisuals();};
        Unloaded+=(_,_)=>{CancelCapture();if(_subscribed){SystemParameters.StaticPropertyChanged-=ParametersChanged;_subscribed=false;}};
        IsVisibleChanged+=(_,_)=>{if(!IsVisible)CancelCapture();};
        IsEnabledChanged+=(_,_)=>{if(!IsEnabled)CancelCapture();RefreshVisuals();};
        UpdateAccessibleValues();
    }

    /// <summary>Sets an exact valid interval. Invalid caller values are rejected before changing the current selection.</summary>
    public void SetRange(double start,double end)
    {
        VerifyAccess();
        if(!double.IsFinite(start)||!double.IsFinite(end)||start<0||end>DurationSeconds||start>=end)
            throw new ArgumentOutOfRangeException(nameof(start),global::Orbit.Core.Localization.L.Get("Text_BF353CB43A92"));
        Publish(start,end);
    }
    private void Publish(double start,double end)
    {
        if(start==_startSeconds&&end==_endSeconds)return;
        _startSeconds=start;_endSeconds=end;
        InvalidateArrange();RefreshVisuals();UpdateAccessibleValues();SelectionChanged?.Invoke(this,EventArgs.Empty);
    }
    internal double SecondsToPosition(double seconds)=>TrackBounds.Left+Math.Clamp(seconds/DurationSeconds,0,1)*TrackBounds.Width;
    private double PositionToSeconds(double x)
    {
        var track=TrackBounds;
        return track.Width<=0?0:Math.Clamp((x-track.Left)/track.Width,0,1)*DurationSeconds;
    }
    private void MoveHandle(bool start,double desired)
    {
        if(!IsEnabled||double.IsNaN(desired))return;
        _lastWasStart=start;
        if(start)
        {
            double maximum=Math.Max(0,Math.Min(Math.BitDecrement(_endSeconds),_endSeconds-MinimumIntervalSeconds));
            Publish(Math.Clamp(desired,0,maximum),_endSeconds);
        }
        else
        {
            double minimum=Math.Min(DurationSeconds,Math.Max(Math.BitIncrement(_startSeconds),_startSeconds+MinimumIntervalSeconds));
            Publish(_startSeconds,Math.Clamp(desired,minimum,DurationSeconds));
        }
    }
    private void Drag(bool start,DragDeltaEventArgs args)
    {
        if(TrackBounds.Width<=0||!double.IsFinite(args.HorizontalChange))return;
        double fraction=(start?_startSeconds:_endSeconds)/DurationSeconds+args.HorizontalChange/TrackBounds.Width;
        MoveHandle(start,Math.Clamp(fraction,0,1)*DurationSeconds);args.Handled=true;
    }
    private void TrackDown(object sender,MouseButtonEventArgs args)
    {
        if(!IsEnabled||args.ChangedButton!=MouseButton.Left||TrackBounds.Width<=0)return;
        for(DependencyObject? node=args.OriginalSource as DependencyObject;node is not null&&node!=this;node=VisualTreeHelper.GetParent(node))
            if(node is Thumb)return;
        var point=args.GetPosition(this);
        if(point.Y<TrackBounds.Top-4||point.Y>TrackBounds.Bottom+4)return;
        MoveNearestHandle(point.X);args.Handled=true;
    }
    internal void MoveNearestHandle(double x)
    {
        if(!double.IsFinite(x)||!IsEnabled)return;
        double a=Math.Abs(x-SecondsToPosition(_startSeconds)),b=Math.Abs(x-SecondsToPosition(_endSeconds));
        bool start=a==b?_lastWasStart:a<b;
        (start?_start:_end).Focus();MoveHandle(start,PositionToSeconds(x));
    }
    private void HandleKey(bool start,KeyEventArgs args)
    {
        if(!IsEnabled)return;
        double step=Math.Max(MinimumIntervalSeconds,Math.Min(.1,DurationSeconds/100));
        if((Keyboard.Modifiers&ModifierKeys.Shift)!=0)step=Math.Min(DurationSeconds,step*10);
        else if((Keyboard.Modifiers&ModifierKeys.Control)!=0)step/=10;
        double current=start?_startSeconds:_endSeconds;
        switch(args.Key)
        {
            case Key.Left:case Key.Down:MoveHandle(start,current-step);break;
            case Key.Right:case Key.Up:MoveHandle(start,current+step);break;
            case Key.Home:MoveHandle(start,0);break;
            case Key.End:MoveHandle(start,DurationSeconds);break;
            default:return;
        }
        args.Handled=true;
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        _start.Measure(new Size(24,48));_end.Measure(new Size(24,48));return new Size(180,104);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        _layoutSize=finalSize;
        // A has its grab label above the band; B below. Both remain reachable even for a subpixel interval.
        _start.Arrange(new Rect(SecondsToPosition(_startSeconds)-12,TrackBounds.Top-24,24,48));
        _end.Arrange(new Rect(SecondsToPosition(_endSeconds)-12,TrackBounds.Top,24,48));
        return finalSize;
    }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var track=TrackBounds;if(track.Width<=0||ActualHeight<=0)return;
        Brush field=ColorBrush(FieldProperty,SystemColors.WindowBrush),selection=ColorBrush(SelectionProperty,SystemColors.HighlightBrush);
        Brush edge=ColorBrush(EdgeProperty,SystemColors.WindowTextBrush),accent=ColorBrush(AccentProperty,SystemColors.HighlightBrush);
        Brush ink=ColorBrush(TextProperty,SystemColors.WindowTextBrush),muted=ColorBrush(MutedProperty,SystemColors.GrayTextBrush);
        double a=SecondsToPosition(_startSeconds),b=SecondsToPosition(_endSeconds);
        drawing.PushClip(new RectangleGeometry(track,5,5));
        drawing.DrawRectangle(field,null,track);
        drawing.DrawRectangle(selection,null,new Rect(a,track.Top,Math.Max(0,b-a),track.Height));
        if(!SystemParameters.HighContrast)
        {
            drawing.PushOpacity(.13);
            drawing.DrawRectangle(ink,null,new Rect(track.Left,track.Top,Math.Max(0,a-track.Left),track.Height));
            drawing.DrawRectangle(ink,null,new Rect(b,track.Top,Math.Max(0,track.Right-b),track.Height));drawing.Pop();
        }
        drawing.Pop();drawing.DrawRoundedRectangle(null,new Pen(edge,1),track,5,5);
        drawing.DrawLine(new Pen(accent,1.5),new Point(a,track.Top),new Point(b,track.Top));
        if(ActualHeight>=104)
        {
            double width=Math.Max(1,(ActualWidth-8)/2);
            FormattedText before=Text("A  "+Clock(_startSeconds,true),10.5,ink,width),after=Text("B  "+Clock(_endSeconds,true),10.5,ink,width);
            drawing.DrawText(before,new Point(0,0));drawing.DrawText(after,new Point(ActualWidth-after.Width,0));
        }
        var longest=Text(Clock(DurationSeconds,false),9.5,muted,ActualWidth);
        int intervals=track.Width>=4*(longest.Width+12)?4:track.Width>=2*(longest.Width+12)?2:1;
        for(int i=0;i<=intervals;i++)
        {
            double seconds=DurationSeconds*(i/(double)intervals),x=SecondsToPosition(seconds);
            drawing.DrawLine(new Pen(muted,.7),new Point(x,ActualHeight-20),new Point(x,ActualHeight-16));
            var label=Text(Clock(seconds,false),9.5,muted,Math.Max(1,ActualWidth));
            drawing.DrawText(label,new Point(Math.Clamp(x-label.Width/2,0,Math.Max(0,ActualWidth-label.Width)),ActualHeight-14));
        }
    }
    private Brush ColorBrush(DependencyProperty property,Brush contrast)=>SystemParameters.HighContrast?contrast:(Brush)GetValue(property);
    private FormattedText Text(string text,double size,Brush ink,double width)=>new(text,CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),size,ink,VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {MaxTextWidth=Math.Max(1,width),MaxLineCount=1,Trimming=TextTrimming.CharacterEllipsis};
    private static string Clock(double seconds,bool precise)
    {
        if(seconds>0&&seconds<.001||seconds>=1e12)return seconds.ToString("G4",CultureInfo.CurrentUICulture)+global::Orbit.Core.Localization.L.Get("Text_DDAE8EB8BF21");
        long whole=(long)Math.Floor(seconds);long hours=whole/3600;long minutes=whole/60%60;long remaining=whole%60;
        string value=hours>0?FormattableString.Invariant($"{hours}:{minutes:00}:{remaining:00}"):FormattableString.Invariant($"{whole/60}:{remaining:00}");
        return precise?value+"."+((int)((seconds-Math.Floor(seconds))*1000)).ToString("000",CultureInfo.InvariantCulture):value;
    }
    private void UpdateAccessibleValues()
    {
        AutomationProperties.SetItemStatus(_start,Clock(_startSeconds,true));AutomationProperties.SetItemStatus(_end,Clock(_endSeconds,true));
        _start.ToolTip=global::Orbit.Core.Localization.L.Get("Text_39CE0432C8DF")+Clock(_startSeconds,true);_end.ToolTip=global::Orbit.Core.Localization.L.Get("Text_992CD7A7C9B6")+Clock(_endSeconds,true);
    }
    private static void PaletteChanged(DependencyObject target,DependencyPropertyChangedEventArgs args)=>((TrimTimeline)target).RefreshVisuals();
    private void RefreshVisuals(){InvalidateVisual();_start?.InvalidateVisual();_end?.InvalidateVisual();}
    private void ParametersChanged(object? sender,System.ComponentModel.PropertyChangedEventArgs args)
    {
        if(args.PropertyName!=nameof(SystemParameters.HighContrast))return;
        if(Dispatcher.CheckAccess())RefreshVisuals();else Dispatcher.BeginInvoke(new Action(RefreshVisuals));
    }
    private void CancelCapture()
    {
        if(_start.IsDragging)_start.CancelDrag();if(_end.IsDragging)_end.CancelDrag();
        if(Mouse.Captured is Visual captured&&(ReferenceEquals(captured,this)||IsAncestorOf(captured)))Mouse.Capture(null);
    }
    private static Brush Ink(string hex){var brush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));brush.Freeze();return brush;}

    private sealed class HandleThumb : Thumb
    {
        private readonly TrimTimeline _owner;
        private readonly bool _isStart;
        public HandleThumb(TrimTimeline owner,bool start)
        {
            _owner=owner;_isStart=start;Width=24;Height=48;Focusable=true;IsTabStop=true;Cursor=Cursors.SizeWE;
            Template=new ControlTemplate(typeof(Thumb));FocusVisualStyle=null;
            AutomationProperties.SetName(this,start?global::Orbit.Core.Localization.L.Get("Text_C3FA5873ED3F"):global::Orbit.Core.Localization.L.Get("Text_9B9488FB90D7"));
            AutomationProperties.SetHelpText(this,global::Orbit.Core.Localization.L.Get("Text_9F151D685CCF"));
            DragDelta+=(_,args)=>owner.Drag(start,args);PreviewKeyDown+=(_,args)=>owner.HandleKey(start,args);
            IsKeyboardFocusedChanged+=(_,_)=>InvalidateVisual();MouseEnter+=(_,_)=>InvalidateVisual();MouseLeave+=(_,_)=>InvalidateVisual();
        }
        protected override void OnRender(DrawingContext drawing)
        {
            drawing.DrawRectangle(Brushes.Transparent,null,new Rect(0,0,ActualWidth,ActualHeight));
            var accent=_owner.ColorBrush(AccentProperty,SystemColors.HighlightBrush);
            var fill=_owner.ColorBrush(HandleProperty,SystemColors.HighlightBrush);
            var ink=_owner.ColorBrush(TextProperty,SystemColors.HighlightTextBrush);
            var pen=new Pen(accent,IsKeyboardFocused?2.5:IsMouseOver?2:1.3);
            drawing.DrawLine(new Pen(accent,2),new Point(12,_isStart?23:0),new Point(12,_isStart?48:25));
            var box=new Rect(.8,_isStart ? .8 : 24.8,22.4,22.4);drawing.DrawRoundedRectangle(fill,pen,box,5,5);
            var label=_owner.Text(_isStart?"A":"B",12,ink,22);drawing.DrawText(label,new Point((24-label.Width)/2,box.Top+(box.Height-label.Height)/2));
        }
    }
}
