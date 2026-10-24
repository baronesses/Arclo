using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Orbit.App.Editing;
using Orbit.Core;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private sealed record AudioFormatChoice(string Id,string Label){public override string ToString()=>Label;}
    private void ShowAudioExport()
    {
        if(!CheckMediaSelection(ToolkitKind.Audio))return;
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_B41D2EDFA4DE"),_files.Count==1?Path.GetFileName(_files[0]):global::Orbit.Core.Localization.L.Format("Text_39E4437FE988",(object?)(_files.Count)));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_FBA2410CE365"),13));
        var format=new ComboBox{Tag="audio-export-format",ItemsSource=new[]{new AudioFormatChoice("M4A","M4A · AAC"),new AudioFormatChoice("OPUS",global::Orbit.Core.Localization.L.Get("Text_C8081EC08CCA")),new AudioFormatChoice("VORBIS","OGG · Vorbis (VBR)")},SelectedIndex=0,Margin=new Thickness(0,2,0,8)};_panelContent.Children.Add(format);
        var bitrateLabel=Text(global::Orbit.Core.Localization.L.Get("Text_F2D66BB6C3B8"),13);_panelContent.Children.Add(bitrateLabel);
        var bitrate=new ComboBox{SelectedItem=192,Margin=new Thickness(0,2,0,8)};_panelContent.Children.Add(bitrate);
        var qualityLabel=Text(global::Orbit.Core.Localization.L.Get("Text_3975ED66B3E9"),13);_panelContent.Children.Add(qualityLabel);
        var quality=new Slider{Tag="audio-vorbis-quality",Minimum=0,Maximum=10,Value=5,TickFrequency=1,IsSnapToTickEnabled=true,Margin=new Thickness(2,3,2,8)};_panelContent.Children.Add(quality);
        quality.ValueChanged+=(_,_)=>qualityLabel.Text=global::Orbit.Core.Localization.L.Format("Text_6DB479C40B87",(object?)((int)quality.Value));
        var explanation=Text("",12);_panelContent.Children.Add(explanation);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_077B3E311173"),12));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_288EFA6F5AC7"),11));
        int version=_loadVersion;var paths=_files.ToArray();
        var run=Btn(global::Orbit.Core.Localization.L.Get("Text_4279266CECA1"),(_,_)=>{
            if(version!=_loadVersion||!CheckMediaSelection(ToolkitKind.Audio)||!paths.SequenceEqual(_files)){Toast(global::Orbit.Core.Localization.L.Get("Text_6715E6574350"));return;}
            QueueEditing(new ImageJobOptions{Action=ImageAction.AudioExport,OutputDirectory=_outputDirectory,
                AudioExportFormat=((AudioFormatChoice)format.SelectedItem).Id,AudioExportBitrateKbps=format.SelectedIndex==2?192:(int)bitrate.SelectedItem,
                AudioExportVorbisQuality=format.SelectedIndex==2?(int)quality.Value:5});
        });GlassAccent(run);_panelContent.Children.Add(run);
        void Update(){
            int selected=bitrate.SelectedItem is int value?value:192;bool opus=format.SelectedIndex==1,vorbis=format.SelectedIndex==2;
            var values=opus?new[]{32,64,96,128,160,192,256}:new[]{64,96,128,160,192,256,320};
            bitrate.ItemsSource=values;bitrate.SelectedItem=values.Contains(selected)?selected:192;
            bitrate.IsEnabled=!vorbis;bitrate.Visibility=bitrateLabel.Visibility=vorbis?Visibility.Collapsed:Visibility.Visible;
            quality.Visibility=qualityLabel.Visibility=vorbis?Visibility.Visible:Visibility.Collapsed;
            explanation.Text=vorbis?global::Orbit.Core.Localization.L.Get("Text_09E97FD88ECA")
                :opus?global::Orbit.Core.Localization.L.Get("Text_A589B871204B"):global::Orbit.Core.Localization.L.Get("Text_06B8EB80F917");
        }
        format.SelectionChanged+=(_,_)=>Update();Update();
    }

    private void ShowVideoFrames()
    {
        if(!CheckMediaSelection(ToolkitKind.Video))return;
        if(_selectionMetadata.Any(m=>m.Bytes>16L*1024*1024*1024)){Toast(global::Orbit.Core.Localization.L.Get("Text_119C261331D0"));return;}
        BeginMediaInspection(true,RenderVideoFrames,global::Orbit.Core.Localization.L.Get("Text_ACEB512C28A7"));
    }
    private void RenderVideoFrames(double duration)
    {
        if(!double.IsFinite(duration)||duration<=0||duration>86400||_selectionMetadata.Any(m=>m.DurationSeconds>86400||m.Bytes>16L*1024*1024*1024)){
            _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_AA1D44A5C9F9"),12,"#EDACB9"));return;
        }
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_7BA718210F9A")+MediaTimecode.Format(duration),12));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_11C66EFCD01C"),12));
        var start=new TextBox{Text="0",MaxLength=32,Margin=new Thickness(0,0,0,5)};_panelContent.Children.Add(start);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_0C5271BBB22E"),12));
        var end=new TextBox{Text=MediaTimecode.Format(Math.Min(3,duration)),MaxLength=32,Margin=new Thickness(0,0,0,5)};_panelContent.Children.Add(end);
        var options=new Grid{Margin=new Thickness(0,0,0,4)};options.ColumnDefinitions.Add(new ColumnDefinition());options.ColumnDefinitions.Add(new ColumnDefinition());
        var left=new StackPanel{Margin=new Thickness(0,0,6,0)};left.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_31C42B0FC3EA"),12));
        var fps=new ComboBox{Tag="frames-fps",ItemsSource=new[]{1,2,3,5,10},SelectedItem=1};left.Children.Add(fps);options.Children.Add(left);
        var right=new StackPanel{Margin=new Thickness(6,0,0,0)};right.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_71C70A48C660"),12));
        var width=new ComboBox{Tag="frames-width",ItemsSource=new[]{160,320,640,1280,1920},SelectedItem=1280};right.Children.Add(width);Grid.SetColumn(right,1);options.Children.Add(right);_panelContent.Children.Add(options);
        var message=Text("",12);_panelContent.Children.Add(message);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_56A081544500"),11));
        int version=_loadVersion;var paths=_files.ToArray();
        var run=Btn(global::Orbit.Core.Localization.L.Get("Text_8C5E0B8FE6FA"),(_,_)=>{
            if(version!=_loadVersion||!CheckMediaSelection(ToolkitKind.Video)||!paths.SequenceEqual(_files)){Toast(global::Orbit.Core.Localization.L.Get("Text_4E5758BA6091"));return;}
            if(!Read(out var from,out var to))return;
            QueueEditing(new ImageJobOptions{Action=ImageAction.VideoFrames,OutputDirectory=_outputDirectory,
                FramesStartSeconds=from,FramesDurationSeconds=to-from,FramesPerSecond=(int)fps.SelectedItem,FramesWidth=(int)width.SelectedItem});
        });GlassAccent(run);_panelContent.Children.Add(run);
        bool Read(out double from,out double to){bool a=MediaTimecode.TryParse(start.Text,out from),b=MediaTimecode.TryParse(end.Text,out to);
            return a&&b&&from<to&&to<=duration&&to<=86400&&to-from<=30&&Math.Ceiling((to-from)*(int)fps.SelectedItem)<=300;}
        void Update(){run.IsEnabled=Read(out var from,out var to);message.Text=run.IsEnabled
            ?global::Orbit.Core.Localization.L.Format("Text_C3AA1ED8300F",(object?)(Math.Ceiling((to-from)*(int)fps.SelectedItem)),(object?)(_files.Count)):global::Orbit.Core.Localization.L.Get("Text_1EFD3FF0C5B9");}
        start.TextChanged+=(_,_)=>Update();end.TextChanged+=(_,_)=>Update();fps.SelectionChanged+=(_,_)=>Update();Update();
    }
}
