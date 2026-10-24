using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Orbit.App.Controls;
using Orbit.App.History;
using Orbit.App.Platform;
using Orbit.Core;
using Orbit.Core.Downloads;
using Orbit.Core.Presets;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private async Task RunDownloadSmokeAsync()
    {
        var destination=Path.Combine(AppContext.BaseDirectory,"download-smoke");
        var run=Path.Combine(destination,"run-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        var checks=new List<string>();
        void Require(bool value,string message){if(!value)throw new InvalidOperationException("Download UI: "+message);checks.Add(message);}
        Button Button(string tag)=>SystemsDescendants<Button>(_panelContent).Single(b=>b.Tag as string==tag);
        async Task Drain()=>await Task.WhenAll(_jobTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(15));
        async Task Until(Func<bool> predicate){var timer=Stopwatch.StartNew();while(!predicate()&&timer.Elapsed<TimeSpan.FromSeconds(5))await Task.Delay(15);Require(predicate(),"pending asynchronous context reached expected state");}
        async Task Shot(string name){_toast.Visibility=Visibility.Collapsed;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);Capture(Path.Combine(destination,name));}
        try{
            Directory.CreateDirectory(run);File.Delete(Path.Combine(destination,"passed.json"));File.Delete(Path.Combine(destination,"error.txt"));
            Require(IsDiagnosticRun&&_bridge is null&&!ProductivityIoAllowed&&!PersonalizationIoAllowed,"isolated mode avoids user stores, hotkeys, clipboard and shell");
            Require(Path.GetTempPath().StartsWith(AppContext.BaseDirectory,StringComparison.OrdinalIgnoreCase),"diagnostic temp belongs to its portable folder");
            _outputDirectory=Path.Combine(run,"Скачанные треки");Directory.CreateDirectory(_outputDirectory);
            Require(DownloadDirectory()==_outputDirectory,"downloads use settings output folder");
            _outputDirectory=null;Require(DownloadDirectory().EndsWith("Downloads",StringComparison.Ordinal),"source-adjacent mode has a separate downloads fallback");
            _outputDirectory=Path.Combine(run,"Скачанные треки");
            ShowDownloadPanel();
            var address=SystemsDescendants<TextBox>(_panelContent).Single(b=>b.Tag as string=="DownloadUrl");
            Require(address.Text==""&&Button("DownloadStart").IsEnabled,"manual downloader opens without selecting a local file");
            foreach(var invalid in new[]{"", "ytsearch:test","file:///C:/test.mp4","javascript:alert(1)","https://user:secret@example.com/a","https://example.com/a\n--exec test","https://example.com/"+new string('я',1400)}){
                address.Text=invalid;Button("DownloadStart").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Require(_jobs.Count==0&&!_historyBlocked,"invalid or oversized canonical URL cannot enqueue or block history: "+invalid.Split(':')[0]);
            }
            Require(SystemsDescendants<TextBlock>(_panelContent).Any(t=>t.Tag as string=="DownloadError"&&t.Text.Length>0),"bad URL has inline feedback");
            var stale=Button("DownloadStart");address.Text="https://example.com/stale";ShowMoreTools();stale.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Require(_jobs.Count==0,"detached download form cannot submit after panel changes");
            _toolSearchBox!.Text="yt-dlp";Require(SystemsDescendants<Button>(_panelContent).Count(b=>b.Tag as string=="tool-command")==1,"download tool is searchable without a file");
            var ownerA=new IntPtr(71001);var ownerB=new IntPtr(71002);
            _browserWindowOverride=h=>h==ownerA||h==ownerB;_invocationForegroundOverride=()=>ownerA;
            _browserUrlOverride=_=>Task.FromResult(new BrowserUrlResult("https://example.com/watch?v=audio",null));
            await InvokeAtCursorCoreAsync(new(ownerA,520,350));
            Require(_browserContext&&_browserUrl=="https://example.com/watch?v=audio"&&_files.Count==0&&!_selectionLoading,"browser invocation publishes URL without old file selection");
            Require(_radial.ActionCount==3&&_radial.Children.OfType<SectorButton>().Any(b=>b.Action==ImageAction.DownloadAudio),"browser menu has compact audio/video/tools actions");
            RefreshRadialOrder();Require(_radial.ActionCount==3&&_radial.Children.OfType<SectorButton>().Any(b=>b.Action==ImageAction.DownloadAudio),"file layout refresh cannot replace browser download actions");
            Require(_radial.SettingsButton.IsVisible,"settings stays inside browser radial menu");await Shot("browser-menu.png");
            ShowDownloadPanel(DownloadMode.VideoMp4);Require(SystemsDescendants<TextBox>(_panelContent).Single(b=>b.Tag as string=="DownloadUrl").Text==_browserUrl,"manual form prefills captured active browser URL");await Shot("download-form.png");
            var sink=new ResultsClipboardCapture(Dispatcher);_resultClipboard=new ResultClipboard(Dispatcher,sink);
            int executions=0;
            _downloadOverride=async(request,progress,ct)=>{
                executions++;progress.Report(new DownloadProgress(.5,"Скачивание · 50%"));await Task.Delay(30,ct);
                var output=Path.Combine(request.OutputDirectory,"пример "+executions+(request.Mode==DownloadMode.AudioMp3?".mp3":".mp4"));
                await File.WriteAllBytesAsync(output,new byte[128],ct);return new DownloadResult(output,128,request.Mode,request.Mode==DownloadMode.AudioMp3?"MP3":"MP4");
            };
            SetQueuePaused(true);HandleAction(ImageAction.DownloadAudio);var audio=_jobs.Last();
            Require(audio.Download?.Mode==DownloadMode.AudioMp3&&audio.InputPaths.Count==0&&audio.HistoryStatus==HistoryStatus.Queued,"browser audio action queues typed URL job while paused");
            Require(executions==0&&sink.Payloads.Count==0,"paused queue neither downloads nor copies");
            SetQueuePaused(false);await Drain();
            Require(audio.HistoryStatus==HistoryStatus.Succeeded&&audio.Result?.Format=="MP3"&&File.Exists(audio.Result.OutputPath),"audio action finishes in selected output folder");
            Require(sink.Payloads.Count==1&&sink.Payloads[0].GetFileDropList().Cast<string>().SequenceEqual(new[]{audio.Result!.OutputPath}),"success automatically publishes actual file with CF_HDROP");
            var effect=sink.Payloads[0].GetData(ResultClipboard.PreferredDropEffectFormat,false) as MemoryStream;
            Require(effect is not null&&BitConverter.ToInt32(effect.ToArray(),0)==1&&sink.Sta&&sink.Persistent,"clipboard operation is persistent STA Copy, never Cut");
            Require(audio.Detail.Contains("скопирован")&&!PresetCatalog.CanSave(audio.Options)&&_repeatRecipe is null,"download copy status is visible and never replaces file recipes");
            var snapshot=HistorySnapshot().Single(e=>e.Id==audio.Id);
            Require(HistoryValidation.TryNormalize(snapshot,true,out var restored)&&restored.Download?.Url==audio.Download!.Url,"download history round trips URL and finished output");
            Require(!HistoryValidation.TryNormalize(snapshot with{Download=null},false,out _),"download marker without typed request is rejected");
            Require(!HistoryValidation.TryNormalize(snapshot with{SourcePaths=new[]{run}},false,out _),"download history cannot smuggle file source paths");
            Require(!HistoryValidation.TryNormalize(snapshot with{Result=audio.Result! with{OutputPath=Path.Combine(run,"outside.mp3")}},false,out _),"download result must stay in its recorded directory");
            Require(!HistoryValidation.TryNormalize(snapshot with{Download=audio.Download! with{Url="file:///C:/bad"}},false,out _),"history rejects non web sources");
            Require(!HistoryValidation.TryNormalize(snapshot with{Download=audio.Download! with{Url="https://example.com/"+new string('я',1400)}},false,out _),"history rejects URL expansion beyond canonical size limit");
            Require(HistoryValidation.TryNormalize(snapshot with{Status=HistoryStatus.Running,Result=null,FinishedUtc=null},true,out var interrupted)&&interrupted.Status==HistoryStatus.Interrupted,"unfinished restored download is interrupted, not automatically resumed");
            await RetryHistoryJobAsync(audio);Require(_jobs.Count==1&&SystemsDescendants<TextBox>(_panelContent).Single(b=>b.Tag as string=="DownloadUrl").Text==audio.Download!.Url,"retry presents editable URL before new submission");
            SetQueuePaused(true);EnqueueDownload(BuildDownloadRequest("https://example.com/cancel",DownloadMode.VideoMp4));var waiting=_jobs.Last();waiting.Cancellation.Cancel();await Drain();
            Require(waiting.HistoryStatus==HistoryStatus.Cancelled&&sink.Payloads.Count==1&&executions==1,"waiting cancellation never starts network or copies a file");
            SetQueuePaused(false);
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _downloadOverride=async(_,_,ct)=>{entered.TrySetResult();await Task.Delay(Timeout.Infinite,ct);throw new InvalidOperationException();};
            EnqueueDownload(BuildDownloadRequest("https://example.com/running-cancel",DownloadMode.VideoMp4));var running=_jobs.Last();await entered.Task;running.Cancellation.Cancel();await Drain();
            Require(running.HistoryStatus==HistoryStatus.Cancelled&&running.Result is null&&sink.Payloads.Count==1,"running cancellation skips clipboard publication");
            _downloadOverride=(_,_,_)=>throw new InvalidOperationException("Сайт недоступен");EnqueueDownload(BuildDownloadRequest("https://example.com/offline",DownloadMode.AudioMp3));await Drain();
            Require(_jobs.Last().HistoryStatus==HistoryStatus.Failed&&sink.Payloads.Count==1,"network failure produces failed job without clipboard mutation");
            _downloadOverride=(request,_,ct)=>{var output=Path.Combine(request.OutputDirectory,"video.mp4");File.WriteAllBytes(output,new byte[256]);return Task.FromResult(new DownloadResult(output,256,request.Mode,"MP4"));};
            EnqueueDownload(BuildDownloadRequest("https://example.com/video",DownloadMode.VideoMp4));await Drain();
            Require(_jobs.Last().Result?.Format=="MP4"&&sink.Payloads.Count==2,"later video still succeeds after cancelled and failed jobs");
            _resultClipboard=new ResultClipboard(Dispatcher,new DownloadsBusyClipboard());
            _downloadOverride=(request,_,ct)=>{var output=Path.Combine(request.OutputDirectory,"saved-with-busy-clipboard.mp3");File.WriteAllBytes(output,new byte[256]);return Task.FromResult(new DownloadResult(output,256,request.Mode,"MP3"));};
            EnqueueDownload(BuildDownloadRequest("https://example.com/busy",DownloadMode.AudioMp3));await Drain();
            Require(_jobs.Last().HistoryStatus==HistoryStatus.Succeeded&&File.Exists(_jobs.Last().Result!.OutputPath)&&_jobs.Last().Detail.Contains("буфер недоступен"),"clipboard busy never fails or deletes a successful download");
            Require(sink.Payloads.Count==2,"failed auto copy does not mutate the previous captured clipboard");_resultClipboard=new ResultClipboard(Dispatcher,sink);
            ShowJobs();_jobSearchBox!.Text="example.com/busy";
            SystemsDescendants<Button>(_panelContent).Single(b=>b.Content as string=="Копировать").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            await Task.WhenAll(_resultToolTasks.ToArray());
            Require(sink.Payloads.Count==3&&sink.Payloads[^1].GetFileDropList()[0]==_jobs.Last().Result!.OutputPath,"real task Copy button recovers after failed automatic copy");
            ShowJobs();_jobSearchBox!.Text="example.com/video";Require(FilteredJobs().Count()==1,"URL is searchable in queue and history");_jobSearchBox.Text="";await Shot("download-queue.png");
            _browserUrlOverride=_=>Task.FromResult(new BrowserUrlResult(null,"Вставьте ссылку вручную"));_invocationForegroundOverride=()=>ownerA;
            await InvokeAtCursorCoreAsync(new(ownerA,510,350));Require(_browserContext&&_browserUrl is null&&_lastPopupOutput is null,"unreadable browser clears prior URL and result");
            HandleAction(ImageAction.DownloadVideo);Require(SystemsDescendants<TextBox>(_panelContent).Single(b=>b.Tag as string=="DownloadUrl").Text=="","browser read failure falls back to empty manual URL form");
            var oldRead=new TaskCompletionSource<BrowserUrlResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _browserUrlOverride=h=>h==ownerA?oldRead.Task:Task.FromResult(new BrowserUrlResult("https://example.com/new",null));
            var oldInvoke=InvokeAtCursorCoreAsync(new(ownerA,510,350));await Until(()=>_selectionLoading&&IsVisible);
            _invocationForegroundOverride=()=>ownerB;await InvokeAtCursorCoreAsync(new(ownerB,530,350));oldRead.SetResult(new BrowserUrlResult("https://example.com/old",null));await oldInvoke;
            Require(_browserUrl=="https://example.com/new","late older browser address cannot replace newer invocation");
            ClearSelection();Require(!_browserContext&&_browserUrl is null&&!_radial.Children.OfType<SectorButton>().Any(b=>b.Action==ImageAction.DownloadAudio),"local file reset removes browser actions and old URL");
            var history=new HistoryStore(Path.Combine(run,"history.json"));Require(await history.SaveAsync(HistorySnapshot()) is null,"mixed completed download history persists in isolated store");
            var loaded=await history.LoadAsync();Require(loaded.Error is null&&loaded.Entries.Count==_jobs.Count&&loaded.Entries.All(e=>e.Download is not null),"persisted download jobs restore without filesystem-source assumptions");
            File.WriteAllText(Path.Combine(destination,"passed.json"),JsonSerializer.Serialize(new{checks=checks.Count,items=checks,run,nativeClipboardWrites=false,physicalInput=false,network=false,downloadExecution="injected; real process engine covered separately"},new JsonSerializerOptions{WriteIndented=true}));
        }catch(Exception ex){App.Log(ex);File.WriteAllText(Path.Combine(destination,"error.txt"),ex.ToString());}
        finally{_browserUrlOverride=null;_browserWindowOverride=null;_invocationForegroundOverride=null;_downloadOverride=null;SetQueuePaused(false);Quit();}
    }
    private sealed class DownloadsBusyClipboard:IResultClipboardSink
    {
        public void SetDataObject(System.Windows.Forms.DataObject payload,bool copy)=>throw new System.Runtime.InteropServices.ExternalException("Owned diagnostic clipboard busy",unchecked((int)0x800401D0));
    }
}
