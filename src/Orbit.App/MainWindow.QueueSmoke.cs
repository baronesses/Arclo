using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Orbit.App.History;
using Orbit.App.Presets;
using Orbit.Core;
using Orbit.Core.Documents;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private async Task RunQueueSmokeAsync()
    {
        var destination=Path.Combine(AppContext.BaseDirectory,"queue-smoke");
        var run=Path.Combine(destination,"run-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        var checks=new List<string>();bool slotHeld=false;
        void Require(bool value,string text){if(!value)throw new InvalidOperationException("Queue integration: "+text);checks.Add(text);}
        Button Control(string tag)=>SystemsDescendants<Button>(_panelContent).Single(b=>b.Tag as string==tag);
        void Click(string tag)=>Control(tag).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        async Task DrainAsync(){await Task.WhenAll(_jobTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(45));await FlushHistoryAsync();}
        async Task YieldAsync()=>await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Background);
        async Task ShotAsync(string name){_toast.Visibility=Visibility.Collapsed;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);Capture(Path.Combine(destination,name));}
        try
        {
            Directory.CreateDirectory(run);File.Delete(Path.Combine(destination,"passed.json"));File.Delete(Path.Combine(destination,"error.txt"));
            Require(IsDiagnosticRun&&_bridge is null&&!ProductivityIoAllowed&&!PersonalizationIoAllowed&&Path.GetTempPath().StartsWith(AppContext.BaseDirectory,StringComparison.OrdinalIgnoreCase),"isolated mode has private temp, no user stores, tray, hotkey, clipboard or registry writes");
            var source=Path.Combine(run,"фото [очередь].png");WriteResultsImage(source);string hash=SystemsDigest(source);
            _historyStore=new HistoryStore(Path.Combine(run,"history.json"));_presetStore=new PresetStore(Path.Combine(run,"presets.json"));_allowDiagnosticProductivityIo=true;
            _outputDirectory=Path.Combine(run,"output");Directory.CreateDirectory(_outputDirectory);
            var options=new ImageJobOptions{Action=ImageAction.WebP,Quality=85,OutputDirectory=_outputDirectory};
            bool loaded=await LoadFilesAsync(new[]{source});Require(loaded,$"owned source loads; status={_status.Text}; toast={_toastText.Text}; version={_loadVersion}");
            SetCompactMode(true);ShowJobs();
            Require(!_queueRunControl.IsPaused&&QueueCounts()==(0,0,0),"new session starts unpaused with no live work");
            Require(!Control("queue-cancel-waiting").IsEnabled&&!Control("queue-cancel-all").IsEnabled,"empty queue has disabled cancellation controls");
            Click("queue-pause");Require(_queueRunControl.IsPaused&&Control("queue-pause").Content as string=="Продолжить очередь","real pause button changes the gate and exposes resume");
            Enqueue(source,options);Enqueue(source,options);Enqueue(source,options);
            using var secret=new SevenZipPassword("queue-owned-secret-2719");var pendingSecret=secret.Clone();
            Enqueue(source,new ImageJobOptions{Action=ImageAction.Archive7z,SevenZipPasswordProtected=true,SevenZipEncryptHeaders=true,OutputDirectory=_outputDirectory},password:pendingSecret);
            var firstBatch=_jobs.ToArray();await YieldAsync();
            Require(QueueCounts()==(0,4,0)&&firstBatch.All(j=>j.HistoryStatus==HistoryStatus.Queued&&!j.Finished&&j.State.Contains("на паузе",StringComparison.Ordinal)),"jobs added while paused stay queued without an active engine");
            Require(Directory.GetFiles(_outputDirectory).Length==0,"paused queue publishes no files");
            Require(SystemsDescendants<TextBlock>(_jobsCards!).Any(t=>t.Text.Contains("Ожидание · очередь на паузе",StringComparison.Ordinal)),"new paused jobs appear immediately in the visible list");
            await FlushHistoryAsync();var recovered=await new HistoryStore(_historyStore.FilePath).LoadAsync();
            Require(recovered.Error is null&&recovered.Entries.Count==4&&recovered.Entries.All(e=>e.Status==HistoryStatus.Interrupted),"paused queued history loads as interrupted instead of resuming work");
            Require(recovered.Entries.All(e=>e.Options.Action!=ImageAction.Archive7z||e.Options.SevenZipPasswordProtected),"paused protected task retains protection intent without a password");
            Require(!File.ReadAllText(_historyStore.FilePath).Contains("queue-owned-secret-2719",StringComparison.Ordinal),"paused and cancelled history never records the transient password");
            await ShotAsync("01-paused-queue.png");Click("queue-cancel-waiting");await DrainAsync();
            Require(firstBatch.All(j=>j.HistoryStatus==HistoryStatus.Cancelled&&j.Result is null&&j.Password is null),"mass cancellation drains paused work and clears each queued secret");
            bool disposed=false;try{using var copy=pendingSecret.Clone();}catch(ObjectDisposedException){disposed=true;}
            Require(disposed&&_queueRunControl.IsPaused&&Directory.GetFiles(_outputDirectory).Length==0,"cancel waiting disposes password storage without resuming or publishing");

            Click("queue-pause");var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _queueExecutionBarrier=ct=>{started.TrySetResult();return release.Task.WaitAsync(ct);};
            Enqueue(source,options);var active=_jobs.Last();await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var completedCancel=SystemsDescendants<Button>(_jobsCards!).First(b=>b.Content as string=="Отменить");
            Click("queue-pause");Enqueue(source,options);var waitingA=_jobs.Last();Enqueue(source,options);var waitingB=_jobs.Last();await YieldAsync();
            Require(active.HistoryStatus==HistoryStatus.Running&&!active.Cancellation.IsCancellationRequested&&QueueCounts()==(1,2,0),"pause leaves an admitted task running while later tasks wait");
            Require((_jobsCards!.Children.OfType<Border>().First().Child as StackPanel)!.Children.OfType<TextBlock>().Any(t=>t.Text.Contains("Обработка…",StringComparison.Ordinal)),"active work stays above queued jobs in the visible list");
            Click("queue-cancel-waiting");await YieldAsync();
            Require(!active.Cancellation.IsCancellationRequested&&waitingA.Cancellation.IsCancellationRequested&&waitingB.Cancellation.IsCancellationRequested,"cancel waiting targets pending work and leaves the active task alone");
            release.TrySetResult();await DrainAsync();_queueExecutionBarrier=null;
            Require(active.HistoryStatus==HistoryStatus.Succeeded&&active.Result?.Format=="WEBP"&&File.Exists(active.Result.OutputPath),"already admitted task completes a real native WebP despite queue pause");
            string readyState=active.State;completedCancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Require(active.State==readyState&&!active.Cancellation.IsCancellationRequested,"a late cancel click cannot rewrite an already completed result");
            Require(waitingA.HistoryStatus==HistoryStatus.Cancelled&&waitingB.HistoryStatus==HistoryStatus.Cancelled&&_queueRunControl.IsPaused,"cancelled pending jobs never run after active completion");
            Require(QueueCounts()==(0,0,0)&&Control("queue-pause").IsEnabled,"an empty paused queue can still be resumed");

            Enqueue(source,options);var rapid=_jobs.Last();Click("queue-pause");Click("queue-pause");await YieldAsync();
            Require(_queueRunControl.IsPaused&&rapid.HistoryStatus==HistoryStatus.Queued&&!rapid.Finished,"immediate resume then pause cannot admit a queued dispatcher continuation");
            Click("queue-pause");await DrainAsync();Require(rapid.HistoryStatus==HistoryStatus.Succeeded,"a second resume releases the waiting task exactly once");

            await _queueGate.WaitAsync();slotHeld=true;Enqueue(source,options);var behindSlot=_jobs.Last();SetQueuePaused(true);_queueGate.Release();slotHeld=false;await YieldAsync();
            Require(behindSlot.HistoryStatus==HistoryStatus.Queued&&!behindSlot.Finished,"a task waiting for the execution slot rechecks pause after acquiring it");
            SetQueuePaused(false);await DrainAsync();Require(behindSlot.HistoryStatus==HistoryStatus.Succeeded,"slot waiter proceeds after explicit resume");

            SetQueuePaused(true);_jobFilterPicker!.SelectedIndex=(int)JobListFilter.Succeeded;_jobSearchBox!.Text="no-visible-match";
            int before=_jobs.Count;for(int i=0;i<45;i++)Enqueue(source,options);var hidden=_jobs.Skip(before).ToArray();
            Require(!FilteredJobs().Any()&&QueueCounts().Waiting==45&&Control("queue-cancel-waiting").IsEnabled,"global counters and cancel controls include work hidden by filters and the 40-card cap");
            Click("queue-cancel-waiting");Enqueue(source,options);var afterSnapshot=_jobs.Last();await Task.WhenAll(_jobTasks.Where(t=>t.IsCompleted).ToArray());await YieldAsync();
            Require(hidden.All(j=>j.Cancellation.IsCancellationRequested)&&!afterSnapshot.Cancellation.IsCancellationRequested,"mass cancellation captures a snapshot and leaves newly added work intact");
            afterSnapshot.Cancellation.Cancel();await DrainAsync();Require(hidden.All(j=>j.HistoryStatus==HistoryStatus.Cancelled)&&QueueCounts()==(0,0,0),"all hidden queued jobs settle without waiting for Resume");
            _jobFilterPicker.SelectedIndex=0;_jobSearchBox.Text="";
            var detachedPause=Control("queue-pause");var detachedCancel=Control("queue-cancel-all");ShowMoreTools();Enqueue(source,options);var detachedJob=_jobs.Last();
            detachedPause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));detachedCancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await YieldAsync();
            Require(_queueRunControl.IsPaused&&!detachedJob.Cancellation.IsCancellationRequested&&detachedJob.HistoryStatus==HistoryStatus.Queued,"detached controls cannot change queue state after a different panel opens");
            ShowJobs();Click("queue-cancel-waiting");await DrainAsync();

            SetQueuePaused(false);started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _queueExecutionBarrier=ct=>{started.TrySetResult();return release.Task.WaitAsync(ct);};int filesBefore=Directory.GetFiles(_outputDirectory).Length;
            Enqueue(source,options);var cancelActive=_jobs.Last();await started.Task.WaitAsync(TimeSpan.FromSeconds(5));Enqueue(source,options);var cancelWaiting=_jobs.Last();SetQueuePaused(true);
            await ShotAsync("02-active-and-paused.png");Click("queue-cancel-all");await DrainAsync();_queueExecutionBarrier=null;
            Require(cancelActive.HistoryStatus==HistoryStatus.Cancelled&&cancelWaiting.HistoryStatus==HistoryStatus.Cancelled,"cancel all settles active and pending jobs while queue remains paused");
            Require(_queueRunControl.IsPaused&&Directory.GetFiles(_outputDirectory).Length==filesBefore,"cancel all keeps completed results and publishes no cancelled outputs");

            var vanishing=Path.Combine(run,"removed-before-start.png");File.Copy(source,vanishing);Enqueue(vanishing,options);var failed=_jobs.Last();Enqueue(source,options);var succeeds=_jobs.Last();File.Delete(vanishing);
            SetQueuePaused(false);await DrainAsync();Require(failed.HistoryStatus==HistoryStatus.Failed&&succeeds.HistoryStatus==HistoryStatus.Succeeded,"one missing queued source fails independently and later work continues");
            Require(_jobs.Where(j=>j.HistoryStatus==HistoryStatus.Succeeded).All(j=>j.Result is not null&&File.Exists(j.Result.OutputPath)),"previously completed output files survive every queue command");
            Require(SystemsDigest(source)==hash,"all operations leave the original photo byte-identical");
            Require(!Directory.GetDirectories(_outputDirectory).Any(p=>Path.GetFileName(p).StartsWith(".orbit-",StringComparison.Ordinal)),"cancelled and successful queue jobs clean private output staging");
            SetQueuePaused(true);Enqueue(source,options);var onExit=_jobs.Last();await FlushHistoryAsync();
            Require(onExit.HistoryStatus==HistoryStatus.Queued&&!onExit.Finished&&!onExit.Cancellation.IsCancellationRequested,"exit test leaves a real pending task behind a closed pause gate");
            Require(new Orbit.Core.Jobs.QueueRunControl().IsPaused==false,"a fresh session does not inherit the previous pause");
            File.WriteAllText(Path.Combine(destination,"passed.json"),JsonSerializer.Serialize(new{checks=checks.Count,items=checks,run,quitRequestedUtc=DateTime.UtcNow,quitPendingJobId=onExit.Id,quitWithPausedPendingWork=true,userSettingsWrites=false,nativeClipboardWrites=false,registryWrites=false,physicalInput=false},new JsonSerializerOptions{WriteIndented=true}));
        }
        catch(Exception ex){App.Log(ex);File.WriteAllText(Path.Combine(destination,"error.txt"),ex.ToString());}
        finally{if(slotHeld)_queueGate.Release();_queueExecutionBarrier=null;Quit();}
    }
}
