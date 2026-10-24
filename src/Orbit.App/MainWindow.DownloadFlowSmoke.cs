using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Orbit.App.Controls;
using Orbit.App.Editing;
using Orbit.App.History;
using Orbit.App.Platform;
using Orbit.App.Presets;
using Orbit.Core;
using Orbit.Core.Downloads;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private async Task RunDownloadFlowSmokeAsync()
    {
        var destination=Path.Combine(AppContext.BaseDirectory,"download-flow-smoke");
        var run=Path.Combine(destination,"run-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        var checks=new List<string>();var release=new List<Action>();int playerSnapshots=0;
        var originals=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        void Require(bool value,string text){if(!value)throw new InvalidOperationException("Download flow: "+text);checks.Add(text);}
        T Tagged<T>(string tag)where T:FrameworkElement=>SystemsDescendants<T>(_panelContent).Single(x=>x.Tag as string==tag);
        T? Find<T>(string tag)where T:FrameworkElement=>SystemsDescendants<T>(_panelContent).SingleOrDefault(x=>x.Tag as string==tag);
        void Click(string tag)=>Tagged<Button>(tag).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        async Task Until(Func<bool> predicate,string reason,int seconds=12){var watch=Stopwatch.StartNew();while(!predicate()&&watch.Elapsed<TimeSpan.FromSeconds(seconds))await Task.Delay(20);if(!predicate())throw new TimeoutException(reason);await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);}
        async Task Finish(){await Task.WhenAll(_jobTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(90));await _downloadEditorTask.WaitAsync(TimeSpan.FromSeconds(30));await FlushHistoryAsync();await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);}
        async Task FinishInExplicitEditor(JobEntry job)
        {
            // The default download/edit destination is now the circle. This suite deliberately
            // exercises the production details/editor route also available from job history.
            await Task.WhenAll(_jobTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(90));
            ShowDownloadJob(job);await Finish();
        }
        async Task<MediaPreviewPane> Player()
        {
            await Until(()=>Find<MediaPreviewPane>("DownloadCurrentPlayer") is not null,"download editor player is attached");
            var pane=Tagged<MediaPreviewPane>("DownloadCurrentPlayer");
            _toast.Visibility=Visibility.Collapsed;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);Capture(Path.Combine(destination,$"player-before-ready-{++playerSnapshots:00}.png"));
            try{await Until(()=>pane.IsOpened||pane.IsClosed||pane.ErrorMessage is not null,"downloaded local file opens in Windows player",20);}
            catch(TimeoutException error){throw new TimeoutException($"Downloaded player did not open: loaded={pane.IsLoaded}; visible={pane.IsVisible}; closed={pane.IsClosed}; source={pane.Player.Source}; status={pane.StatusText}; naturalDuration={pane.Player.NaturalDuration}; hasAudio={pane.Player.HasAudio}; canPause={pane.Player.CanPause}; windowVisible={IsVisible}; nativeVisible={DownloadSmokeIsWindowVisible(new WindowInteropHelper(this).Handle)}",error);}
            Require(pane.IsOpened&&pane.IsMuted&&!pane.IsPlaying,"current downloaded-file player opens muted without autoplay; status="+pane.StatusText+"; error="+pane.ErrorMessage);return pane;
        }
        async Task Shot(string name){_toast.Visibility=Visibility.Collapsed;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);Capture(Path.Combine(destination,name));}
        static TaskCompletionSource Gate()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
        JobEntry Submit(string url,DownloadMode mode=DownloadMode.AudioMp3){ShowDownloadPanel(mode,url);int count=_jobs.Count;Click("DownloadStart");if(_jobs.Count!=count+1)throw new InvalidOperationException("Form did not create exactly one job");var job=_jobs[^1];ShowDownloadJob(job);return job;}
        int executions=0;
        try
        {
            Directory.CreateDirectory(run);File.Delete(Path.Combine(destination,"passed.json"));File.Delete(Path.Combine(destination,"error.txt"));
            Require(IsDiagnosticRun&&_bridge is null&&!ProductivityIoAllowed&&!PersonalizationIoAllowed,"isolated mode starts without user persistence, hotkeys, shell or clipboard access");
            Require(Path.GetTempPath().StartsWith(AppContext.BaseDirectory,StringComparison.OrdinalIgnoreCase),"all diagnostic temporary work belongs to the portable runtime");
            _outputDirectory=Path.Combine(run,"Сохранённые треки [1]");Directory.CreateDirectory(_outputDirectory);
            _historyStore=new HistoryStore(Path.Combine(run,"history.json"));_presetStore=new PresetStore(Path.Combine(run,"presets.json"));_allowDiagnosticProductivityIo=true;
            var sink=new ResultsClipboardCapture(Dispatcher);_resultClipboard=new ResultClipboard(Dispatcher,sink);
            var fixtures=Path.Combine(run,"fixtures");Directory.CreateDirectory(fixtures);
            var wave=Path.Combine(fixtures,"длинный исходник.wav");WriteDownloadFlowWave(wave,32,440);
            var other=Path.Combine(fixtures,"другой исходник.wav");WriteDownloadFlowWave(other,4,880);
            var image=Path.Combine(fixtures,"новое выделение.png");WriteResultsImage(image);
            var video=Path.Combine(fixtures,"исходное видео.mp4");await WriteEditingMediaAsync(video,true);
            var media=new MediaEngine();var encoded=await media.ExecuteAsync(wave,MediaOperation.AudioMp3,new MediaOptions{OutputDirectory=fixtures,AudioBitrateKbps=192});
            string mp3=encoded.OutputPath;var sourceMetadata=await media.InspectAsync(mp3);
            Require(sourceMetadata.Kind=="Audio"&&sourceMetadata.DurationSeconds>31.9,"real FFmpeg fixture is an MP3 longer than the old 30-second preview limit");
            foreach(string path in new[]{wave,other,image,mp3,video})originals[path]=SystemsDigest(path);
            DownloadResult Copy(DownloadRequest request)
            {
                var output=Path.Combine(request.OutputDirectory,"скачанный трек "+(++executions)+".mp3");File.Copy(mp3,output);
                originals[output]=SystemsDigest(output);
                return new DownloadResult(output,new FileInfo(output).Length,request.Mode,"MP3",DurationSeconds:sourceMetadata.DurationSeconds);
            }
            var started=Gate();var complete=Gate();release.Add(()=>complete.TrySetResult());
            _downloadOverride=async(request,progress,ct)=>{progress.Report(new DownloadProgress(.37,"Получение тестового трека"));started.TrySetResult();await complete.Task.WaitAsync(ct);return Copy(request);};
            SetCompactMode(true);var browserOwner=new IntPtr(81001);const string firstUrl="https://example.com/track/first";
            _browserWindowOverride=handle=>handle==browserOwner;_browserUrlOverride=_=>Task.FromResult(new BrowserUrlResult(firstUrl,null));_invocationForegroundOverride=()=>browserOwner;
            await InvokeAtCursorCoreAsync(new(browserOwner,520,350));Require(_browserContext&&_browserUrl==firstUrl,"injected browser invocation supplies the current flow without native browser reads");
            HandleAction(ImageAction.DownloadAudio);var first=_jobs[^1];ShowDownloadJob(first);await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(()=>Find<TextBlock>("DownloadPercent")?.Text.Contains("37",StringComparison.Ordinal)==true,"focused progress reflects the actual running job");
            Require(ReferenceEquals(_focusedDownloadJob,first)&&!_jobsVisible&&Find<Button>("DownloadCancel") is not null,"explicit job details show the selected download progress without opening all history");
            Require(Tagged<TextBlock>("DownloadStatus").Text.Contains("тестового",StringComparison.Ordinal),"focused progress shows the current stage instead of generic job history");
            var bar=Tagged<ProgressBar>("DownloadProgress");Require(!bar.IsIndeterminate&&Math.Abs(bar.Value/bar.Maximum-.37)<.01,"focused progress bar uses reported fraction");
            Require(first.HistoryStatus==HistoryStatus.Running&&sink.Payloads.Count==0,"incomplete download has no result or clipboard publication");await Shot("01-download-progress.png");
            complete.TrySetResult();await Finish();Require(_toast.Visibility!=Visibility.Visible,"current browser download completion uses editor status without a toast covering its controls");var firstPlayer=await Player();
            Require(first.HistoryStatus==HistoryStatus.Succeeded&&first.Result?.OutputPath is{} firstPath&&File.Exists(firstPath),"download completion publishes an actual existing MP3");
            string downloaded=first.Result!.OutputPath;
            Require(firstPlayer.DurationSeconds>30&&!_jobsVisible&&Tagged<TextBlock>("DownloadCurrentFile").Text.Contains(Path.GetFileName(downloaded),StringComparison.Ordinal),"explicit download details finish in the full current track editor rather than history or a 30-second proxy");
            Require(sink.Payloads.Count==1&&sink.Payloads[0].GetFileDropList()[0]==downloaded,"original download copies its actual finished file through the injected clipboard only");
            firstPlayer.Play();
            try{await Until(()=>firstPlayer.PositionSeconds>.05||firstPlayer.ErrorMessage is not null||firstPlayer.IsClosed,"manual current-track playback advances after initial decoder preroll",5);}
            catch(TimeoutException error){throw new TimeoutException($"Manual current-track playback stalled: playing={firstPlayer.IsPlaying}; position={firstPlayer.PositionSeconds}; nativePosition={firstPlayer.Player.Position}; hasAudio={firstPlayer.Player.HasAudio}; canPause={firstPlayer.Player.CanPause}; naturalDuration={firstPlayer.Player.NaturalDuration}; status={firstPlayer.StatusText}; error={firstPlayer.ErrorMessage}",error);}
            Require(firstPlayer.IsPlaying&&firstPlayer.PositionSeconds>.05,"manual muted playback advances for the current downloaded track");firstPlayer.Pause();
            Require(!firstPlayer.IsPlaying,"current track pauses without being restarted by editor UI");
            firstPlayer.SeekSlider.Value=1.2;await Task.Delay(150);Click("DownloadTrimMarkStart");
            Require(MediaTimecode.TryParse(Tagged<TextBox>("DownloadTrimStart").Text,out var mark)&&Math.Abs(mark-1.2)<.3,"start marker reads the actual player cursor");
            firstPlayer.SeekSlider.Value=3.1;await Task.Delay(150);Click("DownloadTrimMarkEnd");
            Require(MediaTimecode.TryParse(Tagged<TextBox>("DownloadTrimEnd").Text,out mark)&&Math.Abs(mark-3.1)<.3,"end marker reads the actual player cursor");
            Require(!firstPlayer.IsPlaying,"seeking and choosing trim markers never start playback");
            var startBox=Tagged<TextBox>("DownloadTrimStart");var endBox=Tagged<TextBox>("DownloadTrimEnd");var save=Tagged<Button>("DownloadTrimSave");int before=_jobs.Count;
            startBox.Text="5";endBox.Text="2";save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(_jobs.Count==before,"reversed trim interval cannot enqueue an edit");
            startBox.Text="0";endBox.Text="99999";save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(_jobs.Count==before,"trim interval outside the current track cannot enqueue an edit");
            startBox.Text="1.25";endBox.Text="2.75";
            // Deliberately point the unrelated file-selection state elsewhere. The downloaded editor must own its source.
            _files=new List<string>{other};_metadata=await _engine.InspectAsync(other);_selectionMetadata=new[]{_metadata};
            await Shot("02-current-track-editor.png");save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(_jobs.Count==before+1&&_jobs[^1].Options.Action==ImageAction.AudioTrim&&_jobs[^1].InputPaths.SequenceEqual(new[]{downloaded}),"save-copy queues real audio trim from the downloaded file rather than unrelated global selection");
            var trimmed=_jobs[^1];await FinishInExplicitEditor(trimmed);var trimmedPlayer=await Player();
            Require(trimmed.HistoryStatus==HistoryStatus.Succeeded&&trimmed.Result is{Format:"WAV"}&&trimmed.Result.OutputPath!=downloaded,"trim creates a new WAV copy with explicit output format");
            var cutMetadata=await media.InspectAsync(trimmed.Result!.OutputPath);
            Require(Math.Abs(cutMetadata.DurationSeconds-1.5)<.08,"real native saved copy has the requested 1.5-second duration");
            Require(firstPlayer.IsClosed&&!_jobsVisible&&ReferenceEquals(_focusedDownloadJob,trimmed),"save-copy closes the old player and remains in the current flow");
            Require(Tagged<TextBlock>("DownloadCurrentFile").Text.Contains(Path.GetFileName(trimmed.Result.OutputPath),StringComparison.Ordinal),"saved copy becomes the current editable result in the same panel");
            Require(sink.Payloads.Count==2&&sink.Payloads[^1].GetFileDropList()[0]==trimmed.Result.OutputPath,"saved copy auto-copy uses the new file, never the source or URL");
            before=_jobs.Count;save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Require(_jobs.Count==before,"detached original editor Save cannot enqueue after the current result changes");
            Require(SystemsDigest(downloaded)==originals[downloaded]&&SystemsDigest(other)==originals[other],"trim preserves both the downloaded original and unrelated selection");await Shot("03-trimmed-current-track.png");
            string firstCut=trimmed.Result.OutputPath;string firstCutHash=SystemsDigest(firstCut);
            ShowDownloadJob(first);await _downloadEditorTask;await Player();Tagged<TextBox>("DownloadTrimStart").Text="1.25";Tagged<TextBox>("DownloadTrimEnd").Text="2.75";Click("DownloadTrimSave");
            var collision=_jobs[^1];await FinishInExplicitEditor(collision);await Player();
            Require(collision.Result is{} collisionResult&&collisionResult.OutputPath!=firstCut&&SystemsDigest(firstCut)==firstCutHash,"repeating the edit publishes a unique file without replacing an earlier copy");
            string currentWave=collision.Result!.OutputPath;string currentWaveHash=SystemsDigest(currentWave);int beforeExport=_jobs.Count;int copiedBeforeExport=sink.Payloads.Count;
            Tagged<ComboBox>("DownloadExportFormat").SelectedIndex=0;Click("DownloadExportSave");var exported=_jobs[^1];
            Require(_jobs.Count==beforeExport+1&&exported.Options.Action==ImageAction.AudioMp3&&exported.InputPaths.SequenceEqual(new[]{currentWave}),"full-copy export owns the current WAV source rather than the unrelated selection or previous MP3");
            await FinishInExplicitEditor(exported);await Player();var exportedMetadata=await media.InspectAsync(exported.Result!.OutputPath);
            Require(exported.HistoryStatus==HistoryStatus.Succeeded&&exported.Result.Format=="MP3"&&Math.Abs(exportedMetadata.DurationSeconds-1.5)<.15&&SystemsDigest(currentWave)==currentWaveHash,"actual full-copy MP3 export preserves the current track duration and source bytes");
            Require(sink.Payloads.Count==copiedBeforeExport+1&&sink.Payloads[^1].GetFileDropList()[0]==exported.Result.OutputPath&&!_jobsVisible,"full-copy export remains in the current editor and copies exactly its new MP3");
            Require(originals.All(pair=>SystemsDigest(pair.Key)==pair.Value),"actual native edits preserve SHA256 of all original fixtures and downloaded source files");

            started=Gate();var cancelStarted=started;
            _downloadOverride=async(_,progress,ct)=>{progress.Report(new DownloadProgress(null,"Ожидание отмены"));cancelStarted.TrySetResult();await Task.Delay(Timeout.Infinite,ct);throw new InvalidOperationException("unreachable");};
            var cancelled=Submit("https://example.com/track/cancel");await cancelStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));int copied=sink.Payloads.Count;Click("DownloadCancel");await Finish();
            Require(cancelled.HistoryStatus==HistoryStatus.Cancelled&&cancelled.Result is null&&sink.Payloads.Count==copied,"focused cancellation stops only that download without result or clipboard change");
            Require(ReferenceEquals(_focusedDownloadJob,cancelled)&&Find<Button>("DownloadRetry") is not null&&!_jobsVisible,"cancelled flow stays visible with retry rather than jumping to history");
            _downloadOverride=(request,_,_)=>Task.FromResult(Copy(request));int countBeforeRetry=_jobs.Count;Click("DownloadRetry");
            if(Find<Button>("DownloadStart") is not null){Require(Tagged<TextBox>("DownloadUrl").Text==cancelled.Download!.Url,"retry form retains the cancelled URL for explicit submission");Click("DownloadStart");}
            Require(_jobs.Count==countBeforeRetry+1&&_jobs[^1].Download?.Url==cancelled.Download!.Url,"retry creates a new job for the same URL without mutating the cancelled record");
            await FinishInExplicitEditor(_jobs[^1]);await Player();Require(cancelled.HistoryStatus==HistoryStatus.Cancelled&&_jobs[^1].HistoryStatus==HistoryStatus.Succeeded,"retry can complete after cancellation while retaining the original cancelled history entry");
            _downloadOverride=(_,_,_)=>Task.FromException<DownloadResult>(new InvalidOperationException("Тестовая ошибка загрузки"));_browserUrl="https://example.com/track/failure";var failed=Submit(_browserUrl);await Finish();
            Require(failed.HistoryStatus==HistoryStatus.Failed&&Find<Button>("DownloadRetry") is not null&&!_jobsVisible,"failed download remains in focused error/retry flow");
            Require(_toast.Visibility!=Visibility.Visible,"current failed download keeps its inline error visible without a toast over retry controls");

            // Every gate is released in finally so a failed assertion cannot strand Quit or the serial queue.
            async Task<JobEntry> StartHeld(string url)
            {
                var enter=Gate();var done=Gate();release.Add(()=>done.TrySetResult());
                _downloadOverride=async(request,progress,ct)=>{progress.Report(new(.2,"Старое скачивание"));enter.TrySetResult();await done.Task.WaitAsync(ct);return Copy(request);};
                var job=Submit(url);await enter.Task.WaitAsync(TimeSpan.FromSeconds(5));return job;
            }
            var older=await StartHeld("https://example.com/track/old-url");ShowDownloadPanel(DownloadMode.AudioMp3,"https://example.com/track/new-url");
            var newerAddress=Tagged<TextBox>("DownloadUrl");int newerVersion=_comparisonVersion;release[^1]();await Finish();
            Require(older.HistoryStatus==HistoryStatus.Succeeded&&_comparisonVersion==newerVersion&&ReferenceEquals(Find<TextBox>("DownloadUrl"),newerAddress)&&newerAddress.Text.EndsWith("new-url",StringComparison.Ordinal)&&Find<MediaPreviewPane>("DownloadCurrentPlayer") is null,
                "old completion cannot navigate over a newer URL form or attach its player");
            older=await StartHeld("https://example.com/track/old-settings");ShowSettings();var settingsFirst=_panelContent.Children[0];int settingsVersion=_comparisonVersion;release[^1]();await Finish();
            Require(older.HistoryStatus==HistoryStatus.Succeeded&&_comparisonVersion==settingsVersion&&ReferenceEquals(_panelContent.Children[0],settingsFirst)&&Find<MediaPreviewPane>("DownloadCurrentPlayer") is null,
                "old completion cannot replace settings with its editor");
            older=await StartHeld("https://example.com/track/old-files");Require(await LoadFilesAsync(new[]{image}),"new real local file selection loads while an old download is running");int localVersion=_comparisonVersion;release[^1]();await Finish();
            Require(older.HistoryStatus==HistoryStatus.Succeeded&&_files.SequenceEqual(new[]{image})&&_metadata?.Path==image&&_comparisonVersion==localVersion&&Find<MediaPreviewPane>("DownloadCurrentPlayer") is null,
                "old completion cannot overwrite a newer local file selection or reopen its card");
            older=await StartHeld("https://example.com/track/old-history");Click("DownloadHistory");Require(_jobsVisible,"history is opened only by the explicit separate history command");release[^1]();await Finish();
            Require(older.HistoryStatus==HistoryStatus.Succeeded&&_jobsVisible&&Find<MediaPreviewPane>("DownloadCurrentPlayer") is null,"old completion respects explicit history navigation");
            older=await StartHeld("https://example.com/track/old-closed");ClosePanel();release[^1]();await Finish();
            Require(older.HistoryStatus==HistoryStatus.Succeeded&&_panel.Visibility!=Visibility.Visible&&Find<MediaPreviewPane>("DownloadCurrentPlayer") is null,"closed flow stays closed after late completion");

            var metadataEntered=Gate();var metadataRelease=Gate();release.Add(()=>metadataRelease.TrySetResult());
            _downloadEditorInspectorOverride=async(path,_)=>{metadataEntered.TrySetResult();await metadataRelease.Task;return await _engine.InspectAsync(path);};
            _downloadOverride=(request,_,_)=>Task.FromResult(Copy(request));var probing=Submit("https://example.com/track/old-metadata-settings");
            await metadataEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));var oldEditorWork=_downloadEditorTask;
            Require(probing.Finished&&probing.HistoryStatus==HistoryStatus.Succeeded,"metadata race starts after the actual download has already finished");
            ShowSettings();var metadataSettings=_panelContent.Children[0];int metadataVersion=_comparisonVersion;metadataRelease.TrySetResult();await oldEditorWork.WaitAsync(TimeSpan.FromSeconds(15));
            Require(_comparisonVersion==metadataVersion&&ReferenceEquals(_panelContent.Children[0],metadataSettings)&&Find<MediaPreviewPane>("DownloadCurrentPlayer") is null,"cancel-ignoring late metadata cannot replace newer settings with an old editor");

            metadataEntered=Gate();metadataRelease=Gate();var metadataGate=metadataRelease;var metadataEntry=metadataEntered;int inspectorCalls=0;release.Add(()=>metadataGate.TrySetResult());
            _downloadEditorInspectorOverride=async(path,_)=>{if(++inspectorCalls==1){metadataEntry.TrySetResult();await metadataGate.Task;}return await _engine.InspectAsync(path);};
            _downloadOverride=(request,_,_)=>Task.FromResult(Copy(request));probing=Submit("https://example.com/track/old-metadata-new-job");await metadataEntry.Task.WaitAsync(TimeSpan.FromSeconds(10));oldEditorWork=_downloadEditorTask;
            var newerJob=await StartHeld("https://example.com/track/new-focused-job");int newerJobVersion=_comparisonVersion;metadataGate.TrySetResult();await oldEditorWork.WaitAsync(TimeSpan.FromSeconds(15));
            Require(ReferenceEquals(_focusedDownloadJob,newerJob)&&_comparisonVersion==newerJobVersion&&newerJob.HistoryStatus==HistoryStatus.Running&&Find<MediaPreviewPane>("DownloadCurrentPlayer") is null,"late metadata from a finished old job cannot hijack a newer running progress card");
            release[^1]();await Finish();await Player();_downloadEditorInspectorOverride=null;
            Require(ReferenceEquals(_focusedDownloadJob,newerJob)&&Tagged<TextBlock>("DownloadCurrentFile").Text==Path.GetFileName(newerJob.Result!.OutputPath),"only the newer job can eventually attach its own completed-file editor");
            _resultClipboard=new ResultClipboard(Dispatcher,new DownloadsBusyClipboard());
            _downloadOverride=(request,_,_)=>Task.FromResult(Copy(request));var busy=Submit("https://example.com/track/busy-current-clipboard");await Finish();await Player();
            Require(busy.HistoryStatus==HistoryStatus.Succeeded&&busy.AutomaticCopySucceeded==false&&Find<TextBlock>("DownloadCurrentCopyStatus") is{IsVisible:true} copyStatus
                &&copyStatus.Text.Contains("недоступ",StringComparison.OrdinalIgnoreCase)&&Find<Button>("DownloadCurrentCopy") is not null,"busy automatic clipboard leaves a visible warning and manual copy action in the current editor");
            _resultClipboard=new ResultClipboard(Dispatcher,sink);int beforeManualCopy=sink.Payloads.Count;Click("DownloadCurrentCopy");await Task.WhenAll(_resultToolTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(15));
            Require(sink.Payloads.Count==beforeManualCopy+1&&sink.Payloads[^1].GetFileDropList()[0]==busy.Result!.OutputPath,"current editor Copy recovers from automatic clipboard failure using the actual current file");await Shot("04-busy-clipboard-current-editor.png");
            _downloadEditorInspectorOverride=(path,_)=>Task.FromResult(new ToolkitMetadata(path,"MP3",0,0,new FileInfo(path).Length,ToolkitKind.Audio,double.NaN));
            _downloadOverride=(request,_,_)=>Task.FromResult(Copy(request));var invalidMetadata=Submit("https://example.com/track/invalid-metadata");await Finish();
            Require(invalidMetadata.HistoryStatus==HistoryStatus.Succeeded&&File.Exists(invalidMetadata.Result!.OutputPath)&&Find<MediaPreviewPane>("DownloadCurrentPlayer") is null
                &&Find<Button>("DownloadRetry") is{Visibility:Visibility.Visible}&&Find<TextBlock>("DownloadProblem") is{Visibility:Visibility.Visible},"invalid nonfinite editor metadata preserves the finished file and shows a usable retry without a stalled player");
            _downloadEditorInspectorOverride=null;
            ShowDownloadJob(first);await _downloadEditorTask;await Player();
            Tagged<TextBox>("DownloadTrimStart").Text="0";Tagged<TextBox>("DownloadTrimEnd").Text=(sourceMetadata.DurationSeconds+.0005).ToString("R",CultureInfo.InvariantCulture);
            SetQueuePaused(true);int beforeBoundary=_jobs.Count;Click("DownloadTrimSave");
            Require(_jobs.Count==beforeBoundary+1&&_jobs[^1].Options.TrimEndSeconds is{} clampedEnd&&Math.Abs(clampedEnd-sourceMetadata.DurationSeconds)<1e-9,"sub-millisecond end rounding is accepted and queued options clamp to actual source duration");
            var boundary=_jobs[^1];boundary.Cancellation.Cancel();await Finish();SetQueuePaused(false);
            Require(boundary.HistoryStatus==HistoryStatus.Cancelled&&boundary.Result is null,"boundary validation fixture cancels while queued without publishing another copy");
            ShowDownloadJob(first);await _downloadEditorTask;await Player();
            Tagged<TextBox>("DownloadTrimStart").Text="0";Tagged<TextBox>("DownloadTrimEnd").Text=(sourceMetadata.DurationSeconds+.002).ToString("R",CultureInfo.InvariantCulture);beforeBoundary=_jobs.Count;Click("DownloadTrimSave");
            Require(_jobs.Count==beforeBoundary,"end more than one millisecond past actual duration is rejected rather than silently clamped");
            var originalVideo=await media.InspectAsync(video);
            _downloadOverride=(request,_,ct)=>{
                ct.ThrowIfCancellationRequested();var path=Path.Combine(request.OutputDirectory,"скачанное видео.mp4");File.Copy(video,path);originals[path]=SystemsDigest(path);
                return Task.FromResult(new DownloadResult(path,new FileInfo(path).Length,request.Mode,"MP4",originalVideo.Width,originalVideo.Height,originalVideo.DurationSeconds));
            };
            var videoDownload=Submit("https://example.com/video/current",DownloadMode.VideoMp4);await Finish();var videoPlayer=await Player();string downloadedVideo=videoDownload.Result!.OutputPath;
            Require(videoDownload.HistoryStatus==HistoryStatus.Succeeded&&videoPlayer.IsVideo&&!_jobsVisible&&Tagged<TextBlock>("DownloadCurrentFile").Text==Path.GetFileName(downloadedVideo),"actual downloaded MP4 opens its own muted video editor without navigating to history");
            Tagged<TextBox>("DownloadTrimStart").Text="0.5";Tagged<TextBox>("DownloadTrimEnd").Text="2";int beforeVideoEdit=_jobs.Count;Click("DownloadTrimSave");var videoCut=_jobs[^1];
            Require(_jobs.Count==beforeVideoEdit+1&&videoCut.Options.Action==ImageAction.VideoTrim&&videoCut.InputPaths.SequenceEqual(new[]{downloadedVideo}),"video save-copy uses the current downloaded video path and VideoTrim operation");
            await FinishInExplicitEditor(videoCut);var videoCutMetadata=await media.InspectAsync(videoCut.Result!.OutputPath);var videoCutPlayer=await Player();
            Require(videoCut.HistoryStatus==HistoryStatus.Succeeded&&videoCut.Result.Format=="MP4"&&Math.Abs(videoCutMetadata.DurationSeconds-1.5)<.12&&videoCutMetadata.Width==160&&videoCutMetadata.Height==100,"real native video trim publishes a 1.5-second H264/AAC MP4 with the original frame size");
            Require(videoPlayer.IsClosed&&videoCutPlayer.IsVideo&&!_jobsVisible&&ReferenceEquals(_focusedDownloadJob,videoCut)&&sink.Payloads[^1].GetFileDropList()[0]==videoCut.Result.OutputPath,"video trim remains in the current editor, releases its source player and copies only the new result");await Shot("05-current-video-copy.png");
            Require(originals.All(pair=>SystemsDigest(pair.Key)==pair.Value)&&SystemsDigest(firstCut)==firstCutHash,"cancellation, retry and stale navigation preserve every completed file and original source hash");
            await FlushHistoryAsync();var restored=await new HistoryStore(_historyStore.FilePath).LoadAsync();
            Require(restored.Error is null&&restored.Entries.Count==_jobs.Count&&restored.Entries.Any(entry=>entry.Id==first.Id&&entry.Download?.Url==first.Download!.Url)
                &&restored.Entries.Any(entry=>entry.Id==trimmed.Id&&entry.SourcePaths.SequenceEqual(new[]{downloaded})&&entry.Result?.OutputPath==firstCut),"isolated durable history distinguishes URL downloads from local trim copies with their exact results");
            ShowJobs();_jobSearchBox!.Text="";await Shot("06-separate-download-history.png");
            Require(_jobsVisible&&_jobs.All(job=>job.Finished)&&!_historyBlocked,"final separate history remains usable with all download and edit states complete");
            File.WriteAllText(Path.Combine(destination,"passed.json"),JsonSerializer.Serialize(new{checks=checks.Count,items=checks,run,presentation="explicit production job-details/editor route",defaultRadialWorkspaceCoveredBy="--radial-workspace-smoke",network=false,downloadExecution="injected copies of real native MP3/MP4",actualNativeAudioTrim=true,actualNativeVideoTrim=true,actualWpfMediaPlayback=true,playbackMuted=true,autoplay=false,listeningPerformed=false,userProfileIo=false,nativeClipboardWrites=false,physicalInput=false},new JsonSerializerOptions{WriteIndented=true}));
        }
        catch(Exception error){App.Log(error);Directory.CreateDirectory(destination);File.WriteAllText(Path.Combine(destination,"error.txt"),error+"\n"+string.Join("\n",checks));}
        finally
        {
            foreach(var unblock in release)unblock();_downloadOverride=null;_downloadEditorInspectorOverride=null;_browserWindowOverride=null;_browserUrlOverride=null;_invocationForegroundOverride=null;
            foreach(var job in _jobs.Where(job=>!job.Finished))job.Cancellation.Cancel();SetQueuePaused(false);Quit();
        }
    }

    private static void WriteDownloadFlowWave(string path,double seconds,double frequency)
    {
        const int rate=44100;int samples=checked((int)Math.Round(seconds*rate));using var writer=new BinaryWriter(File.Create(path),Encoding.ASCII);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));writer.Write(36+samples*2);writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);writer.Write((short)1);writer.Write((short)1);writer.Write(rate);writer.Write(rate*2);writer.Write((short)2);writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));writer.Write(samples*2);for(int index=0;index<samples;index++)writer.Write((short)Math.Round(Math.Sin(2*Math.PI*frequency*index/rate)*4500));
    }
    [DllImport("user32.dll",EntryPoint="IsWindowVisible")]private static extern bool DownloadSmokeIsWindowVisible(IntPtr window);
}
