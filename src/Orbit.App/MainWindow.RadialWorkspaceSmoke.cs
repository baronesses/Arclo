using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Orbit.App.Controls;
using Orbit.App.History;
using Orbit.App.Platform;
using Orbit.App.Presets;
using Orbit.Core;
using Orbit.Core.Downloads;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private async Task RunRadialWorkspaceSmokeAsync()
    {
        var destination=Path.Combine(AppContext.BaseDirectory,"radial-workspace-smoke");
        var run=Path.Combine(destination,"run-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        var checks=new List<string>();var release=new List<Action>();
        var originals=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var metrics=new List<object>();bool queueHeld=false;
        void Require(bool value,string text){if(!value)throw new InvalidOperationException("Radial workspace: "+text);checks.Add(text);}
        T Tagged<T>(string tag)where T:FrameworkElement=>SystemsDescendants<T>(_panelContent).Single(element=>element.Tag as string==tag);
        T? Find<T>(string tag)where T:FrameworkElement=>SystemsDescendants<T>(_panelContent).SingleOrDefault(element=>element.Tag as string==tag);
        void Click(string tag)=>Tagged<Button>(tag).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        bool Circle()=>_radialWorkspaceVisible&&_panel.Visibility!=Visibility.Visible&&_radialView.Visibility==Visibility.Visible&&!_jobsVisible;
        bool Current(string path)=>Circle()&&string.Equals(_radialWorkspacePath,path,StringComparison.OrdinalIgnoreCase)
            &&_radial.FileName.Text==Path.GetFileName(path);
        static TaskCompletionSource Gate()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Until(Func<bool> condition,string reason,int seconds=12)
        {
            var watch=Stopwatch.StartNew();while(!condition()&&watch.Elapsed<TimeSpan.FromSeconds(seconds))await Task.Delay(20);
            if(!condition())throw new TimeoutException(reason);await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);
        }
        async Task Work()
        {
            await Task.WhenAll(_jobTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(90));
            await _radialWorkspaceTask.WaitAsync(TimeSpan.FromSeconds(30));
            await Task.WhenAll(_resultToolTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(45));
            await FlushHistoryAsync();await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);
        }
        async Task Shot(string name){_toast.Visibility=Visibility.Collapsed;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);Capture(Path.Combine(destination,name));}
        async Task Paste(string path)
        {
            var data=new DataObject();data.SetData(DataFormats.FileDrop,new[]{path});
            var payload=RadialClipboardReader.Parse(data);
            Require(payload.Error is null&&payload.Paths.SequenceEqual(new[]{path}),"owned file-drop DataObject parses without reading the system clipboard");
            await ApplyClipboardPayloadAsync(payload);
        }
        void MainCommands()
        {
            var commands=_radial.Children.OfType<SectorButton>().Select(button=>button.CommandKind).ToArray();
            Require(commands.Length==5&&commands.ToHashSet().SetEquals(new[]{RadialCommand.Convert,RadialCommand.SaveAs,RadialCommand.Copy,RadialCommand.Trim,RadialCommand.More}),
                "current file exposes exactly Convert, Save as, Copy, Trim and More in the circle");
        }
        JobEntry Submit(string url)
        {
            ShowDownloadPanel(DownloadMode.AudioMp3,url);int count=_jobs.Count;Click("DownloadStart");
            if(_jobs.Count!=count+1)throw new InvalidOperationException("Download form did not enqueue exactly one job");return _jobs[^1];
        }
        int downloads=0;
        try
        {
            Directory.CreateDirectory(run);File.Delete(Path.Combine(destination,"passed.json"));File.Delete(Path.Combine(destination,"error.txt"));
            Require(IsDiagnosticRun&&_bridge is null&&!ProductivityIoAllowed&&!PersonalizationIoAllowed,
                "diagnostic starts without user persistence, tray, hotkeys or native clipboard access");
            Require(Path.GetTempPath().StartsWith(AppContext.BaseDirectory,StringComparison.OrdinalIgnoreCase),"temporary work stays inside the diagnostic portable runtime");
            var fixtures=Path.Combine(run,"fixtures");Directory.CreateDirectory(fixtures);
            _outputDirectory=Path.Combine(run,"Готовые файлы [1]");Directory.CreateDirectory(_outputDirectory);
            _historyStore=new HistoryStore(Path.Combine(run,"history.json"));_presetStore=new PresetStore(Path.Combine(run,"presets.json"));_allowDiagnosticProductivityIo=true;
            var sink=new ResultsClipboardCapture(Dispatcher);_resultClipboard=new ResultClipboard(Dispatcher,sink);
            var wave=Path.Combine(fixtures,"длинный источник.wav");WriteDownloadFlowWave(wave,32,440);
            var image=Path.Combine(fixtures,"изображение [1].png");WriteResultsImage(image);
            var media=new MediaEngine();var encoded=await media.ExecuteAsync(wave,MediaOperation.AudioMp3,new MediaOptions{OutputDirectory=fixtures,AudioBitrateKbps=192});
            string mp3=encoded.OutputPath;var mp3Metadata=await media.InspectAsync(mp3);
            foreach(var path in new[]{wave,image,mp3})originals[path]=SystemsDigest(path);
            Require(mp3Metadata.Kind=="Audio"&&mp3Metadata.DurationSeconds>31.9,"native fixture is an actual MP3 longer than thirty seconds");
            SetCompactMode(true);await Paste(mp3);
            Require(Current(mp3)&&_radial.Preview.Source is not null&&_radialWorkspaceMetadata?.Kind==ToolkitKind.Audio,
                "pasted local MP3 becomes the current file in the circle without a details panel or history");MainCommands();
            Require(!_radial.ProgressVisible,"idle current file has no progress ring");await Shot("01-pasted-audio-circle.png");

            int copies=sink.Payloads.Count;
            var copyButton=_radial.Children.OfType<SectorButton>().Single(button=>button.CommandKind==RadialCommand.Copy);
            copyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(()=>sink.Payloads.Count==copies+1,"actual command-sector click reaches the injected clipboard sink");
            Require(sink.Payloads[^1].GetFileDropList()[0]==mp3&&Current(mp3),"Copy publishes the exact current file and leaves the circle visible");
            var preview=_radial.Preview.Source;FocusManager.SetFocusedElement(this,_radial.CenterCard);var focus=FocusManager.GetFocusedElement(this);
            await RunRadialCommandAsync(RadialCommand.Convert);
            Require(_radialConversionMenu&&Circle()&&_radial.ActionCount==5
                &&_radial.Children.OfType<SectorButton>().Count(button=>button.CommandKind!=RadialCommand.None)==2
                &&_radial.Children.OfType<SectorButton>().Where(button=>button.CommandKind==RadialCommand.None).Select(button=>button.Action).ToHashSet().SetEquals(new[]{ImageAction.AudioMp3,ImageAction.AudioWav,ImageAction.AudioFlac})
                &&_radial.Children.OfType<SectorButton>().Any(button=>button.CommandKind==RadialCommand.Convert)
                &&_radial.Children.OfType<SectorButton>().Any(button=>button.CommandKind==RadialCommand.More),
                "Convert retains five circle sectors with real MP3, WAV and FLAC formats plus Back and More");
            Require(ReferenceEquals(preview,_radial.Preview.Source)&&ReferenceEquals(focus,FocusManager.GetFocusedElement(this))&&Current(mp3),
                "format submenu preserves the current preview, filename and logical focus");await Shot("02-audio-format-circle.png");
            _radial.CenterCard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(!_radialConversionMenu&&Current(mp3),"center click returns from format choices to the current-file menu");MainCommands();

            string saved=Path.Combine(run,"отдельная копия.mp3");string? requestedSaveSource=null;
            _radialSaveAsPathOverride=path=>{requestedSaveSource=path;return Task.FromResult<string?>(null);};
            await RunRadialCommandAsync(RadialCommand.SaveAs);
            Require(requestedSaveSource==mp3&&!File.Exists(saved)&&Current(mp3),"cancelled Save as keeps the current circle and creates no file");
            _radialSaveAsPathOverride=path=>{requestedSaveSource=path;return Task.FromResult<string?>(saved);};
            await RunRadialCommandAsync(RadialCommand.SaveAs);
            Require(requestedSaveSource==mp3&&File.Exists(saved)&&SystemsDigest(saved)==originals[mp3]&&Current(mp3),
                "Save as writes an exact separate copy from the current file without changing the circle source");
            string savedHash=SystemsDigest(saved);await RunRadialCommandAsync(RadialCommand.SaveAs);
            Require(SystemsDigest(saved)==savedHash&&Current(mp3),"Save as refuses an existing destination without replacing its bytes");

            await _queueGate.WaitAsync();queueHeld=true;await RunRadialCommandAsync(RadialCommand.Convert);int count=_jobs.Count;
            _radial.Children.OfType<SectorButton>().Single(button=>button.Action==ImageAction.AudioWav).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(_jobs.Count==count+1&&_jobs[^1].Options.Action==ImageAction.AudioWav&&_jobs[^1].InputPaths.SequenceEqual(new[]{mp3}),
                "actual WAV sector queues conversion from the current MP3");var converted=_jobs[^1];
            Require(Circle()&&_radial.ProgressVisible&&_radial.Children.OfType<SectorButton>().Any(button=>button.CommandKind==RadialCommand.Cancel),
                "queued conversion keeps the current-file circle with a progress ring and Cancel command");
            _queueGate.Release();queueHeld=false;await Work();string currentWave=converted.Result!.OutputPath;var waveMetadata=await media.InspectAsync(currentWave);
            metrics.Add(new{operation="AudioWav",path=currentWave,duration=waveMetadata.DurationSeconds,format=waveMetadata.Format});
            Require(converted.HistoryStatus==HistoryStatus.Succeeded&&converted.Result.Format=="WAV"&&Math.Abs(waveMetadata.DurationSeconds-32)<.1&&Current(currentWave),
                "real native conversion publishes the full WAV and updates the current circle without opening history or an editor");
            Require(!_radial.ProgressVisible&&sink.Payloads[^1].GetFileDropList()[0]==currentWave,"completed conversion clears the ring and copies its exact new result");MainCommands();await Shot("03-converted-current-circle.png");

            await RunRadialCommandAsync(RadialCommand.Trim);
            await Until(()=>Find<MediaPreviewPane>("DownloadCurrentPlayer") is not null,"explicit Trim attaches the media editor");var pane=Tagged<MediaPreviewPane>("DownloadCurrentPlayer");
            Require(_panel.Visibility==Visibility.Visible&&pane.Player.Source?.LocalPath==currentWave&&!pane.IsPlaying&&pane.IsMuted,
                "only explicit Trim opens the existing muted editor for the exact current file without autoplay");
            ClosePanel();Require(pane.IsClosed&&Current(currentWave)&&!_radial.ProgressVisible,
                "closing the explicit editor releases its player and restores the same current-file circle");
            await RunRadialCommandAsync(RadialCommand.Trim);await Until(()=>Find<MediaPreviewPane>("DownloadCurrentPlayer") is not null,"current file editor reopens explicitly");
            Tagged<TextBox>("DownloadTrimStart").Text="1.25";Tagged<TextBox>("DownloadTrimEnd").Text="2.75";count=_jobs.Count;Click("DownloadTrimSave");var trimmed=_jobs[^1];
            Require(_jobs.Count==count+1&&trimmed.Options.Action==ImageAction.AudioTrim&&trimmed.InputPaths.SequenceEqual(new[]{currentWave})&&Circle(),
                "Save fragment returns immediately to the circle and queues only the current WAV interval");
            await Work();string cut=trimmed.Result!.OutputPath;var cutMetadata=await media.InspectAsync(cut);
            metrics.Add(new{operation="AudioTrim",path=cut,duration=cutMetadata.DurationSeconds,format=cutMetadata.Format});
            Require(trimmed.HistoryStatus==HistoryStatus.Succeeded&&Math.Abs(cutMetadata.DurationSeconds-1.5)<.08&&Current(cut)
                &&sink.Payloads[^1].GetFileDropList()[0]==cut,"actual 1.5-second trim becomes the current circular result and clipboard file");

            await Paste(image);Require(Current(image)&&_radialWorkspaceMetadata?.Kind==ToolkitKind.Image,"pasted image replaces the current audio file and keeps the circle");
            preview=_radial.Preview.Source;await RunRadialCommandAsync(RadialCommand.Convert);
            Require(_radialConversionMenu&&ReferenceEquals(preview,_radial.Preview.Source)&&_radial.Children.OfType<SectorButton>().Any(button=>button.Action==ImageAction.Png),
                "image Convert changes only the format sectors while retaining its real preview");
            _radial.Children.OfType<SectorButton>().Single(button=>button.Action==ImageAction.Png).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));var webp=_jobs[^1];await Work();
            var imageMetadata=await _engine.InspectAsync(webp.Result!.OutputPath);var originalImageMetadata=await _engine.InspectAsync(image);
            metrics.Add(new{operation="PNG",path=webp.Result.OutputPath,width=imageMetadata.Width,height=imageMetadata.Height,format=imageMetadata.Format});
            Require(webp.HistoryStatus==HistoryStatus.Succeeded&&imageMetadata.Format=="PNG"&&imageMetadata.Width==originalImageMetadata.Width
                &&imageMetadata.Height==originalImageMetadata.Height&&Current(webp.Result.OutputPath),"actual PNG to PNG conversion preserves dimensions and publishes its current circular result");
            await RunRadialCommandAsync(RadialCommand.More);Require(_panel.Visibility==Visibility.Visible&&!_jobsVisible,"More opens tools only after its explicit command");
            ClosePanel();Require(Current(webp.Result.OutputPath),"closing More restores the same current result circle");await Shot("04-current-image-circle.png");

            DownloadResult CopyDownload(DownloadRequest request)
            {
                string output=Path.Combine(request.OutputDirectory,"скачанный трек "+(++downloads)+".mp3");File.Copy(mp3,output);originals[output]=SystemsDigest(output);
                return new DownloadResult(output,new FileInfo(output).Length,request.Mode,"MP3",DurationSeconds:mp3Metadata.DurationSeconds);
            }
            async Task<(JobEntry Job,TaskCompletionSource Done,IProgress<DownloadProgress> Progress)> Held(string url)
            {
                var entered=Gate();var done=Gate();IProgress<DownloadProgress>? reporter=null;release.Add(()=>done.TrySetResult());
                _downloadOverride=async(request,progress,ct)=>{reporter=progress;progress.Report(new(.37,"Получаем тестовый трек"));entered.TrySetResult();await done.Task.WaitAsync(ct);return CopyDownload(request);};
                var job=Submit(url);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));await Until(()=>_radial.ProgressFraction is{} fraction&&Math.Abs(fraction-.37)<.01,"radial progress receives the running fraction");
                return(job,done,reporter!);
            }
            const string url="https://example.com/media/radial-current";var urlData=new DataObject();urlData.SetData(DataFormats.UnicodeText,url);
            await ApplyClipboardPayloadAsync(RadialClipboardReader.Parse(urlData));
            Require(_browserContext&&_browserUrl==url&&_panel.Visibility!=Visibility.Visible,"injected URL paste offers browser download actions in the circle without reading clipboard or browser data");
            var held=await Held(url);
            Require(Circle()&&ReferenceEquals(_radialWorkspaceJob,held.Job)&&_radial.ProgressVisible&&_radial.Preview.Source is not null,
                "default running download shows its file center and progress ring rather than a rectangular progress panel");
            var ring=_radial.Children.OfType<FrameworkElement>().Single(element=>element.GetType().Name=="RadialProgressRing");
            Require(ring.Visibility==Visibility.Visible&&!ring.IsHitTestVisible&&_radial.Metadata.Text.Contains("тестовый",StringComparison.Ordinal),
                "actual progress ring is visible and cannot intercept the radial command hit targets");await Shot("05-download-progress-circle.png");
            held.Done.TrySetResult();await Work();string downloaded=held.Job.Result!.OutputPath;
            Require(held.Job.HistoryStatus==HistoryStatus.Succeeded&&Current(downloaded)&&_radialWorkspaceMetadata?.DurationSeconds>31.9
                &&!_radial.ProgressVisible&&Find<MediaPreviewPane>("DownloadCurrentPlayer") is null,
                "download completion stays in the same circle with its full current file and no automatic editor or history");
            Require(sink.Payloads[^1].GetFileDropList()[0]==downloaded,"download completion copies the actual current file rather than its URL");MainCommands();await Shot("06-downloaded-current-circle.png");

            held=await Held("https://example.com/media/radial-cancel");copies=sink.Payloads.Count;
            await RunRadialCommandAsync(RadialCommand.Cancel);await Work();
            Require(held.Job.HistoryStatus==HistoryStatus.Cancelled&&held.Job.Result is null&&sink.Payloads.Count==copies&&Circle(),
                "radial Cancel stops the held download without publishing a file, clipboard payload or history panel");
            Require(!_radial.ProgressVisible&&!_radial.Children.OfType<SectorButton>().Any(button=>button.CommandKind==RadialCommand.Cancel)
                &&_radial.Children.OfType<SectorButton>().Any(button=>button.CommandKind==RadialCommand.Trim),
                "cancelled download removes its progress ring and restores the idle command sectors");

            held=await Held("https://example.com/media/old-settings");ShowSettings();var settings=_panelContent.Children[0];int version=_comparisonVersion;
            held.Progress.Report(new(.82,"Поздний прогресс"));await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Background);
            Require(_panel.Visibility==Visibility.Visible&&ReferenceEquals(settings,_panelContent.Children[0])&&version==_comparisonVersion&&!_radial.ProgressVisible,
                "late running progress cannot replace newer settings or expose a radial progress overlay");
            held.Done.TrySetResult();await Work();
            Require(held.Job.HistoryStatus==HistoryStatus.Succeeded&&version==_comparisonVersion&&ReferenceEquals(settings,_panelContent.Children[0])
                &&Find<MediaPreviewPane>("DownloadCurrentPlayer") is null,"late completed download cannot navigate over settings");
            held=await Held("https://example.com/media/old-paste");await Paste(image);preview=_radial.Preview.Source;
            held.Progress.Report(new(.91,"Старый файл почти готов"));await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Background);
            Require(Current(image)&&ReferenceEquals(preview,_radial.Preview.Source)&&!_radial.ProgressVisible,"late progress cannot change a newly pasted current file or its preview");
            held.Done.TrySetResult();await Work();
            Require(Current(image)&&ReferenceEquals(preview,_radial.Preview.Source)&&!_radial.ProgressVisible&&held.Job.HistoryStatus==HistoryStatus.Succeeded,
                "late completion preserves the newer pasted file while retaining the older successful job separately");
            var pasteEntered=Gate();var pasteRelease=Gate();release.Add(()=>pasteRelease.TrySetResult());
            _selectionInspectorOverride=async(path,_)=>{pasteEntered.TrySetResult();await pasteRelease.Task;return await _engine.InspectAsync(path);};
            var slowPaste=ApplyClipboardPayloadAsync(new RadialClipboardPayload(new[]{mp3},null,null));
            await pasteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));ShowSettings();settings=_panelContent.Children[0];version=_comparisonVersion;
            pasteRelease.TrySetResult();await slowPaste.WaitAsync(TimeSpan.FromSeconds(20));_selectionInspectorOverride=null;
            Require(_panel.Visibility==Visibility.Visible&&ReferenceEquals(settings,_panelContent.Children[0])&&version==_comparisonVersion
                &&!_radialWorkspaceVisible&&!_radial.ProgressVisible,
                "slow pasted-file inspection cannot close newer settings or activate a stale radial workspace");
            Require(originals.All(pair=>File.Exists(pair.Key)&&SystemsDigest(pair.Key)==pair.Value)&&SystemsDigest(saved)==savedHash,
                "conversion, trim, save-copy and stale navigation preserve every original source and earlier separate copy");
            await FlushHistoryAsync();var restored=await new HistoryStore(_historyStore.FilePath).LoadAsync();
            Require(restored.Error is null&&restored.Entries.Count==_jobs.Count&&restored.Entries.Any(entry=>entry.Id==held.Job.Id&&entry.Result?.OutputPath==held.Job.Result!.OutputPath),
                "durable isolated history records real jobs without taking over the radial workspace");
            ShowJobs();Require(_jobsVisible,"history remains available as an explicit separate view");await Shot("07-explicit-history.png");
            File.WriteAllText(Path.Combine(destination,"passed.json"),JsonSerializer.Serialize(new{checks=checks.Count,items=checks,run,metrics,
                downloadNetwork=false,downloadExecution="injected copy of real native MP3",actualNativeAudioConversion=true,actualNativeAudioTrim=true,
                actualNativeImageConversion=true,automaticEditor=false,userProfileIo=false,systemClipboardReads=false,nativeClipboardWrites=false,
                physicalInput=false,playbackMuted=true,autoplay=false,listeningPerformed=false},new JsonSerializerOptions{WriteIndented=true}));
        }
        catch(Exception error){App.Log(error);Directory.CreateDirectory(destination);File.WriteAllText(Path.Combine(destination,"error.txt"),error+"\n"+string.Join("\n",checks));}
        finally
        {
            foreach(var unblock in release)unblock();if(queueHeld)_queueGate.Release();
            _downloadOverride=null;_radialSaveAsPathOverride=null;_selectionInspectorOverride=null;
            foreach(var job in _jobs.Where(job=>!job.Finished))job.Cancellation.Cancel();SetQueuePaused(false);Quit();
        }
    }
}
