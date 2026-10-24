using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Orbit.App.Appearance;
using Orbit.App.Layout;
using Orbit.App.Platform;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private readonly PopupBackdropSampler _popupSampler=new(TimeSpan.FromMilliseconds(120));
    private CancellationTokenSource? _popupFrameCancellation;
    private BackdropFrame? _popupBackdropFrame;
    private bool _popupBackdropRefreshPending;

    private async Task PreparePopupBackdropAsync(HotkeyInvocation invocation)
    {
        _popupFrameCancellation?.Cancel();_popupFrameCancellation?.Dispose();
        _popupFrameCancellation=new CancellationTokenSource();var ct=_popupFrameCancellation.Token;
        _popupBackdropFrame=null;_radial.SetBackdropFrame(null,Rect.Empty);
        if(IsDiagnosticRun||_theme!=OrbitTheme.LiquidGlassCrimson||_reducedTransparency||SystemParameters.HighContrast)return;
        var monitor=NativeScreen.GetWorkAreaAt(invocation.CursorX,invocation.CursorY);
        var work=new Rect(monitor.Left,monitor.Top,monitor.Width,monitor.Height);
        var radial=PopupPlacementMath.CenterAtCursor(new Point(invocation.CursorX,invocation.CursorY),work,new Size(_compactSize,_compactSize),monitor.ScaleX,monitor.ScaleY);
        var card=PopupPlacementMath.CenterAtCursor(new Point(invocation.CursorX,invocation.CursorY),work,new Size(_compactSize,600),monitor.ScaleX,monitor.ScaleY);
        var bounds=Rect.Union(radial.PhysicalBounds,card.PhysicalBounds);
        BackdropFrame? frame;
        try{frame=await _popupSampler.CaptureAsync(bounds,ct);}
        catch(OperationCanceledException)when(ct.IsCancellationRequested){return;}
        if(ct.IsCancellationRequested||_closing)return;
        _popupBackdropFrame=frame;RefreshPopupBackdrop();
    }

    private async Task RecaptureThemeBackdropAsync()
    {
        if(!_compact||!IsVisible||_theme!=OrbitTheme.LiquidGlassCrimson||_reducedTransparency||IsDiagnosticRun)return;
        int version=_invocationVersion;
        var invocation=_popupAnchor??NativeScreen.GetCursorInvocation();bool panel=_panel.IsVisible;
        Hide();await PreparePopupBackdropAsync(invocation);
        if(_closing||version!=_invocationVersion)return;
        ShowActivated=false;Show();PlacePopup(new Size(_compactSize,panel?600:_compactSize));ShowActivated=true;Activate();
    }

    private void RefreshPopupBackdrop()
    {
        if(_popupBackdropRefreshPending||_closing)return;
        _popupBackdropRefreshPending=true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render,new Action(()=>{
            _popupBackdropRefreshPending=false;
            if(_closing)return;
            bool use=_compact&&_theme==OrbitTheme.LiquidGlassCrimson&&!_reducedTransparency&&!SystemParameters.HighContrast;
            var frame=_popupBackdropFrame;
            if(!use||frame==null){_radial.SetBackdropFrame(null,Rect.Empty);_panel.SetResourceReference(Border.BackgroundProperty,"GlassPanelFill");return;}
            var handle=new WindowInteropHelper(this).Handle;
            if(handle==IntPtr.Zero||!GetWindowRect(handle,out var window))return;
            var dpi=VisualTreeHelper.GetDpi(this);
            var frameInWindow=new Rect((frame.ScreenBounds.Left-window.Left)/dpi.DpiScaleX,(frame.ScreenBounds.Top-window.Top)/dpi.DpiScaleY,frame.ScreenBounds.Width/dpi.DpiScaleX,frame.ScreenBounds.Height/dpi.DpiScaleY);
            if(_radialView.IsVisible&&_radialView.ActualWidth>0){
                var transform=_radial.TransformToAncestor(this).Inverse;
                if(transform!=null)_radial.SetBackdropFrame(frame.Image,transform.TransformBounds(frameInWindow));
            }
            if(_panel.IsVisible&&_panel.ActualWidth>0){
                var origin=_panel.TranslatePoint(new Point(),this);
                var imageBounds=new Rect(frameInWindow.X-origin.X,frameInWindow.Y-origin.Y,frameInWindow.Width,frameInWindow.Height);
                var drawing=new DrawingGroup();using(var dc=drawing.Open()){
                    dc.PushClip(new RectangleGeometry(new Rect(_panel.RenderSize)));
                    dc.DrawImage(frame.Image,imageBounds);
                    // The compact card has no shell underneath; keep text readable over a bright desktop.
                    dc.DrawRectangle(Controls.RadialMenu.Brush("#B802060E"),null,new Rect(_panel.RenderSize));dc.Pop();
                }
                drawing.Freeze();_panel.Background=new DrawingBrush(drawing){ViewboxUnits=BrushMappingMode.Absolute,Viewbox=new Rect(_panel.RenderSize),Stretch=Stretch.Fill};
            }
        }));
    }
}
