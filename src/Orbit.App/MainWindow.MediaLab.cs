using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Orbit.App.Controls;
using Orbit.Core;
using Orbit.Core.Media;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _previewBuildCancellation;
    private CancellationTokenSource? _loudnessInspection;
    private MediaPreviewResult? _mediaPreviewResult;
    private MediaPreviewPane? _mediaPreviewPane;
    private Border? _mediaPreviewHost;
    private TextBlock? _mediaPreviewStatus;
    private Button? _mediaPreviewBuildButton;
    private string? _previewTemporaryRoot;
    private int _previewGeneration;

    private void StopMediaLab(){_loudnessInspection?.Cancel();StopMediaPreview();_mediaPreviewHost=null;_mediaPreviewStatus=null;_mediaPreviewBuildButton=null;}
    private void StopMediaPreview()
    {
        ++_previewGeneration;_previewBuildCancellation?.Cancel();
        if(_mediaPreviewBuildButton!=null)_mediaPreviewBuildButton.IsEnabled=true;
        _mediaPreviewPane?.Dispose();_mediaPreviewPane=null;
        if(_mediaPreviewHost!=null)_mediaPreviewHost.Child=null;
        var result=_mediaPreviewResult;_mediaPreviewResult=null;
        if(result!=null)_=TrackResultTask(result.DisposeAsync().AsTask());
    }
    private void InvalidateMediaPreview()
    {
        if(_mediaPreviewPane==null&&_previewBuildCancellation==null)return;
        StopMediaPreview();if(_mediaPreviewStatus!=null)_mediaPreviewStatus.Text=global::Orbit.Core.Localization.L.Get("Text_53D08964842F");
    }
    private void AddFragmentPreview(string path,Func<(bool Valid,double From,double To)> readRange)
    {
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_B43724619674"),13));
        var status=Text(global::Orbit.Core.Localization.L.Get("Text_1F898E400C7D"),11);
        _mediaPreviewStatus=status;_panelContent.Children.Add(status);
        var host=new Border{Margin=new Thickness(0,7,0,4)};_mediaPreviewHost=host;
        var build=new Button{Content=global::Orbit.Core.Localization.L.Get("Text_581A11BEDD85"),Margin=new Thickness(3,0,3,0)};
        _mediaPreviewBuildButton=build;
        build.Click+=(_,_)=>{
            var range=readRange();if(!range.Valid){Toast(global::Orbit.Core.Localization.L.Get("Text_1137D381EF6D"));return;}
            StopMediaPreview();var cancellation=CancellationTokenSource.CreateLinkedTokenSource(_resultToolsCancellation.Token);_previewBuildCancellation=cancellation;
            int generation=_previewGeneration;int version=_comparisonVersion;
            status.Text=global::Orbit.Core.Localization.L.Get("Text_5FFBB42070BB");build.IsEnabled=false;
            _=TrackResultTask(BuildFragmentPreviewAsync(path,range.From,Math.Min(30,range.To-range.From),version,generation,cancellation,host,status,build));
        };
        _panelContent.Children.Add(build);_panelContent.Children.Add(host);
    }
    private async Task BuildFragmentPreviewAsync(string path,double start,double duration,int version,int generation,CancellationTokenSource cancellation,
        Border host,TextBlock status,Button build)
    {
        MediaPreviewResult? prepared=null;
        try{
            prepared=await new MediaPreviewEngine().BuildAsync(path,new MediaPreviewOptions{StartSeconds=start,DurationSeconds=duration,OutputDirectory=_previewTemporaryRoot},cancellation.Token);
            if(_closing||cancellation.IsCancellationRequested||version!=_comparisonVersion||generation!=_previewGeneration)return;
            var pane=new MediaPreviewPane(prepared.Path,prepared.IsVideo,prepared.DurationSeconds,mute:IsDiagnosticRun);
            _mediaPreviewPane=pane;_mediaPreviewResult=prepared;prepared=null;host.Child=pane;
            status.Text=global::Orbit.Core.Localization.L.Get("Text_EDFC3C11FB7B")+duration.ToString("0.###")+global::Orbit.Core.Localization.L.Get("Text_010FF41CE17D")+start.ToString("0.###")+global::Orbit.Core.Localization.L.Get("Text_587D3E2EFC3F");
            _=Dispatcher.InvokeAsync(()=>{if(!_closing&&version==_comparisonVersion&&generation==_previewGeneration)pane.BringIntoView();},System.Windows.Threading.DispatcherPriority.Loaded);
        }catch(OperationCanceledException){}
        catch(Exception ex){if(!_closing&&version==_comparisonVersion&&generation==_previewGeneration){status.Text=global::Orbit.Core.Localization.L.Get("Text_D77C0F96707A")+ex.Message;Toast(ex.Message);}}
        finally{
            if(prepared!=null)await prepared.DisposeAsync();
            if(ReferenceEquals(_previewBuildCancellation,cancellation))_previewBuildCancellation=null;
            cancellation.Dispose();if(!_closing&&version==_comparisonVersion&&generation==_previewGeneration)build.IsEnabled=true;
        }
    }
    private void ShowAudioNormalize()
    {
        if(!CheckMediaSelection(ToolkitKind.Audio))return;
        if(_selectionMetadata.Any(m=>m.DurationSeconds<3)){Toast(global::Orbit.Core.Localization.L.Get("Text_C39A2952691E"));return;}
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_9E34ACAA5368"),_files.Count==1?Path.GetFileName(_files[0]):global::Orbit.Core.Localization.L.Format("Text_938510B402FF",(object?)(_files.Count)));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_B9D67DAD6F00"),12));
        var target=new ComboBox{ItemsSource=new double[]{-24,-23,-20,-18,-16,-14,-12,-9},SelectedItem=-16d,Margin=new Thickness(0,2,0,6)};_panelContent.Children.Add(target);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_817485D64893"),12));
        var peak=new ComboBox{ItemsSource=new double[]{-3,-2,-1,-0.5,-0.1},SelectedItem=-1d,Margin=new Thickness(0,2,0,6)};_panelContent.Children.Add(peak);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_7C86ED103911"),12));
        var lra=new ComboBox{ItemsSource=new double[]{1,3,5,7,11,15,20},SelectedItem=11d,Margin=new Thickness(0,2,0,6)};_panelContent.Children.Add(lra);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_B99E860395F8"),11));
        var measurement=Text(global::Orbit.Core.Localization.L.Get("Text_A95B9DDEF699"),12);_panelContent.Children.Add(measurement);
        var measure=Btn(global::Orbit.Core.Localization.L.Get("Text_34D4896C50EF"),(_,_)=>{});_panelContent.Children.Add(measure);
        measure.Click+=(_,_)=>{
            if(_selectionLoading||_files.Count==0)return;_loudnessInspection?.Cancel();
            var cancellation=CancellationTokenSource.CreateLinkedTokenSource(_resultToolsCancellation.Token);_loudnessInspection=cancellation;
            int version=_comparisonVersion;measure.IsEnabled=false;measurement.Text=global::Orbit.Core.Localization.L.Get("Text_BB87B7CC5121");
            _=TrackResultTask(MeasureLoudnessAsync(_files[0],version,cancellation,measure,measurement));
        };
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_64B5C5231F9C"),11));
        var run=Btn(global::Orbit.Core.Localization.L.Get("Text_A5D1A28854ED"),(_,_)=>{
            if(!CheckMediaSelection(ToolkitKind.Audio))return;
            QueueEditing(new ImageJobOptions{Action=ImageAction.AudioNormalize,OutputDirectory=_outputDirectory,
                NormalizeTargetLufs=(double)target.SelectedItem,NormalizeTruePeakDb=(double)peak.SelectedItem,NormalizeLoudnessRange=(double)lra.SelectedItem});
        });GlassAccent(run);_panelContent.Children.Add(run);
    }
    private async Task MeasureLoudnessAsync(string path,int version,CancellationTokenSource cancellation,Button button,TextBlock status)
    {
        try{
            var measured=await new AudioNormalizeEngine().AnalyzeAsync(path,cancellation.Token);
            if(!_closing&&!cancellation.IsCancellationRequested&&version==_comparisonVersion)
                status.Text=global::Orbit.Core.Localization.L.Format("Text_FFD72A41BC28",(object?)(measured.IntegratedLufs),(object?)(measured.TruePeakDb),(object?)(measured.LoudnessRange));
        }catch(OperationCanceledException){}
        catch(Exception ex){if(!_closing&&version==_comparisonVersion)status.Text=global::Orbit.Core.Localization.L.Get("Text_21DEBD5AD9ED")+ex.Message;}
        finally{
            if(ReferenceEquals(_loudnessInspection,cancellation))_loudnessInspection=null;
            cancellation.Dispose();if(!_closing&&version==_comparisonVersion)button.IsEnabled=true;
        }
    }
}
