using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Orbit.App.Appearance;
using Orbit.App.Controls;
using Orbit.App.Platform;
using Orbit.App.Settings;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private async Task RunThemeSmokeAsync()
    {
        var directory=Path.Combine(AppContext.BaseDirectory,"themes-smoke");var checks=new List<string>();
        void Require(bool value,string name){if(!value)throw new InvalidOperationException("Theme integration: "+name);checks.Add(name);}
        async Task Layout(){await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);await Task.Delay(40);}
        try{
            Directory.CreateDirectory(directory);File.Delete(Path.Combine(directory,"passed.json"));File.Delete(Path.Combine(directory,"error.txt"));
            Require(IsDiagnosticRun&&_bridge==null&&!ProductivityIoAllowed&&!PersonalizationIoAllowed,"isolated theme mode has no user stores or global bridge");
            BeginAnimation(OpacityProperty,null);Opacity=1;_theme=OrbitTheme.LiquidGlassCrimson;SetCompactMode(false);await Layout();
            Require(GlassDescendants<Button>(_header).Count(b=>Equals(b.Tag,"WindowMinimize"))==1&&GlassDescendants<Button>(_header).All(b=>b.Content is not("⚙" or "×")),"full window has one minimize button while settings remain in the radial menu");
            Require(_radial.SettingsButton.IsVisible&&_radial.SettingsButton.IsEnabled,"settings is an independent visible button inside the radial menu");
            Require(TryFindResource("TextBrush") is SolidColorBrush text&&text.Color.R>230,"Crimson glass uses readable pearl-white text");
            Require(TryFindResource("GlassPanelFill") is SolidColorBrush panel&&panel.Color.A<200,"Crimson tool cards are translucent rather than solid dark blocks");
            Require(_backdrop.IsApplied||!string.IsNullOrEmpty(_backdrop.UnavailableReason)||SystemParameters.HighContrast,"full glass requests actual native blur with a defined fallback");
            CaptureThemeComposite(Path.Combine(directory,"01-crimson-full.png"));
            _radial.SettingsButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));await Layout();
            Require(_panel.IsVisible,"radial settings button opens the actual application settings");
            var tabs=GlassDescendants<TabControl>(_panelContent).Single(t=>Equals(t.Tag,"SettingsTabs"));
            Require(tabs.Items.Count==2&&tabs.Items.OfType<TabItem>().Any(t=>Equals(t.Header,"Темы")),"settings contain a separate accessible Themes tab");
            tabs.SelectedIndex=1;await Layout();
            var choices=GlassDescendants<RadioButton>(_panelContent).ToArray();
            Require(choices.Length==2&&choices.Any(b=>Equals(b.Tag,OrbitTheme.Light))&&choices.Any(b=>Equals(b.Tag,OrbitTheme.LiquidGlassCrimson)),"Themes tab offers both preserved Light and Liquid Glass Crimson choices");
            var light=choices.Single(b=>Equals(b.Tag,OrbitTheme.Light));var crimson=choices.Single(b=>Equals(b.Tag,OrbitTheme.LiquidGlassCrimson));
            var legacyText=Text("Theme-sensitive tool text",13,"#E2DDF9");var previous=(SolidColorBrush)legacyText.Foreground;
            light.IsChecked=true;await Layout();
            Require(_theme==OrbitTheme.Light&&previous.Color.R<60,"theme choice updates existing legacy text as well as dynamic control resources");
            Require(TryFindResource("TextBrush") is SolidColorBrush lightText&&lightText.Color.R<60,"Light restores its dark ink palette");
            Capture(Path.Combine(directory,"02-light-themes.png"));
            crimson.IsChecked=true;await Layout();
            Require(_theme==OrbitTheme.LiquidGlassCrimson&&previous.Color.R>230,"switching back restores Crimson text without reopening cards");
            CaptureThemeComposite(Path.Combine(directory,"03-crimson-themes.png"));
            var store=new SettingsStore(Path.Combine(directory,"owned-settings.json"));
            Require(store.Save(new OrbitSettings{Theme="light"})==null&&store.Load().Theme=="light","Light choice survives a real atomic save and reload in an owned store");
            Require(store.Save(new OrbitSettings{Theme="liquid-glass-crimson"})==null&&store.Load().Theme=="liquid-glass-crimson","Crimson choice survives a real atomic save and reload");
            Require((new OrbitSettings{Theme="invalid"}).Normalize().Theme=="liquid-glass-crimson","unknown persisted theme falls back to a valid theme");
            ClosePanel();_popupAnchor=new HotkeyInvocation(IntPtr.Zero,500,400);SetCompactMode(true);await Layout();
            var handle=new System.Windows.Interop.WindowInteropHelper(this).Handle;GetWindowRect(handle,out var window);
            var sample=ThemeWallpaper(900,900);_popupBackdropFrame=new BackdropFrame(sample,new Rect(window.Left-200,window.Top-200,900,900),TimeSpan.Zero);RefreshPopupBackdrop();await Layout();
            Require(!_backdrop.IsApplied&&GlassMaterial.GetBackdropFrame(_radial)!=null,"compact glass uses a clipped sampled backdrop and disables whole-HWND blur");
            Require(!GlassMaterial.GetReducedTransparency(_radial)&&GlassMaterial.GetTheme(_radial)==OrbitTheme.LiquidGlassCrimson,"compact Crimson preserves transparency and its material palette");
            CaptureThemeComposite(Path.Combine(directory,"04-crimson-compact.png"));
            _radial.SettingsButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));await Layout();
            Require(_compact&&_radialView.Visibility==Visibility.Collapsed&&_panel.IsVisible,"compact settings replace the radial instead of overlapping it");
            Require(_panel.Background is DrawingBrush,"compact card maps the same sampled background into its own clipped surface");
            var patternedFrame=_popupBackdropFrame;
            var white=BitmapSource.Create(1,1,96,96,PixelFormats.Bgra32,null,new byte[]{255,255,255,255},4);white.Freeze();
            _popupBackdropFrame=patternedFrame! with{Image=white};RefreshPopupBackdrop();await Layout();
            var surface=new DrawingVisual();using(var dc=surface.RenderOpen())dc.DrawRectangle(_panel.Background,null,new Rect(0,0,1,1));
            var rendered=new RenderTargetBitmap(1,1,96,96,PixelFormats.Pbgra32);rendered.Render(surface);var pixel=new byte[4];rendered.CopyPixels(pixel,4,0);
            var actualBackground=Color.FromRgb(pixel[2],pixel[1],pixel[0]);
            Require(ThemeContrast(((SolidColorBrush)FindResource("TextBrush")).Color,actualBackground)>=4.5,"actual compact card text stays readable over a white desktop sample");
            Require(ThemeContrast(((SolidColorBrush)FindResource("GlassMutedText")).Color,actualBackground)>=4.5,"compact card secondary text also passes contrast over white");
            _popupBackdropFrame=patternedFrame;RefreshPopupBackdrop();await Layout();
            tabs=GlassDescendants<TabControl>(_panelContent).Single();tabs.SelectedIndex=1;await Layout();CaptureThemeComposite(Path.Combine(directory,"05-compact-themes.png"));
            tabs.SelectedIndex=0;await Layout();var reduced=GlassDescendants<CheckBox>(_panelContent).Single(b=>Equals(b.Content,"Меньше прозрачности"));
            reduced.IsChecked=true;reduced.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));await Layout();
            Require(_reducedTransparency&&GlassMaterial.GetReducedTransparency(_radial)&&GlassMaterial.GetBackdropFrame(_radial)==null&&!_backdrop.IsApplied,"reduced transparency disables sampled and native blur and makes the radial opaque");
            reduced.IsChecked=false;reduced.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));await Layout();
            Require(!_reducedTransparency&&_popupBackdropFrame!=null&&_panel.Background is DrawingBrush,"disabling reduced transparency restores the retained background on the visible card");
            ClosePanel();await Layout();
            Require(GlassMaterial.GetBackdropFrame(_radial)!=null,"returning from the card restores the sampled frame on the radial surfaces");
            var selectionBefore=_files.ToArray();var sourceBefore=_radial.Preview.Source;_theme=OrbitTheme.Light;ApplyGlassAppearance();await Layout();
            Require(_files.SequenceEqual(selectionBefore)&&ReferenceEquals(_radial.Preview.Source,sourceBefore),"theme switching preserves the selected source and image pixels");
            Require(GlassMaterial.GetBackdropFrame(_radial)==null&&GlassMaterial.GetReducedTransparency(_radial),"compact Light keeps the previous opaque white appearance");
            File.WriteAllText(Path.Combine(directory,"passed.json"),JsonSerializer.Serialize(new{Passed=checks.Count,Checks=checks,UserPreferencesWritten=false,DesktopCaptured=false,OwnSyntheticBackdrop=true,NativeInput=false},new JsonSerializerOptions{WriteIndented=true}));
        }catch(Exception error){Directory.CreateDirectory(directory);File.WriteAllText(Path.Combine(directory,"error.txt"),error.ToString());}
        finally{Quit();}
    }
    private static BitmapSource ThemeWallpaper(int width,int height)
    {
        var visual=new DrawingVisual();using(var dc=visual.RenderOpen()){
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(2,6,14),Color.FromRgb(157,3,45),60),null,new Rect(0,0,width,height));
            dc.DrawEllipse(new RadialGradientBrush(Color.FromRgb(248,152,164),Color.FromArgb(0,197,3,55)),null,new Point(width*.75,height*.24),width*.7,height*.65);
            dc.DrawGeometry(new LinearGradientBrush(Color.FromRgb(217,91,119),Color.FromRgb(88,3,31),40),null,Geometry.Parse(FormattableString.Invariant($"M 0,{height*.65} C {width*.4},{height*.25} {width*.6},{height*.9} {width},{height*.35} L {width},{height} L 0,{height} Z")));
        }
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);bitmap.Freeze();return bitmap;
    }
    private static double ThemeContrast(Color first,Color second)
    {
        static double Linear(byte value){double x=value/255d;return x<=.04045?x/12.92:Math.Pow((x+.055)/1.055,2.4);}
        static double Luminance(Color c)=>.2126*Linear(c.R)+.7152*Linear(c.G)+.0722*Linear(c.B);
        double a=Luminance(first),b=Luminance(second);return (Math.Max(a,b)+.05)/(Math.Min(a,b)+.05);
    }
    private void CaptureThemeComposite(string path)
    {
        UpdateLayout();int width=(int)Math.Ceiling(ActualWidth),height=(int)Math.Ceiling(ActualHeight);
        var visual=new DrawingVisual();using(var dc=visual.RenderOpen()){
            dc.DrawImage(ThemeWallpaper(width,height),new Rect(0,0,width,height));
            dc.DrawRectangle(new VisualBrush(_shell),null,new Rect(0,0,width,height));
        }
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(path);encoder.Save(output);
    }
}
