using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Orbit.Core;
using Orbit.Core.Media;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _audioTagsInspection;
    private void StopAudioTagsInspection()=>_audioTagsInspection?.Cancel();
    private bool CheckAudioTagSelection()
    {
        if(!CheckMediaSelection(ToolkitKind.Audio))return false;
        if(_selectionMetadata.Any(m=>m.Bytes is <=0 or >2L*1024*1024*1024||!double.IsFinite(m.DurationSeconds)||m.DurationSeconds is <=0 or >86400)
            ||_files.Any(p=>Path.GetExtension(p).ToLowerInvariant() is not(".mp3" or ".flac" or ".m4a"))){
            Toast(global::Orbit.Core.Localization.L.Get("Text_19FADD369576"));return false;
        }
        return true;
    }
    private void ShowAudioTags()
    {
        if(!CheckAudioTagSelection())return;
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_A2732561BC0E"),_files.Count==1?Path.GetFileName(_files[0]):global::Orbit.Core.Localization.L.Format("Text_39E4437FE988",(object?)(_files.Count)));
        int panelVersion=_comparisonVersion,loadVersion=_loadVersion;var paths=_files.ToArray();
        if(paths.Length>1){RenderAudioTags(null,paths,loadVersion,panelVersion);return;}
        var loading=Text(global::Orbit.Core.Localization.L.Get("Text_202A7E269B3E"),13);_panelContent.Children.Add(loading);
        var cancellation=CancellationTokenSource.CreateLinkedTokenSource(_resultToolsCancellation.Token);_audioTagsInspection=cancellation;
        _=TrackResultTask(LoadAsync());
        async Task LoadAsync(){try{
            var metadata=await new AudioTagsEngine().InspectAsync(paths[0],cancellation.Token);
            if(_closing||cancellation.IsCancellationRequested||panelVersion!=_comparisonVersion||loadVersion!=_loadVersion)return;
            _panelContent.Children.Remove(loading);RenderAudioTags(metadata,paths,loadVersion,panelVersion);
        }catch(OperationCanceledException){}
        catch(Exception ex){if(!_closing&&panelVersion==_comparisonVersion)loading.Text=global::Orbit.Core.Localization.L.Get("Text_38398C833B63")+ex.Message;}
        finally{if(ReferenceEquals(_audioTagsInspection,cancellation))_audioTagsInspection=null;cancellation.Dispose();}}
    }
    private void RenderAudioTags(AudioTagMetadata? metadata,string[] paths,int loadVersion,int panelVersion)
    {
        _panelContent.Children.Add(Text(paths.Length>1?global::Orbit.Core.Localization.L.Get("Text_06F9D053310B"):global::Orbit.Core.Localization.L.Get("Text_1EE7553DB458"),12));
        (CheckBox Toggle,TextBox Value) AddField(string label,string tag,string value){
            var toggle=new CheckBox{Content=label,Tag="tag-set-"+tag,IsChecked=false,Margin=new Thickness(0,5,0,2)};_panelContent.Children.Add(toggle);
            var input=new TextBox{Tag="tag-"+tag,Text=value,MaxLength=512,IsEnabled=false,Margin=new Thickness(0,2,0,6)};_panelContent.Children.Add(input);
            return(toggle,input);
        }
        var title=AddField(global::Orbit.Core.Localization.L.Get("Text_0918B4BA9268"),"title",metadata?.Title??"");
        var artist=AddField(global::Orbit.Core.Localization.L.Get("Text_037174D58809"),"artist",metadata?.Artist??"");
        var album=AddField(global::Orbit.Core.Localization.L.Get("Text_0928B8D4C742"),"album",metadata?.Album??"");
        var status=Text("",11);_panelContent.Children.Add(status);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_581EB76574EE"),11));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_83E647199FD7"),11));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_D9110DB6FA9F"),11));
        var run=Btn(global::Orbit.Core.Localization.L.Get("Text_E51076A3E7C3"),(_,_)=>{});GlassAccent(run);_panelContent.Children.Add(run);
        bool Read(out ImageJobOptions options){
            bool t=title.Toggle.IsChecked==true,a=artist.Toggle.IsChecked==true,l=album.Toggle.IsChecked==true;
            options=new ImageJobOptions{Action=ImageAction.AudioTags,OutputDirectory=_outputDirectory,
                AudioTagSetTitle=t,AudioTagTitle=t?title.Value.Text:"",AudioTagSetArtist=a,AudioTagArtist=a?artist.Value.Text:"",AudioTagSetAlbum=l,AudioTagAlbum=l?album.Value.Text:""};
            return(t||a||l)&&(!t||ValidAudioTagText(title.Value.Text))&&(!a||ValidAudioTagText(artist.Value.Text))&&(!l||ValidAudioTagText(album.Value.Text));
        }
        void Update(){
            title.Value.IsEnabled=title.Toggle.IsChecked==true;artist.Value.IsEnabled=artist.Toggle.IsChecked==true;album.Value.IsEnabled=album.Toggle.IsChecked==true;
            bool any=title.Toggle.IsChecked==true||artist.Toggle.IsChecked==true||album.Toggle.IsChecked==true;
            run.IsEnabled=Read(out _);status.Text=!any?global::Orbit.Core.Localization.L.Get("Text_B61CDE5A9F23"):run.IsEnabled?global::Orbit.Core.Localization.L.Get("Text_9D886C04B5FF"):global::Orbit.Core.Localization.L.Get("Text_B98E3B30CCF3");
            status.Foreground=Ink(any&&!run.IsEnabled?"#EDACB9":"#BAC6DE");
        }
        run.Click+=(_,_)=>{
            if(panelVersion!=_comparisonVersion||loadVersion!=_loadVersion||!CheckAudioTagSelection()||!paths.SequenceEqual(_files)){
                Toast(global::Orbit.Core.Localization.L.Get("Text_20DD6CF4A3FB"));return;
            }
            if(!Read(out var options)){Update();return;}QueueEditing(options);
        };
        foreach(var field in new[]{title,artist,album}){field.Toggle.Checked+=(_,_)=>Update();field.Toggle.Unchecked+=(_,_)=>Update();field.Value.TextChanged+=(_,_)=>Update();}Update();
    }
    private static bool ValidAudioTagText(string value)
    {
        if(value.Length>512)return false;
        for(int i=0;i<value.Length;i++){
            if(char.IsControl(value[i])||value[i] is '\u2028' or '\u2029')return false;
            if(char.IsSurrogate(value[i])){if(!char.IsHighSurrogate(value[i])||i+1>=value.Length||!char.IsLowSurrogate(value[++i]))return false;}
        }
        return true;
    }
}
