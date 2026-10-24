using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Orbit.App.Controls;
using Orbit.App.Platform;
using Orbit.Core;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private async Task RunGlassSmokeAsync()
    {
        var destination=Path.Combine(AppContext.BaseDirectory,"glass-smoke");
        var checks=new List<string>();
        ComboBox? openCombo=null;
        var originalMetadata=_metadata;
        var originalFiles=_files;
        void Require(bool condition,string name){if(!condition)throw new InvalidOperationException("Glass integration: "+name);checks.Add(name);}
        async Task Layout(){await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);await Task.Delay(40);}
        bool CenterReachesButton()
        {
            var hit=VisualTreeHelper.HitTest(_radial,new Point(_radial.Width/2,_radial.Height/2));
            for(DependencyObject? item=hit?.VisualHit;item!=null;item=VisualTreeHelper.GetParent(item))
                if(item is Button button)return ReferenceEquals(button,_radial.CenterCard);
            return false;
        }
        try
        {
            Directory.CreateDirectory(destination);File.Delete(Path.Combine(destination,"passed.json"));File.Delete(Path.Combine(destination,"error.txt"));
            Require(_args.Contains("--glass-smoke")&&IsDiagnosticRun&&_bridge==null,"isolated diagnostic skips user preferences and global bridge");
            Require(Application.Current.Resources.MergedDictionaries.Any(d=>d.Source?.OriginalString.Contains("GlassStyles",StringComparison.Ordinal)==true),"real App.xaml merges GlassStyles resources");
            Require(Application.Current.TryFindResource(typeof(ComboBox)) is Style&&Application.Current.TryFindResource(typeof(TextBox)) is Style&&Application.Current.TryFindResource(typeof(Slider)) is Style&&TryFindResource("GlassPanelFill") is Brush,"real control styles and glass brushes resolve");
            BeginAnimation(OpacityProperty,null);Opacity=1;_theme=Appearance.OrbitTheme.Light;SetCompactMode(false);ApplyGlassAppearance();await Layout();
            Require(CenterReachesButton(),"center hit test reaches its Button through glass decorations");
            Require(_radialView.ActualWidth<=440.1,"full view radial fits the smaller 440 maximum");
            Require(_shell.CornerRadius.TopLeft<=12&&_panel.CornerRadius.TopLeft<=8,"window and tool card use restrained corners");
            Require(_ambient.Opacity==0&&_shell.Background is SolidColorBrush,"shell is a single flat surface without ambient gradients");
            Require(TryFindResource("TextBrush") is SolidColorBrush ink&&(SystemParameters.HighContrast||ink.Color.R<60&&ink.Color.G<60&&ink.Color.B<70),"light surfaces have dark readable text");
            var fullBackdrop=_backdrop.AppliedMode.ToString();
            Require(_backdrop.IsApplied||!string.IsNullOrEmpty(_backdrop.UnavailableReason)||SystemParameters.HighContrast,"full view enables native blur or supplies a clear fallback reason");
            Capture(Path.Combine(destination,"01-main.png"));

            var originalActions=_radial.Children.OfType<SectorButton>().Select(s=>new RadialAction(s.Action,AutomationProperties.GetName(s),s.ToolTip?.ToString()??"")).ToArray();
            ShowSettings();await Layout();
            var radialBounds=_radialView.TransformToAncestor(_body).TransformBounds(new Rect(_radialView.RenderSize));
            var panelBounds=_panel.TransformToAncestor(_body).TransformBounds(new Rect(_panel.RenderSize));
            Require(!radialBounds.IntersectsWith(panelBounds),"full radial and settings occupy separate columns without overlap");
            Require(panelBounds.Top>=0&&panelBounds.Bottom<=_body.ActualHeight,"settings stay between the header and footer");
            var verticalButtons=GlassDescendants<StackPanel>(_panelContent).Single(p=>Equals(p.Tag,"GeneralSettings")).Children.OfType<Button>().Take(3).ToArray();
            Require(verticalButtons.Length==3&&verticalButtons.All(b=>b.Margin.Top+b.Margin.Bottom>=6),"stacked settings buttons have spacing between their borders");
            var settingsScroll=(ScrollViewer)_panel.Child;settingsScroll.ScrollToEnd();await Layout();
            var scrollBar=GlassDescendants<ScrollBar>(_panel).First(b=>b.Orientation==Orientation.Vertical&&b.IsVisible);scrollBar.ApplyTemplate();
            Require(scrollBar.ActualWidth<=10&&scrollBar.Template.FindName("PART_Track",scrollBar) is Track{Thumb:not null},"settings use a narrow glass scrollbar with a real draggable thumb");
            double bottom=settingsScroll.VerticalOffset;ScrollBar.PageUpCommand.Execute(null,scrollBar);await Layout();
            Require(bottom>0&&settingsScroll.VerticalOffset<bottom,"glass scrollbar page command scrolls the actual settings viewport");
            settingsScroll.ScrollToTop();await Layout();
            var sizes=GlassDescendants<ComboBox>(_panelContent).Single();openCombo=sizes;sizes.ApplyTemplate();
            sizes.SelectedItem=520;
            Require(sizes.SelectedItem is int selected&&selected==520&&_compactSize==520,"integer size selection updates the actual compact size setting");
            var transparency=GlassDescendants<CheckBox>(_panelContent).Single(c=>Equals(c.Content,"Меньше прозрачности"));
            transparency.IsChecked=true;transparency.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Require(!_backdrop.IsApplied,"reduced transparency disables the native blur");
            Require(_reducedTransparency&&GlassMaterial.GetReducedTransparency(_radial)&&_radial.Children.OfType<SectorButton>().All(GlassMaterial.GetReducedTransparency),"checkbox Click updates preference and existing inherited glass material");
            _radial.SetActions(new[]{new RadialAction(ImageAction.FileInfo,"Сведения","Проверка"),new RadialAction(ImageAction.FileHash,"SHA-256","Проверка"),new RadialAction(ImageAction.ArchiveZip,"ZIP","Проверка")});
            Require(_radial.Children.OfType<SectorButton>().Count()==3&&_radial.Children.OfType<SectorButton>().All(GlassMaterial.GetReducedTransparency),"newly created context sectors inherit reduced transparency");
            transparency.IsChecked=false;transparency.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Require(!_reducedTransparency&&GlassMaterial.GetReducedTransparency(_radial)==SystemParameters.HighContrast&&_radial.Children.OfType<SectorButton>().All(s=>GlassMaterial.GetReducedTransparency(s)==SystemParameters.HighContrast),"checkbox restores normal material while honoring system high contrast");
            _radial.SetActions(originalActions);await Layout();
            var popup=sizes.Template.FindName("PART_Popup",sizes) as Popup;
            var drop=sizes.Template.FindName("DropToggle",sizes) as ToggleButton;
            Require(popup!=null&&drop!=null,"ComboBox builds its actual PART_Popup and DropToggle");
            var toggle=(IToggleProvider?)new ToggleButtonAutomationPeer(drop!).GetPattern(PatternInterface.Toggle);
            Require(toggle!=null,"drop toggle exposes its real WPF toggle action");
            toggle!.Toggle();await Layout();
            Require(sizes.IsDropDownOpen&&popup!.IsOpen,"toggle action opens ComboBox through its template binding");
            sizes.SelectedItem=360;
            Require(sizes.SelectedItem is int value&&value==360&&_compactSize==360,"open dropdown retains integer selections and setting updates");
            sizes.IsDropDownOpen=false;sizes.SelectedItem=380;await Layout();
            Capture(Path.Combine(destination,"02-settings.png"));

            var structuralPath=Path.Combine(destination,"structural-only-not-opened.png");
            _metadata=new ToolkitMetadata(structuralPath,"PNG",160,100,0,ToolkitKind.Image);_files=new List<string>{structuralPath};
            ShowAction(ImageAction.Resize);await Layout();
            var width=GlassDescendants<TextBox>(_panelContent).First();width.ApplyTemplate();width.Text="123";
            Require(width.Template.FindName("PART_ContentHost",width)!=null&&ParseDimension(width.Text)==123,"real resize TextBox has content host and accepts manually edited pixel width");
            ShowAction(ImageAction.WebP);await Layout();
            var slider=GlassDescendants<Slider>(_panelContent).Single();slider.ApplyTemplate();
            var track=slider.Template.FindName("PART_Track",slider) as Track;
            Require(track?.Thumb!=null,"real quality Slider builds PART_Track and its Thumb");
            slider.Value=77;await Layout();
            Require(track!.Value==77,"Slider value reaches the templated track binding");
            track.SetCurrentValue(Track.ValueProperty,66d);track.GetBindingExpression(Track.ValueProperty)?.UpdateSource();await Layout();
            Require(slider.Value==66,"track value changes return through the two-way Slider binding");
            Capture(Path.Combine(destination,"03-parameters.png"));

            _metadata=originalMetadata;_files=originalFiles;ClosePanel();_popupAnchor=new HotkeyInvocation(IntPtr.Zero,500,400);SetCompactMode(true);await Layout();
            Require(_compact&&_header.Visibility==Visibility.Collapsed&&_radialView.Visibility==Visibility.Visible&&CenterReachesButton(),"compact glass layout keeps the center button interactive");
            Require(Math.Abs(Width-380)<1&&Math.Abs(Height-380)<1,"compact window uses the new 380 size at its invocation anchor");
            Require(_radial.FileName.FontSize*_radialView.ActualWidth/_radial.Width>=11.9,"compact file name retains at least twelve logical pixels");
            Require(!_backdrop.IsApplied&&GlassMaterial.GetReducedTransparency(_radial),"compact radial uses solid white surfaces and clear transparent gaps");
            Capture(Path.Combine(destination,"04-compact.png"));
            var visual=new DrawingVisual();
            using(var drawing=visual.RenderOpen())
            {
                var background=new SolidColorBrush(Color.FromRgb(225,231,237));
                drawing.DrawRectangle(background,null,new Rect(0,0,560,560));

                drawing.DrawRectangle(new VisualBrush(_radial){Stretch=Stretch.Uniform},null,new Rect(20,20,520,520));
            }
            var bitmap=new RenderTargetBitmap(560,560,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using(var stream=File.Create(Path.Combine(destination,"05-radial-on-opaque-background.png")))encoder.Save(stream);
            File.WriteAllText(Path.Combine(destination,"passed.json"),JsonSerializer.Serialize(new{Passed=checks.Count,Checks=checks,FullBackdrop=fullBackdrop,CompactBackdrop=_backdrop.AppliedMode.ToString(),UserPreferencesRead=false,UserPreferencesWritten=false,CodecExecuted=false,NativeInputSynthesized=false,WpfToggleAutomation=true,HighContrast=SystemParameters.HighContrast,OpaquePreviewRenderedFromActualWpfVisual=true},new JsonSerializerOptions{WriteIndented=true}));
        }
        catch(Exception exception){Directory.CreateDirectory(destination);File.WriteAllText(Path.Combine(destination,"error.txt"),exception.ToString());}
        finally{if(openCombo!=null)openCombo.IsDropDownOpen=false;_metadata=originalMetadata;_files=originalFiles;Quit();}
    }

    private static IEnumerable<T> GlassDescendants<T>(DependencyObject root) where T:DependencyObject
    {
        if(root is T match)yield return match;
        for(int index=0;index<VisualTreeHelper.GetChildrenCount(root);index++)
            foreach(var descendant in GlassDescendants<T>(VisualTreeHelper.GetChild(root,index)))yield return descendant;
    }
}
