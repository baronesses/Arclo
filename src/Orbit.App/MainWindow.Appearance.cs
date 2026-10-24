using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Orbit.App.Settings;
using Orbit.App.Platform;
using Orbit.App.Controls;
using Orbit.App.Appearance;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private readonly SettingsStore _settingsStore=new();
    private bool _reducedTransparency;
    private OrbitTheme _theme=OrbitTheme.LiquidGlassCrimson;
    private HotkeyGesture _shortcut=HotkeyGesture.Default;
    private readonly TextBlock _shortcutHint=new(){FontSize=10,Foreground=Ink("#C6D8EF"),HorizontalAlignment=HorizontalAlignment.Right};
    private string ShortcutDisplay=>_shortcut.Display;
    private static void GlassAccent(Button button)
    {
        button.SetResourceReference(Control.BackgroundProperty,"GlassAccentFill");
        button.SetResourceReference(Control.BorderBrushProperty,"GlassFocusEdge");
    }
    private void LoadPreferences()
    {
        if(IsDiagnosticRun)return;
        var settings=_settingsStore.Load();
        _theme=settings.Theme=="light"?OrbitTheme.Light:OrbitTheme.LiquidGlassCrimson;_compactSize=settings.CompactSize;_outputDirectory=settings.OutputDirectory;_reducedTransparency=settings.ReducedTransparency;_keepHistory=settings.KeepHistory;_welcomeCompleted=settings.WelcomeCompleted;
        if(HotkeyGesture.TryParse(settings.Shortcut,out var gesture,out _))_shortcut=gesture;
    }
    private bool SavePreferences()
    {
        if(IsDiagnosticRun)return true;
        var error=_settingsStore.Save(new OrbitSettings{Language=Orbit.Core.Localization.L.Language,Theme=_theme==OrbitTheme.Light?"light":"liquid-glass-crimson",CompactSize=_compactSize,OutputDirectory=_outputDirectory,ReducedTransparency=_reducedTransparency,Shortcut=_shortcut.ToString(),KeepHistory=_keepHistory,WelcomeCompleted=_welcomeCompleted});
        if(error!=null){Toast(error);return false;}return true;
    }
    private void ApplyGlassAppearance()
    {
        bool highContrast=SystemParameters.HighContrast;
        bool dense=_reducedTransparency||highContrast;
        bool blur=_backdrop.Apply(!_compact&&!dense);
        UiThemeBrushes.Apply(_theme);
        _radial.SetTheme(_theme);
        _radial.SetGlassAppearance(dense||_theme==OrbitTheme.Light&&_compact);
        string[] keys={"TextBrush","GlassMutedText","GlassControlFill","GlassControlHover","GlassPanelFill","GlassFieldFill","GlassSelection","GlassSelectionText","GlassEdge","GlassFocusEdge","GlassAccentFill","GlassTopLight"};
        foreach(var key in keys)Resources.Remove(key);
        if(highContrast){
            Resources["TextBrush"]=SystemColors.WindowTextBrush;Resources["GlassMutedText"]=SystemColors.WindowTextBrush;
            Resources["GlassControlFill"]=SystemColors.WindowBrush;Resources["GlassControlHover"]=SystemColors.HighlightBrush;
            Resources["GlassPanelFill"]=SystemColors.WindowBrush;Resources["GlassFieldFill"]=SystemColors.WindowBrush;
            Resources["GlassSelection"]=SystemColors.HighlightBrush;Resources["GlassSelectionText"]=SystemColors.HighlightTextBrush;
            Resources["GlassEdge"]=SystemColors.WindowTextBrush;Resources["GlassFocusEdge"]=SystemColors.HighlightBrush;
            Resources["GlassAccentFill"]=SystemColors.HighlightBrush;Resources["GlassTopLight"]=Brushes.Transparent;
        }else if(_theme==OrbitTheme.LiquidGlassCrimson){
            Resources["TextBrush"]=RadialMenu.Brush("#F5F2F6");Resources["GlassMutedText"]=RadialMenu.Brush("#CCBBC5");
            Resources["GlassControlFill"]=RadialMenu.Brush(dense?"#20202A":"#38202530");Resources["GlassControlHover"]=RadialMenu.Brush("#884F1024");
            Resources["GlassPanelFill"]=RadialMenu.Brush(dense?"#0D1018":"#7002060E");Resources["GlassFieldFill"]=RadialMenu.Brush(dense?"#171923":"#8802060E");
            Resources["GlassSelection"]=RadialMenu.Brush("#A0A3072E");Resources["GlassSelectionText"]=RadialMenu.Brush("#FFFFFF");
            Resources["GlassEdge"]=RadialMenu.Brush("#60DDB8C8");Resources["GlassFocusEdge"]=RadialMenu.Brush("#ED7797");
            Resources["GlassAccentFill"]=RadialMenu.Brush("#B0C50337");Resources["GlassTopLight"]=RadialMenu.Brush("#28FFFFFF");
        }else if(dense){
            Resources["GlassControlFill"]=Brushes.White;Resources["GlassControlHover"]=RadialMenu.Brush("#EDF1F5");
            Resources["GlassPanelFill"]=RadialMenu.Brush("#F8FAFC");Resources["GlassFieldFill"]=Brushes.White;
        }
        _shell.Background=_compact?Brushes.Transparent:highContrast?SystemColors.WindowBrush:
            _theme==OrbitTheme.LiquidGlassCrimson?CrimsonShell(blur):new SolidColorBrush(Color.FromArgb(blur?(byte)225:(byte)255,246,248,250));
        _ambient.Opacity=0;
        _shell.BorderBrush=highContrast?SystemColors.WindowTextBrush:RadialMenu.Brush(_theme==OrbitTheme.Light?"#BBC4CE":"#90C9889E");
        _panel.SetResourceReference(Border.BackgroundProperty,"GlassPanelFill");
        _panel.SetResourceReference(Border.BorderBrushProperty,"GlassEdge");
        _toast.SetResourceReference(Border.BackgroundProperty,"GlassPanelFill");
        _toast.SetResourceReference(Border.BorderBrushProperty,"GlassEdge");
        RefreshPopupBackdrop();
    }

    private static Brush CrimsonShell(bool translucent)
    {
        if(!translucent)return RadialMenu.Brush("#080B13");
        var brush=new LinearGradientBrush{StartPoint=new Point(0,0),EndPoint=new Point(.85,1)};
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(152,2,6,14),0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(115,2,6,14),.55));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(92,197,3,55),1));
        brush.Freeze();return brush;
    }
    private void SystemAppearanceChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName is nameof(SystemParameters.HighContrast) or nameof(SystemParameters.ClientAreaAnimation))
            Dispatcher.InvokeAsync(()=>{if(!_closing)ApplyGlassAppearance();});
    }
}
