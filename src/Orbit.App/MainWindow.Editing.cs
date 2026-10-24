using System;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Orbit.App.Editing;
using Orbit.App.Controls;
using Orbit.Core;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private void ShowOcr()
    {
        if(_files.Count==0||_selectionMetadata.Any(m=>m.Kind is not(ToolkitKind.Image or ToolkitKind.Pdf))){Toast(global::Orbit.Core.Localization.L.Get("Text_27797576509E"));return;}
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_9B45DFE62C68"),_files.Count==1?Path.GetFileName(_files[0]):global::Orbit.Core.Localization.L.Format("Text_FFB793B7F6C5",(object?)(_files.Count)));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_31AA98B78CE1"),13));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_858C65F71B97")));
        var languages=new ComboBox{ItemsSource=new[]{global::Orbit.Core.Localization.L.Get("Text_92468B4C0365"),global::Orbit.Core.Localization.L.Get("Text_6DA140FDD4BE"),"English"},SelectedIndex=0,Margin=new Thickness(0,0,0,8)};
        _panelContent.Children.Add(languages);
        var dpi=new ComboBox{ItemsSource=new[]{150,200,300},SelectedItem=200,Margin=new Thickness(0,0,0,8)};
        if(_selectionMetadata.Any(m=>m.Kind==ToolkitKind.Pdf)){
            _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_C15F071BD747")));_panelContent.Children.Add(dpi);
        }
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_24F8C9495C8E"),12));
        var run=Btn(global::Orbit.Core.Localization.L.Get("Text_BD918FC0B058"),(_,_)=>QueueEditing(new ImageJobOptions{
            Action=ImageAction.OcrText,OutputDirectory=_outputDirectory,
            OcrLanguages=languages.SelectedIndex switch{1=>"rus",2=>"eng",_=>"rus+eng"},OcrDpi=(int)dpi.SelectedItem
        }));
        GlassAccent(run);run.Margin=new Thickness(0,10,0,0);_panelContent.Children.Add(run);
    }

    private void ShowMediaTrim(ImageAction action)
    {
        var kind=action==ImageAction.VideoTrim?ToolkitKind.Video:ToolkitKind.Audio;
        if(_files.Count==0||_selectionMetadata.Any(m=>m.Kind!=kind)){Toast(global::Orbit.Core.Localization.L.Get("Text_8924BB79FDFB"));return;}
        var duration=_selectionMetadata.Min(m=>m.DurationSeconds);
        if(!double.IsFinite(duration)||duration<=0){Toast(global::Orbit.Core.Localization.L.Get("Text_CEC34B1FC58B"));return;}
        OpenPanel(action==ImageAction.VideoTrim?global::Orbit.Core.Localization.L.Get("Text_A6AAFD249FE2"):global::Orbit.Core.Localization.L.Get("Text_9EF1B0F1709E"),_files.Count==1?Path.GetFileName(_files[0]):global::Orbit.Core.Localization.L.Format("Text_0B5005AB99E2",(object?)(_files.Count)));
        int panelVersion=_comparisonVersion;
        bool Current()=>!_closing&&panelVersion==_comparisonVersion&&_panel.Visibility==Visibility.Visible;
        _panelContent.Children.Add(Text((_files.Count==1?global::Orbit.Core.Localization.L.Get("Text_B14D63D1A598"):global::Orbit.Core.Localization.L.Get("Text_1D2B4DB42344"))+MediaTimecode.Format(duration),14));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_2459693EE4A5"),12));
        var timeline=new TrimTimeline(duration){Tag="MediaTrimTimeline"};_panelContent.Children.Add(timeline);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_D2A4A963F0D2")));
        var start=new TextBox{Text="00:00:00.000",MaxLength=32,Margin=new Thickness(0,0,0,6)};_panelContent.Children.Add(start);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_E4AA21B2B27E")));
        var end=new TextBox{Text=duration<.001?duration.ToString("0.###############",CultureInfo.InvariantCulture):MediaTimecode.Format(duration),MaxLength=32,Margin=new Thickness(0,0,0,6)};_panelContent.Children.Add(end);
        var interval=Text("",12);_panelContent.Children.Add(interval);
        var run=new Button{Content=global::Orbit.Core.Localization.L.Get("Text_70139572CE3E"),Margin=new Thickness(0,8,0,0)};GlassAccent(run);
        bool ReadRange(out double from,out double to){
            bool first=MediaTimecode.TryParse(start.Text,out from),last=MediaTimecode.TryParse(end.Text,out to);
            return first&&last&&from<to&&to<=duration;
        }
        bool synchronizing=false;
        void UpdateRange(){
            if(synchronizing)return;
            InvalidateMediaPreview();
            bool valid=ReadRange(out var from,out var to);run.IsEnabled=valid&&Current();
            if(valid){synchronizing=true;try{timeline.SetRange(from,to);}finally{synchronizing=false;}}
            interval.Text=valid?global::Orbit.Core.Localization.L.Get("Text_B824A9C540EE")+MediaTimecode.Format(to-from):global::Orbit.Core.Localization.L.Get("Text_134CFD4C8A5C");
            interval.Foreground=Ink(valid?"#A7D8C8":"#EDACB9");
        }
        start.TextChanged+=(_,_)=>UpdateRange();end.TextChanged+=(_,_)=>UpdateRange();
        timeline.SelectionChanged+=(_,_)=>{
            if(synchronizing||!Current())return;
            string a=MediaTimecode.Format(timeline.StartSeconds),b=MediaTimecode.Format(timeline.EndSeconds);
            if(!MediaTimecode.TryParse(a,out var from)||!MediaTimecode.TryParse(b,out var to)||from>=to){
                a=timeline.StartSeconds.ToString("0.###############",CultureInfo.InvariantCulture);
                b=timeline.EndSeconds.ToString("0.###############",CultureInfo.InvariantCulture);
            }
            synchronizing=true;try{start.Text=a;end.Text=b;}finally{synchronizing=false;}
            UpdateRange();
        };
        _panelContent.Children.Add(Text(action==ImageAction.VideoTrim?global::Orbit.Core.Localization.L.Get("Text_72C6245A1280"):global::Orbit.Core.Localization.L.Get("Text_B3B5698AC6AB"),12));
        run.Click+=(_,_)=>{
            if(!Current()||!ReadRange(out var from,out var to)){UpdateRange();return;}
            QueueEditing(new ImageJobOptions{Action=action,OutputDirectory=_outputDirectory,TrimStartSeconds=from,TrimEndSeconds=to});
        };
        _panelContent.Children.Add(run);UpdateRange();
        AddFragmentPreview(_files[0],()=>{var valid=ReadRange(out var from,out var to);return(valid,from,to);});
    }

    private void ShowImageTransform()
    {
        if(_files.Count==0||_selectionKind!=ToolkitKind.Image){Toast(global::Orbit.Core.Localization.L.Get("Text_A42D94238C0D"));return;}
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_BBBE7EFE973C"),_files.Count==1?Path.GetFileName(_files[0]):global::Orbit.Core.Localization.L.Format("Text_CA05C5186FF7",(object?)(_files.Count)));
        var picture=new Image{Source=_preview,MaxWidth=250,MaxHeight=145,Stretch=Stretch.Uniform};
        _panelContent.Children.Add(new Viewbox{Height=145,Stretch=Stretch.Uniform,Margin=new Thickness(0,0,0,4),Child=picture});
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_A989B81F1594")));
        var angle=new ComboBox{ItemsSource=new[]{global::Orbit.Core.Localization.L.Get("Text_D2306B3152EA"),"90°","180°","270°"},SelectedIndex=1,Margin=new Thickness(0,0,0,8)};_panelContent.Children.Add(angle);
        var horizontal=new CheckBox{Content=global::Orbit.Core.Localization.L.Get("Text_9F08AEECE366"),Margin=new Thickness(0,3,0,5)};
        var vertical=new CheckBox{Content=global::Orbit.Core.Localization.L.Get("Text_ADEA8E56D77E"),Margin=new Thickness(0,3,0,5)};
        _panelContent.Children.Add(horizontal);_panelContent.Children.Add(vertical);
        var dimensions=Text("",12);_panelContent.Children.Add(dimensions);
        void UpdatePreview(){
            int degrees=Math.Max(0,angle.SelectedIndex)*90;
            var transforms=new TransformGroup();transforms.Children.Add(new RotateTransform(degrees));
            transforms.Children.Add(new ScaleTransform(horizontal.IsChecked==true?-1:1,vertical.IsChecked==true?-1:1));picture.LayoutTransform=transforms;
            var first=_metadata!;bool swap=degrees is 90 or 270;
            dimensions.Text=$"{(swap?first.Height:first.Width)} × {(swap?first.Width:first.Height)} px · PNG";
        }
        angle.SelectionChanged+=(_,_)=>UpdatePreview();horizontal.Checked+=(_,_)=>UpdatePreview();horizontal.Unchecked+=(_,_)=>UpdatePreview();vertical.Checked+=(_,_)=>UpdatePreview();vertical.Unchecked+=(_,_)=>UpdatePreview();
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_150EA90B871A"),12));
        var run=Btn(global::Orbit.Core.Localization.L.Get("Text_C1E0FA8D9D8F"),(_,_)=>QueueEditing(new ImageJobOptions{
            Action=ImageAction.ImageTransform,OutputDirectory=_outputDirectory,RotationDegrees=Math.Max(0,angle.SelectedIndex)*90,
            FlipHorizontal=horizontal.IsChecked==true,FlipVertical=vertical.IsChecked==true
        }));GlassAccent(run);run.Margin=new Thickness(0,8,0,0);_panelContent.Children.Add(run);UpdatePreview();
    }

    private void QueueEditing(ImageJobOptions options)
    {
        QueueSelected(options);if(_compact)ClosePanel();else ShowJobs();
    }
}
