using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Orbit.App.History;
using Orbit.Core.Jobs;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private readonly QueueRunControl _queueRunControl=new();
    private Func<CancellationToken,Task>? _queueExecutionBarrier;
    private TextBlock? _queueSummary;
    private Button? _queuePauseButton,_queueCancelWaitingButton,_queueCancelAllButton;
    private int _queueControlsVersion;

    private (int Running,int Waiting,int Cancelling) QueueCounts()
    {
        var live=_jobs.Where(j=>!j.Finished).ToArray();
        return(live.Count(j=>j.HistoryStatus==HistoryStatus.Running&&!j.Cancellation.IsCancellationRequested),
            live.Count(j=>j.HistoryStatus==HistoryStatus.Queued&&!j.Cancellation.IsCancellationRequested),
            live.Count(j=>j.Cancellation.IsCancellationRequested));
    }
    private void ToggleQueuePause()=>SetQueuePaused(!_queueRunControl.IsPaused);
    private void SetQueuePaused(bool paused)
    {
        Dispatcher.VerifyAccess();if(_closing)return;
        bool changed=paused?_queueRunControl.Pause():_queueRunControl.Resume();if(!changed)return;
        foreach(var job in _jobs.Where(j=>!j.Finished&&j.HistoryStatus==HistoryStatus.Queued&&!j.Cancellation.IsCancellationRequested))
            job.State=paused?global::Orbit.Core.Localization.L.Get("Text_AA8D8A7478F9"):global::Orbit.Core.Localization.L.Get("Text_B22A2454BD69");
        UpdateJobsButton();RefreshJobs();
    }
    private void CancelQueue(bool waitingOnly)
    {
        Dispatcher.VerifyAccess();if(_closing)return;
        var selected=_jobs.Where(j=>!j.Finished&&!j.Cancellation.IsCancellationRequested&&(!waitingOnly||j.HistoryStatus==HistoryStatus.Queued)).ToArray();
        foreach(var job in selected){job.State=global::Orbit.Core.Localization.L.Get("Text_CCF5E8C11886");job.Cancellation.Cancel();}
        UpdateJobsButton();RefreshJobs();
    }
    private void ShowJobsFromTray()
    {
        if(_closing)return;
        InvalidatePendingInvocation();Show();WindowState=WindowState.Normal;Activate();ShowJobs();
    }
    private void AddQueueControls()
    {
        _queueControlsVersion=_comparisonVersion;int version=_queueControlsVersion;
        bool Current()=>!_closing&&_jobsVisible&&version==_comparisonVersion;
        _queueSummary=Text("",12);_queueSummary.Tag="queue-summary";_panelContent.Children.Add(_queueSummary);
        _queuePauseButton=Btn("",(_,_)=>{if(Current())ToggleQueuePause();});_queuePauseButton.Tag="queue-pause";
        GlassAccent(_queuePauseButton);_queuePauseButton.Margin=new Thickness(0,6,0,5);_panelContent.Children.Add(_queuePauseButton);
        _queueCancelWaitingButton=Btn(global::Orbit.Core.Localization.L.Get("Text_D0DA9AE38812"),(_,_)=>{if(Current())CancelQueue(true);});_queueCancelWaitingButton.Tag="queue-cancel-waiting";
        _queueCancelAllButton=Btn(global::Orbit.Core.Localization.L.Get("Text_EC38C8E8F46E"),(_,_)=>{if(Current())CancelQueue(false);});_queueCancelAllButton.Tag="queue-cancel-all";
        var row=new Grid();row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition());
        foreach(var button in new[]{_queueCancelWaitingButton,_queueCancelAllButton}){button.FontSize=11;button.Padding=new Thickness(5,8,5,8);row.Children.Add(button);}
        Grid.SetColumn(_queueCancelAllButton,1);_panelContent.Children.Add(row);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_9F41CB814409"),10));
    }
    private void UpdateQueueControls()
    {
        var counts=QueueCounts();_bridge?.SetQueueState(_queueRunControl.IsPaused,_jobs.Count(j=>!j.Finished&&j.HistoryStatus==HistoryStatus.Running),counts.Waiting);
        if(_queueControlsVersion!=_comparisonVersion||_queueSummary is null)return;
        _queueSummary.Text=(_queueRunControl.IsPaused?global::Orbit.Core.Localization.L.Get("Text_3411ABE40069"):global::Orbit.Core.Localization.L.Get("Text_5C28B3DAC4AC"))+global::Orbit.Core.Localization.L.Format("Text_95662582613F",(object?)(counts.Running),(object?)(counts.Waiting))+(counts.Cancelling>0?global::Orbit.Core.Localization.L.Format("Text_352704A7BDB1",(object?)(counts.Cancelling)):"");
        _queuePauseButton!.Content=_queueRunControl.IsPaused?global::Orbit.Core.Localization.L.Get("Text_67F9AD62344E"):global::Orbit.Core.Localization.L.Get("Text_710DA0C0168D");
        _queuePauseButton.IsEnabled=!_closing;
        _queueCancelWaitingButton!.IsEnabled=counts.Waiting>0&&!_closing;
        _queueCancelWaitingButton.ToolTip=global::Orbit.Core.Localization.L.Format("Text_E08EB3AD77B5",(object?)(counts.Waiting));
        _queueCancelAllButton!.IsEnabled=counts.Waiting+counts.Running>0&&!_closing;
        _queueCancelAllButton.ToolTip=global::Orbit.Core.Localization.L.Format("Text_FEC6563D26B5",(object?)(counts.Waiting+counts.Running));
    }
}
