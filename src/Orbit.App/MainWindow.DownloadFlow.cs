using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Orbit.App.History;
using Orbit.Core;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private JobEntry? _focusedDownloadJob;
    private JobEntry? _resumeDownloadWorkspaceJob;
    private int _downloadPanelVersion=-1;
    private bool _downloadEditorTransitioning;
    private CancellationTokenSource? _downloadEditorCancellation;
    private Task _downloadEditorTask=Task.CompletedTask;
    private TextBlock? _downloadStatusText,_downloadPercentText,_downloadInfoText,_downloadErrorText;
    private ProgressBar? _downloadProgressBar;
    private Button? _downloadCancelButton,_downloadRetryButton,_downloadResumeButton;
    private Func<string,CancellationToken,Task<ToolkitMetadata>>? _downloadEditorInspectorOverride;

    private bool IsDownloadViewCurrent(JobEntry job,int? version=null)=>!_closing&&IsVisible&&WindowState!=WindowState.Minimized
        &&ReferenceEquals(_focusedDownloadJob,job)&&_downloadPanelVersion==_comparisonVersion
        &&(version is null||version==_comparisonVersion)&&_panel.Visibility==Visibility.Visible;

    private void StopDownloadPresentation()
    {
        _downloadEditorCancellation?.Cancel();_downloadEditorCancellation=null;
        _focusedDownloadJob=null;_downloadPanelVersion=-1;_downloadEditorTransitioning=false;
        _downloadStatusText=null;_downloadPercentText=null;_downloadInfoText=null;_downloadErrorText=null;
        _downloadProgressBar=null;_downloadCancelButton=null;_downloadRetryButton=null;_downloadResumeButton=null;
    }
    private void PreserveDownloadWorkspaceForHide()=>_resumeDownloadWorkspaceJob=_focusedDownloadJob is{} job&&IsDownloadViewCurrent(job)?job:null;
    private void ResumeDownloadWorkspace()
    {
        var job=_resumeDownloadWorkspaceJob;_resumeDownloadWorkspaceJob=null;
        if(job is not null&&!_closing)ShowDownloadJob(job);
    }

    private void ShowDownloadJob(JobEntry job)
    {
        if(_closing)return;
        bool download=job.Download is not null;
        OpenPanel(download?global::Orbit.Core.Localization.L.Get("Text_24F7AAD249B9"):global::Orbit.Core.Localization.L.Get("Text_69024748C0A9"),download?JobCaption(job):ActionName(job.Options.Action));
        _focusedDownloadJob=job;_downloadPanelVersion=_comparisonVersion;
        int version=_comparisonVersion;
        bool Current()=>IsDownloadViewCurrent(job,version);
        var card=new StackPanel();
        var name=Text(job.Result is{} result?Path.GetFileName(result.OutputPath):download?global::Orbit.Core.Localization.L.Get("Text_1374256A5B55"):Path.GetFileName(job.Path),17);
        name.FontWeight=FontWeights.SemiBold;name.TextTrimming=TextTrimming.CharacterEllipsis;name.MaxHeight=48;name.ToolTip=job.Download?.Url??job.Path;card.Children.Add(name);
        var statusRow=new Grid{Margin=new Thickness(0,14,0,8)};statusRow.ColumnDefinitions.Add(new ColumnDefinition());statusRow.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        _downloadStatusText=Text(global::Orbit.Core.Localization.L.Get("Text_B22A2454BD69"),13);_downloadStatusText.Tag="DownloadStatus";_downloadStatusText.Margin=new Thickness(0);_downloadStatusText.TextTrimming=TextTrimming.CharacterEllipsis;
        _downloadPercentText=Text("",13);_downloadPercentText.Tag="DownloadPercent";_downloadPercentText.Margin=new Thickness(12,0,0,0);
        statusRow.Children.Add(_downloadStatusText);Grid.SetColumn(_downloadPercentText,1);statusRow.Children.Add(_downloadPercentText);card.Children.Add(statusRow);
        _downloadProgressBar=new ProgressBar{Tag="DownloadProgress",Height=6,Minimum=0,Maximum=1,BorderThickness=new Thickness(0),Margin=new Thickness(0,0,0,9)};
        _downloadProgressBar.SetResourceReference(ProgressBar.ForegroundProperty,"GlassFocusEdge");_downloadProgressBar.SetResourceReference(ProgressBar.BackgroundProperty,"GlassFieldFill");card.Children.Add(_downloadProgressBar);
        _downloadInfoText=Text("",11);_downloadInfoText.Tag="DownloadTransferInfo";card.Children.Add(_downloadInfoText);
        var destination=job.Download?.OutputDirectory??job.Options.OutputDirectory??Path.GetDirectoryName(job.Path)??"";
        var folder=Text(global::Orbit.Core.Localization.L.Get("Text_F36A643BC794")+Path.GetFileName(Path.TrimEndingDirectorySeparator(destination)),11);folder.ToolTip=destination;folder.TextTrimming=TextTrimming.CharacterEllipsis;card.Children.Add(folder);
        var frame=new Border{Child=card,Padding=new Thickness(16),CornerRadius=new CornerRadius(10),BorderThickness=new Thickness(1),Margin=new Thickness(0,16,0,10)};
        frame.SetResourceReference(Border.BackgroundProperty,"GlassControlFill");frame.SetResourceReference(Border.BorderBrushProperty,"GlassEdge");_panelContent.Children.Add(frame);
        _downloadErrorText=Text("",12);_downloadErrorText.Tag="DownloadProblem";_downloadErrorText.Visibility=Visibility.Collapsed;_panelContent.Children.Add(_downloadErrorText);
        _downloadCancelButton=Btn(download?global::Orbit.Core.Localization.L.Get("Text_D60EA58EC4DD"):global::Orbit.Core.Localization.L.Get("Text_F7CF8F37ACEB"),(_,_)=>{if(!Current()||job.Finished||job.Result is not null||job.Cancellation.IsCancellationRequested)return;job.State=global::Orbit.Core.Localization.L.Get("Text_CCF5E8C11886");job.Cancellation.Cancel();UpdateJobsButton();RefreshJobs();});
        _downloadCancelButton.Tag="DownloadCancel";_panelContent.Children.Add(_downloadCancelButton);
        _downloadResumeButton=Btn(global::Orbit.Core.Localization.L.Get("Text_67F9AD62344E"),(_,_)=>{if(Current())SetQueuePaused(false);});_downloadResumeButton.Tag="DownloadResume";GlassAccent(_downloadResumeButton);_panelContent.Children.Add(_downloadResumeButton);
        _downloadRetryButton=Btn(global::Orbit.Core.Localization.L.Get("Text_5189135A6110"),(_,_)=>{
            if(!Current()||!job.Finished)return;
            if(job.HistoryStatus==HistoryStatus.Succeeded&&job.Result is not null){_downloadEditorTransitioning=false;_downloadErrorText!.Visibility=Visibility.Collapsed;RefreshDownloadView();}
            else if(job.Download is{} request)ShowDownloadPanel(request.Mode,request.Url);
            else EnqueueDownloadEdit(job.Path,job.Options);
        });_downloadRetryButton.Tag="DownloadRetry";_panelContent.Children.Add(_downloadRetryButton);
        var links=new Grid{Margin=new Thickness(0,10,0,0)};links.ColumnDefinitions.Add(new ColumnDefinition());links.ColumnDefinitions.Add(new ColumnDefinition());
        var history=Btn(global::Orbit.Core.Localization.L.Get("Text_942B2F2ED9ED"),(_,_)=>{if(Current())ShowJobs();});history.Tag="DownloadHistory";history.ToolTip=global::Orbit.Core.Localization.L.Get("Text_790B13DF7F51");
        var next=Btn(global::Orbit.Core.Localization.L.Get("Text_3C53B794D394"),(_,_)=>{if(Current())ShowDownloadPanel();});next.Tag="DownloadNew";
        foreach(var button in new[]{history,next}){button.FontSize=12;button.Padding=new Thickness(5,9,5,9);links.Children.Add(button);}Grid.SetColumn(next,1);_panelContent.Children.Add(links);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_54409684263B"),12));
        RefreshDownloadView();
    }

    private void RefreshDownloadView()
    {
        var job=_focusedDownloadJob;
        if(job is null||!IsDownloadViewCurrent(job)||_downloadStatusText is null)return;
        bool complete=job.Finished&&job.HistoryStatus==HistoryStatus.Succeeded&&job.Result is not null;
        bool waiting=job.HistoryStatus==HistoryStatus.Queued&&!job.Finished;
        _downloadStatusText.Text=complete?global::Orbit.Core.Localization.L.Get("Text_DACAC83A3F42"):job.State;
        _downloadPercentText!.Text=complete?"100%":job.Progress is{} p?$"{p:P0}":waiting?"":"…";
        _downloadProgressBar!.Value=complete?1:job.Progress??0;
        _downloadProgressBar.IsIndeterminate=!job.Finished&&!waiting&&job.Progress is null;
        _downloadCancelButton!.Visibility=job.Finished?Visibility.Collapsed:Visibility.Visible;
        _downloadCancelButton.IsEnabled=job.Result is null&&!job.Cancellation.IsCancellationRequested;
        _downloadResumeButton!.Visibility=waiting&&_queueRunControl.IsPaused?Visibility.Visible:Visibility.Collapsed;
        _downloadRetryButton!.Visibility=job.Finished&&!complete?Visibility.Visible:Visibility.Collapsed;
        var info=new System.Collections.Generic.List<string>();
        if(!job.Finished&&job.DownloadSpeed is >0 and{} speed)info.Add(FormatBytes((long)Math.Min(speed,long.MaxValue))+global::Orbit.Core.Localization.L.Get("Text_E875245583C8"));
        if(!job.Finished&&job.DownloadEta is >=0 and{} eta&&double.IsFinite(eta))info.Add(global::Orbit.Core.Localization.L.Get("Text_6FF275F3CB54")+TimeSpan.FromSeconds(Math.Min(eta,86400)).ToString(eta>=3600?@"h\:mm\:ss":@"m\:ss"));
        if(waiting)info.Add(_queueRunControl.IsPaused?global::Orbit.Core.Localization.L.Get("Text_3411ABE40069"):global::Orbit.Core.Localization.L.Get("Text_9D89AB5CDBAC"));
        _downloadInfoText!.Text=string.Join(" · ",info);
        if(job.Finished&&!complete){_downloadErrorText!.Text=job.Detail;_downloadErrorText.Visibility=Visibility.Visible;}
        if(complete&&!_downloadEditorTransitioning){
            _downloadEditorTransitioning=true;
            var cancellation=CancellationTokenSource.CreateLinkedTokenSource(_resultToolsCancellation.Token);_downloadEditorCancellation=cancellation;
            _downloadEditorTask=TrackResultTask(LoadDownloadEditorAsync(job,_downloadPanelVersion,cancellation));
        }
    }

    private async Task LoadDownloadEditorAsync(JobEntry job,int version,CancellationTokenSource cancellation)
    {
        try{
            await Task.Yield();
            var path=job.Result!.OutputPath;
            var metadata=IsDiagnosticRun&&_downloadEditorInspectorOverride is{} inspect?await inspect(path,cancellation.Token):await InspectSelectionAsync(path,cancellation.Token);
            if(cancellation.IsCancellationRequested||!IsDownloadViewCurrent(job,version))return;
            if(metadata.Kind is not(ToolkitKind.Audio or ToolkitKind.Video)||!double.IsFinite(metadata.DurationSeconds)
                ||metadata.DurationSeconds is <=0 or >86400
                ||!string.Equals(Path.GetFullPath(path),Path.GetFullPath(metadata.Path),StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(global::Orbit.Core.Localization.L.Get("Text_56D51223C555"));
            BuildDownloadedMediaEditor(job,metadata);
            _focusedDownloadJob=job;_downloadPanelVersion=_comparisonVersion;
        }catch(OperationCanceledException){ }
        catch(Exception ex){
            if(!cancellation.IsCancellationRequested&&IsDownloadViewCurrent(job,version)){
                _downloadStatusText!.Text=global::Orbit.Core.Localization.L.Get("Text_43985E59A3FF");_downloadErrorText!.Text=global::Orbit.Core.Localization.L.Get("Text_15972BFDED27")+ex.Message;_downloadErrorText.Visibility=Visibility.Visible;
                _downloadRetryButton!.Content=global::Orbit.Core.Localization.L.Get("Text_2749296A4C42");_downloadRetryButton.Visibility=Visibility.Visible;
                var actions=new WrapPanel();AddResultToolButtons(actions,job);actions.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_A99564C5702F"),async(_,_)=>{if(IsDownloadViewCurrent(job,version))await OpenKnownResultAsync(job.Result!.OutputPath,true);}));_panelContent.Children.Add(actions);
            }
        }finally{if(ReferenceEquals(_downloadEditorCancellation,cancellation))_downloadEditorCancellation=null;cancellation.Dispose();}
    }

    private JobEntry EnqueueDownloadEdit(string path,ImageJobOptions options)
    {
        var input=Path.GetFullPath(path);
        var actual=options with{OutputDirectory=options.OutputDirectory??_outputDirectory??Path.GetDirectoryName(input)};
        var job=new JobEntry(input,actual){InputPaths=new[]{input},DownloadWorkspaceEdit=true,PopupSession=_compact?_popupSession:null,State=_queueRunControl.IsPaused?global::Orbit.Core.Localization.L.Get("Text_AA8D8A7478F9"):global::Orbit.Core.Localization.L.Get("Text_B22A2454BD69")};
        if(_closing)return job;
        _jobs.Add(job);ShowRadialJob(job);_jobTasks.RemoveAll(task=>task.IsCompleted);ScheduleHistorySave();UpdateJobsButton();_jobTasks.Add(RunJobAsync(job));RefreshJobs();return job;
    }

    private async Task OpenDownloadedFileToolsAsync(JobEntry job)
    {
        if(job.Result is null||_closing)return;
        string path=job.Result.OutputPath;
        var loading=LoadFilesAsync(new[]{path},true);int requested=_loadVersion;
        if(await loading&&!_closing&&requested==_loadVersion&&_files.Count==1&&string.Equals(_files[0],path,StringComparison.OrdinalIgnoreCase))ShowMoreTools();
    }
}
