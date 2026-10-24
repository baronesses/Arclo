using System;
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
    private CancellationTokenSource? _mediaInspectionCancellation;
    private bool CheckMediaSelection(ToolkitKind kind)
    {
        if(_selectionLoading||_files.Count==0||_selectionMetadata.Any(m=>m.Kind!=kind)){
            Toast(kind==ToolkitKind.Video?global::Orbit.Core.Localization.L.Get("Text_25E2FFFE56CF"):global::Orbit.Core.Localization.L.Get("Text_91B4B14A5352"));return false;
        }
        if(kind==ToolkitKind.Audio&&_selectionMetadata.Any(m=>m.Bytes>2L*1024*1024*1024||m.DurationSeconds>86400)){
            Toast(global::Orbit.Core.Localization.L.Get("Text_A41AFDB12AEF"));return false;
        }
        return true;
    }

    private void ShowVideoGif()
    {
        if(!CheckMediaSelection(ToolkitKind.Video))return;
        BeginMediaInspection(true,RenderVideoGif);
    }

    private void RenderVideoGif(double duration)
    {
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_7BA718210F9A")+MediaTimecode.Format(duration),12));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_11C66EFCD01C")));
        var start=new TextBox{Text="0",MaxLength=32,Margin=new Thickness(0,0,0,6)};_panelContent.Children.Add(start);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_0C5271BBB22E")));
        var end=new TextBox{Text=MediaTimecode.Format(Math.Min(3,duration)),MaxLength=32,Margin=new Thickness(0,0,0,6)};_panelContent.Children.Add(end);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_71C70A48C660")));
        var width=new ComboBox{ItemsSource=new[]{160,240,320,480,640,960,1280},SelectedItem=480,Margin=new Thickness(0,0,0,6)};_panelContent.Children.Add(width);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_31C42B0FC3EA")));
        var fps=new ComboBox{ItemsSource=new[]{6,10,12,15,20,24,30},SelectedItem=12,Margin=new Thickness(0,0,0,6)};_panelContent.Children.Add(fps);
        var interval=Text("",12);_panelContent.Children.Add(interval);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_9611753E3766"),11));
        var run=Btn(global::Orbit.Core.Localization.L.Get("Text_9DF062139F73"),(_,_)=>{});GlassAccent(run);run.Margin=new Thickness(0,8,0,0);_panelContent.Children.Add(run);
        bool Valid(out double from,out double to){
            bool a=MediaTimecode.TryParse(start.Text,out from),b=MediaTimecode.TryParse(end.Text,out to);
            return a&&b&&from<to&&to<=duration&&to-from<=30&&(to-from)*(int)fps.SelectedItem<=900;
        }
        void Update(){bool valid=Valid(out var from,out var to);run.IsEnabled=valid;interval.Text=valid
            ?global::Orbit.Core.Localization.L.Format("Text_88159557FC1E",(object?)(MediaTimecode.Format(to-from)),(object?)(Math.Ceiling((to-from)*(int)fps.SelectedItem)))
            :global::Orbit.Core.Localization.L.Get("Text_CA671EDFEAAC");}
        start.TextChanged+=(_,_)=>Update();end.TextChanged+=(_,_)=>Update();fps.SelectionChanged+=(_,_)=>Update();
        run.Click+=(_,_)=>{if(Valid(out var from,out var to))QueueEditing(new ImageJobOptions{Action=ImageAction.VideoGif,OutputDirectory=_outputDirectory,
            GifStartSeconds=from,GifDurationSeconds=to-from,GifWidth=(int)width.SelectedItem,GifFramesPerSecond=(int)fps.SelectedItem});else Update();};
        Update();
    }

    private void ShowVideoExport()
    {
        if(!CheckMediaSelection(ToolkitKind.Video))return;
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_3F01DBCFE9B5"),_files.Count==1?System.IO.Path.GetFileName(_files[0]):global::Orbit.Core.Localization.L.Format("Text_C9E7675CB569",(object?)(_files.Count)));
        var sizing=new Grid{Margin=new Thickness(0,0,0,6)};sizing.ColumnDefinitions.Add(new ColumnDefinition());sizing.ColumnDefinitions.Add(new ColumnDefinition());
        var widthColumn=new StackPanel{Margin=new Thickness(0,0,6,0)};widthColumn.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_44191BDBA86A"),12));
        var width=new ComboBox{Tag="video-width",ItemsSource=new[]{global::Orbit.Core.Localization.L.Get("Text_78330C067282"),"640 px","1280 px","1920 px","2560 px","3840 px"},SelectedIndex=0};widthColumn.Children.Add(width);sizing.Children.Add(widthColumn);
        var fpsColumn=new StackPanel{Margin=new Thickness(6,0,0,0)};fpsColumn.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_F92764CB0017"),12));
        var fps=new ComboBox{Tag="video-fps",ItemsSource=new[]{global::Orbit.Core.Localization.L.Get("Text_78330C067282"),"24 FPS","25 FPS","30 FPS","50 FPS","60 FPS"},SelectedIndex=0};fpsColumn.Children.Add(fps);Grid.SetColumn(fpsColumn,1);sizing.Children.Add(fpsColumn);_panelContent.Children.Add(sizing);
        var noUpscale=new CheckBox{Content=global::Orbit.Core.Localization.L.Get("Text_C1AC52D5B064"),IsChecked=true,Margin=new Thickness(0,4,0,7)};_panelContent.Children.Add(noUpscale);
        var mute=new CheckBox{Content=global::Orbit.Core.Localization.L.Get("Text_D17BE34C2A9E"),IsChecked=false,Margin=new Thickness(0,4,0,7)};_panelContent.Children.Add(mute);
        var crfLabel=Text("CRF: 24",12);_panelContent.Children.Add(crfLabel);
        var crf=new Slider{Minimum=0,Maximum=51,Value=24,TickFrequency=1,IsSnapToTickEnabled=true,Margin=new Thickness(2,5,2,9)};_panelContent.Children.Add(crf);
        crf.ValueChanged+=(_,_)=>crfLabel.Text=global::Orbit.Core.Localization.L.Format("Text_5F7307B12FE0",(object?)((int)crf.Value));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_58FF221C9BB4"),12));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_886A7E9E9023"),11));
        var run=Btn(global::Orbit.Core.Localization.L.Get("Text_7BC3E922382B"),(_,_)=>QueueEditing(new ImageJobOptions{Action=ImageAction.VideoExport,OutputDirectory=_outputDirectory,
            VideoExportWidth=width.SelectedIndex switch{1=>640,2=>1280,3=>1920,4=>2560,5=>3840,_=>null},VideoExportNoUpscale=noUpscale.IsChecked==true,
            VideoExportRemoveAudio=mute.IsChecked==true,VideoCrf=(int)crf.Value,
            VideoExportFramesPerSecond=fps.SelectedIndex switch{1=>24,2=>25,3=>30,4=>50,5=>60,_=>null}}));GlassAccent(run);run.Margin=new Thickness(0,9,0,0);_panelContent.Children.Add(run);
    }

    private void ShowAudioAdjust()
    {
        if(!CheckMediaSelection(ToolkitKind.Audio))return;
        BeginMediaInspection(false,duration=>RenderAudioAdjust(duration));
    }

    private void RenderAudioAdjust(double duration,ImageJobOptions? recipe=null)
    {
        var volumeLabel=Text(global::Orbit.Core.Localization.L.Format("Text_60330964DFCB",(object?)(recipe?.AudioVolumePercent??100)),14);_panelContent.Children.Add(volumeLabel);
        var volume=new Slider{Minimum=0,Maximum=200,Value=recipe?.AudioVolumePercent??100,TickFrequency=1,IsSnapToTickEnabled=true,Margin=new Thickness(2,4,2,9)};_panelContent.Children.Add(volume);
        var reset=Btn(global::Orbit.Core.Localization.L.Get("Text_CDDB821A1E5A"),(_,_)=>volume.Value=100);_panelContent.Children.Add(reset);
        volume.ValueChanged+=(_,_)=>volumeLabel.Text=global::Orbit.Core.Localization.L.Format("Text_77909F7F6805",(object?)((int)volume.Value));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_3F07AE7EDA03")));
        var fadeIn=new TextBox{Text=MediaTimecode.Format(recipe?.AudioFadeInSeconds??0),MaxLength=32,Margin=new Thickness(0,0,0,6)};_panelContent.Children.Add(fadeIn);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_0D7CD185E22E")));
        var fadeOut=new TextBox{Text=MediaTimecode.Format(recipe?.AudioFadeOutSeconds??0),MaxLength=32,Margin=new Thickness(0,0,0,6)};_panelContent.Children.Add(fadeOut);
        var info=Text("",12);_panelContent.Children.Add(info);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_748946F55975"),11));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_5448C10BB0A0"),11));
        var run=Btn(global::Orbit.Core.Localization.L.Get("Text_D4A534B1BB82"),(_,_)=>{});GlassAccent(run);run.Margin=new Thickness(0,8,0,0);_panelContent.Children.Add(run);
        bool Read(out double from,out double to){bool a=MediaTimecode.TryParse(fadeIn.Text,out from),b=MediaTimecode.TryParse(fadeOut.Text,out to);return a&&b&&from+to<=duration;}
        void Update(){bool valid=Read(out var from,out var to);run.IsEnabled=valid;info.Text=valid?global::Orbit.Core.Localization.L.Get("Text_1D2B4DB42344")+MediaTimecode.Format(duration):global::Orbit.Core.Localization.L.Get("Text_499D105BF5F2");}
        fadeIn.TextChanged+=(_,_)=>Update();fadeOut.TextChanged+=(_,_)=>Update();
        run.Click+=(_,_)=>{if(Read(out var from,out var to))QueueEditing(new ImageJobOptions{Action=ImageAction.AudioAdjust,OutputDirectory=_outputDirectory,
            AudioVolumePercent=volume.Value,AudioFadeInSeconds=from,AudioFadeOutSeconds=to});else Update();};Update();
    }

    private void ApplyAudioRecipe(ImageJobOptions options)
    {
        if(!CheckMediaSelection(ToolkitKind.Audio))return;
        BeginMediaInspection(false,duration=>{
            if(options.AudioFadeInSeconds+options.AudioFadeOutSeconds<=duration)QueueEditing(options);
            else{RenderAudioAdjust(duration,options);Toast(global::Orbit.Core.Localization.L.Get("Text_D642303BCA98"));}
        });
    }

    private void StopMediaInspection()=>_mediaInspectionCancellation?.Cancel();

    private void BeginMediaInspection(bool video,Action<double> render,string? title=null)
    {
        OpenPanel(title??(video?global::Orbit.Core.Localization.L.Get("Text_17447D4759DA"):global::Orbit.Core.Localization.L.Get("Text_0778DED80983")),_files.Count==1?System.IO.Path.GetFileName(_files[0]):global::Orbit.Core.Localization.L.Format("Text_FFB793B7F6C5",(object?)(_files.Count)));
        var loading=Text(video?global::Orbit.Core.Localization.L.Get("Text_B4B0D2150DB2"):global::Orbit.Core.Localization.L.Get("Text_545A5871C30B"),13);
        _panelContent.Children.Add(loading);
        var cancellation=CancellationTokenSource.CreateLinkedTokenSource(_resultToolsCancellation.Token);_mediaInspectionCancellation=cancellation;
        int version=_comparisonVersion;var paths=_files.ToArray();
        _=TrackResultTask(InspectMediaEditorAsync(paths,video,version,cancellation,loading,render));
    }

    private async Task InspectMediaEditorAsync(string[] paths,bool video,int version,CancellationTokenSource cancellation,TextBlock loading,Action<double> render)
    {
        try{
            var durations=new System.Collections.Generic.Dictionary<string,double>(StringComparer.OrdinalIgnoreCase);
            foreach(var path in paths){
                cancellation.Token.ThrowIfCancellationRequested();
                durations[path]=video?await new VideoGifEngine().GetVideoDurationAsync(path,cancellation.Token)
                    :await new AudioAdjustEngine().GetDecodedDurationAsync(path,cancellation.Token);
            }
            if(_closing||cancellation.IsCancellationRequested||version!=_comparisonVersion)return;
            if(durations.Values.Any(value=>!double.IsFinite(value)||value<=0))throw new InvalidOperationException(global::Orbit.Core.Localization.L.Get("Text_C15EEFE5B04A"));
            _selectionMetadata=_selectionMetadata.Select(m=>durations.TryGetValue(m.Path,out var value)?m with{DurationSeconds=value}:m).ToArray();
            if(_metadata!=null&&durations.TryGetValue(_metadata.Path,out var first))_metadata=_metadata with{DurationSeconds=first};
            while(_panelContent.Children.Count>2)_panelContent.Children.RemoveAt(2);
            render(durations.Values.Min());
        }catch(OperationCanceledException){}
        catch(Exception ex){if(!_closing&&version==_comparisonVersion){loading.Text=global::Orbit.Core.Localization.L.Get("Text_14B069E2DC14")+ex.Message;Toast(ex.Message);}}
        finally{if(ReferenceEquals(_mediaInspectionCancellation,cancellation))_mediaInspectionCancellation=null;cancellation.Dispose();}
    }
}
