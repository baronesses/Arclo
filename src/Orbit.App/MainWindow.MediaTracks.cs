using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Orbit.App.Editing;
using Orbit.Core;
using Orbit.Core.Media;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _trackInspection;
    private void StopTrackInspection()=>_trackInspection?.Cancel();
    private sealed record TrackChoice(MediaTrack Track)
    {
        public override string ToString()=>global::Orbit.Core.Localization.L.Format("Text_865630CB50F5",(object?)(Track.StreamIndex),(object?)(Track.Codec),(object?)(CleanTrackTag(Track.Language)),(object?)(Track.Channels));
    }
    private static string CleanTrackTag(string value)=>string.IsNullOrWhiteSpace(value)?"—":new string(value.Where(c=>!char.IsControl(c)).Take(120).ToArray());
    private bool CheckSingleMedia()
    {
        if(_selectionLoading||_files.Count!=1||_selectionMetadata.Count!=1||_selectionKind is not(ToolkitKind.Video or ToolkitKind.Audio)){
            Toast(global::Orbit.Core.Localization.L.Get("Text_7517D07B0266"));return false;
        }
        var m=_selectionMetadata[0];
        if(m.Bytes is <=0 or >16L*1024*1024*1024||!double.IsFinite(m.DurationSeconds)||m.DurationSeconds is <=0 or >86400){
            Toast(global::Orbit.Core.Localization.L.Get("Text_C30FE636A3F8"));return false;
        }
        return true;
    }
    private void ShowMediaTracks(bool extract)
    {
        if(!CheckSingleMedia())return;
        OpenPanel(extract?global::Orbit.Core.Localization.L.Get("Text_FA1849E17A5B"):global::Orbit.Core.Localization.L.Get("Text_4B139322A5A5"),Path.GetFileName(_files[0]));
        var loading=Text(global::Orbit.Core.Localization.L.Get("Text_98E356026A9B"),13);_panelContent.Children.Add(loading);
        var cancellation=CancellationTokenSource.CreateLinkedTokenSource(_resultToolsCancellation.Token);_trackInspection=cancellation;
        int panelVersion=_comparisonVersion,loadVersion=_loadVersion;string path=_files[0];
        _=TrackResultTask(LoadMediaTracksAsync(path,extract,panelVersion,loadVersion,cancellation,loading));
    }
    private async Task LoadMediaTracksAsync(string path,bool extract,int panelVersion,int loadVersion,CancellationTokenSource cancellation,TextBlock loading)
    {
        try{
            var catalog=await new MediaTracksEngine().InspectAsync(path,cancellation.Token);
            if(_closing||cancellation.IsCancellationRequested||panelVersion!=_comparisonVersion||loadVersion!=_loadVersion)return;
            _panelContent.Children.Remove(loading);
            _panelContent.Children.Add(Text($"{catalog.Format} · "+(catalog.DurationSeconds>0?MediaTimecode.Format(catalog.DurationSeconds):global::Orbit.Core.Localization.L.Get("Text_DBE6015960D2"))+$" · {FormatBytes(catalog.Bytes)}",13));
            if(extract)RenderTrackExtractor(catalog,loadVersion);
            else{
                _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Format("Text_2ED69AF649B4",(object?)(catalog.Streams.Count)),13));
                foreach(var track in catalog.Streams)AddTrackDetails(_panelContent,track);
                _panelContent.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_4A612B084F7C"),(_,_)=>ShowMediaTracks(true)));
            }
        }catch(OperationCanceledException){}
        catch(Exception ex){if(!_closing&&panelVersion==_comparisonVersion&&loadVersion==_loadVersion)loading.Text=global::Orbit.Core.Localization.L.Get("Text_3C3F9B74BB52")+ex.Message;}
        finally{if(ReferenceEquals(_trackInspection,cancellation))_trackInspection=null;cancellation.Dispose();}
    }
    private void AddTrackDetails(StackPanel host,MediaTrack track)
    {
        string type=track.Type switch{"Audio"=>global::Orbit.Core.Localization.L.Get("Text_1718E90D59C5"),"Video"=>track.IsAttachedPicture?global::Orbit.Core.Localization.L.Get("Text_5BCD9613EB14"):global::Orbit.Core.Localization.L.Get("Text_F92FAFB4C00A"),"Subtitle"=>global::Orbit.Core.Localization.L.Get("Text_BF52ABCA49A2"),"Attachment"=>global::Orbit.Core.Localization.L.Get("Text_60942A954137"),_=>global::Orbit.Core.Localization.L.Get("Text_D28130B83C8C")};
        host.Children.Add(Text($"#{track.StreamIndex} · {type} · {CleanTrackTag(track.Codec)}"+(track.IsDefault?global::Orbit.Core.Localization.L.Get("Text_F806C3DC7393"):""),14,"#E9E3FF"));
        if(track.Type=="Audio")host.Children.Add(Text(global::Orbit.Core.Localization.L.Format("Text_3AF2F721029C",(object?)(track.Channels),(object?)(track.SampleRate)),12));
        if(track.Type=="Video")host.Children.Add(Text($"{track.Width} × {track.Height} px",12));
        if(!string.IsNullOrWhiteSpace(track.Language)||!string.IsNullOrWhiteSpace(track.Title))host.Children.Add(Text(global::Orbit.Core.Localization.L.Format("Text_07FE53076D8F",(object?)(CleanTrackTag(track.Language)),(object?)(CleanTrackTag(track.Title))),12));
        host.Children.Add(Text((track.DurationSeconds>0?MediaTimecode.Format(track.DurationSeconds):global::Orbit.Core.Localization.L.Get("Text_DBE6015960D2"))+
            (track.Bitrate>0?global::Orbit.Core.Localization.L.Format("Text_0A529CFBD58C",(object?)(track.Bitrate/1000d)):global::Orbit.Core.Localization.L.Get("Text_0143C0145FC0")),11));
    }
    private void RenderTrackExtractor(MediaTrackCatalog catalog,int loadVersion)
    {
        var choices=catalog.Streams.Where(s=>s.Type=="Audio").Select(s=>new TrackChoice(s)).ToArray();
        if(choices.Length==0){_panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_8393C663C9BE"),13));return;}
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_8643D91D3E51"),12));
        var track=new ComboBox{Tag="audio-track",ItemsSource=choices,SelectedIndex=0,Margin=new Thickness(0,2,0,7)};_panelContent.Children.Add(track);
        var details=new StackPanel();_panelContent.Children.Add(details);
        void UpdateTrack(){details.Children.Clear();if(track.SelectedItem is TrackChoice choice)AddTrackDetails(details,choice.Track);}
        track.SelectionChanged+=(_,_)=>UpdateTrack();UpdateTrack();
        var grid=new Grid{Margin=new Thickness(0,4,0,6)};grid.ColumnDefinitions.Add(new ColumnDefinition());grid.ColumnDefinitions.Add(new ColumnDefinition());
        var left=new StackPanel{Margin=new Thickness(0,0,6,0)};left.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_63FDD980CF43"),12));
        var format=new ComboBox{Tag="track-format",ItemsSource=new[]{"MP3","WAV","FLAC"},SelectedIndex=0};left.Children.Add(format);grid.Children.Add(left);
        var right=new StackPanel{Margin=new Thickness(6,0,0,0)};right.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_6DA541E41F56"),12));
        var bitrate=new ComboBox{Tag="track-bitrate",ItemsSource=new[]{64,96,128,160,192,256,320},SelectedItem=192};right.Children.Add(bitrate);Grid.SetColumn(right,1);grid.Children.Add(right);_panelContent.Children.Add(grid);
        var help=Text("",11);_panelContent.Children.Add(help);
        void UpdateFormat(){bool mp3=format.SelectedIndex==0;bitrate.IsEnabled=mp3;help.Text=mp3
            ?global::Orbit.Core.Localization.L.Get("Text_24EFFCEF5126")
            :global::Orbit.Core.Localization.L.Get("Text_43899A1930C1");}
        format.SelectionChanged+=(_,_)=>UpdateFormat();UpdateFormat();
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_2294B62BB65C"),11));
        var run=Btn(global::Orbit.Core.Localization.L.Get("Text_61C01DE06E49"),(_,_)=>{
            if(loadVersion!=_loadVersion||!CheckSingleMedia()||!string.Equals(_files[0],catalog.Path,StringComparison.OrdinalIgnoreCase)){
                Toast(global::Orbit.Core.Localization.L.Get("Text_80E43C40E9D0"));return;
            }
            if(track.SelectedItem is not TrackChoice selected)return;
            QueueEditing(new ImageJobOptions{Action=ImageAction.AudioTrackExtract,OutputDirectory=_outputDirectory,
                AudioTrackStreamIndex=selected.Track.StreamIndex,AudioTrackSourceSha256=catalog.SourceSha256,
                AudioTrackFormat=(string)format.SelectedItem,AudioTrackBitrateKbps=(int)bitrate.SelectedItem});
        });GlassAccent(run);_panelContent.Children.Add(run);
        var streamDetails=new StackPanel();foreach(var stream in catalog.Streams)AddTrackDetails(streamDetails,stream);
        _panelContent.Children.Add(new Expander{Header=global::Orbit.Core.Localization.L.Get("Text_C6912F46AED0"),Content=streamDetails,Margin=new Thickness(0,7,0,3)});
    }
    private void ShowAudioCover()
    {
        if(!CheckMediaSelection(ToolkitKind.Audio))return;
        if(_selectionMetadata.Any(m=>m.Bytes is <=0 or >2L*1024*1024*1024||!double.IsFinite(m.DurationSeconds)||m.DurationSeconds is <=0 or >86400)){
            Toast(global::Orbit.Core.Localization.L.Get("Text_3726E0D451B0"));return;
        }
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_E903BD6CE24C"),_files.Count==1?Path.GetFileName(_files[0]):global::Orbit.Core.Localization.L.Format("Text_39E4437FE988",(object?)(_files.Count)));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_839DA545F405"),13));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_9D75AE624DB4"),12));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_858E94B8CCC9"),11));
        int version=_loadVersion;var paths=_files.ToArray();
        var run=Btn(global::Orbit.Core.Localization.L.Get("Text_E287EADF4A68"),(_,_)=>{
            if(version!=_loadVersion||!CheckMediaSelection(ToolkitKind.Audio)||!paths.SequenceEqual(_files)){
                Toast(global::Orbit.Core.Localization.L.Get("Text_4E5758BA6091"));return;
            }
            QueueEditing(new ImageJobOptions{Action=ImageAction.AudioCover,OutputDirectory=_outputDirectory});
        });GlassAccent(run);_panelContent.Children.Add(run);
    }
}
