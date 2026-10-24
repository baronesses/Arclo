using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Orbit.App.Controls;
using Orbit.App.Editing;
using Orbit.App.History;
using Orbit.App.Platform;
using Orbit.App.Presets;
using Orbit.Core;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private async Task RunTrimTimelineSmokeAsync()
    {
        var destination=Path.Combine(AppContext.BaseDirectory,"trim-timeline-smoke");
        var run=Path.Combine(destination,"run-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        var checks=new List<string>();var metrics=new List<object>();
        var originals=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        void Require(bool value,string message){if(!value)throw new InvalidOperationException("Trim timeline: "+message);checks.Add(message);}
        T Tagged<T>(string tag)where T:FrameworkElement=>SystemsDescendants<T>(_panelContent).Single(element=>element.Tag as string==tag);
        void Click(string tag)=>Tagged<Button>(tag).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        async Task Render(){await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);}
        async Task Until(Func<bool> condition,string reason,int seconds=15)
        {
            var watch=Stopwatch.StartNew();while(!condition()&&watch.Elapsed<TimeSpan.FromSeconds(seconds))await Task.Delay(20);
            if(!condition())throw new TimeoutException(reason);await Render();
        }
        async Task Finish()
        {
            await Task.WhenAll(_jobTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(90));
            await _radialWorkspaceTask.WaitAsync(TimeSpan.FromSeconds(30));
            await Task.WhenAll(_resultToolTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(45));
            await FlushHistoryAsync();await Render();
        }
        async Task Shot(string name){_toast.Visibility=Visibility.Collapsed;await Render();Capture(Path.Combine(destination,name));}
        bool Circle(string path)=>_radialWorkspaceVisible&&_radialWorkspacePath==path&&_panel.Visibility!=Visibility.Visible
            &&_radialView.Visibility==Visibility.Visible&&!_jobsVisible;
        static bool Near(double value,double expected,double tolerance=.002)=>Math.Abs(value-expected)<=tolerance;
        bool Fields(double from,double to)=>MediaTimecode.TryParse(Tagged<TextBox>("DownloadTrimStart").Text,out double start)
            &&MediaTimecode.TryParse(Tagged<TextBox>("DownloadTrimEnd").Text,out double end)&&Near(start,from)&&Near(end,to);
        static double CenterX(Thumb thumb,TrimTimeline timeline)=>thumb.TranslatePoint(new Point(thumb.ActualWidth/2,thumb.ActualHeight/2),timeline).X;
        async Task Drag(TrimTimeline timeline,Thumb startThumb,Thumb endThumb,bool start,double target)
        {
            // Use the arranged track's coordinate system: layout rounding can move a
            // thumb center by a DIP, especially for a short selection on a long track.
            await Render();double scale=timeline.TrackBounds.Width/timeline.DurationSeconds;
            if(!double.IsFinite(scale)||scale<=0)throw new InvalidOperationException("Timeline handles do not provide a positive rendered DIP-per-second scale");
            var thumb=start?startThumb:endThumb;double current=start?timeline.StartSeconds:timeline.EndSeconds;
            double delta=(target-current)*scale;
            thumb.RaiseEvent(new DragStartedEventArgs(0,0){RoutedEvent=Thumb.DragStartedEvent});
            thumb.RaiseEvent(new DragDeltaEventArgs(delta,0){RoutedEvent=Thumb.DragDeltaEvent});
            thumb.RaiseEvent(new DragCompletedEventArgs(delta,0,false){RoutedEvent=Thumb.DragCompletedEvent});await Render();
        }
        bool FullyVisible(FrameworkElement element)
        {
            if(_panel.Child is not ScrollViewer scroll||element.ActualWidth<=0||element.ActualHeight<=0)return false;
            var bounds=element.TransformToAncestor(scroll).TransformBounds(new Rect(0,0,element.ActualWidth,element.ActualHeight));
            return bounds.Left>=0&&bounds.Top>=0&&bounds.Right<=scroll.ActualWidth+1&&bounds.Bottom<=scroll.ActualHeight+1;
        }
        async Task<(TrimTimeline Timeline,Thumb Start,Thumb End,MediaPreviewPane Player,double Duration)> Open(string path)
        {
            var data=new DataObject();data.SetData(DataFormats.FileDrop,new[]{path});await ApplyClipboardPayloadAsync(RadialClipboardReader.Parse(data));
            Require(Circle(path),"owned media paste selects the current circle without automatically opening an editor");
            var metadata=_radialWorkspaceMetadata??throw new InvalidOperationException("Current media metadata was not loaded");
            await RunRadialCommandAsync(RadialCommand.Trim);await Render();
            var timeline=Tagged<TrimTimeline>("DownloadTrimTimeline");var pane=Tagged<MediaPreviewPane>("DownloadCurrentPlayer");
            await Until(()=>pane.IsOpened||pane.ErrorMessage is not null||pane.IsClosed,"explicit current-media player opens",20);
            Require(pane.IsOpened&&pane.IsMuted&&!pane.IsPlaying&&pane.Player.Source?.LocalPath==path,
                "explicit Trim opens the real current media muted without autoplay");
            var thumbs=SystemsDescendants<Thumb>(timeline).OrderBy(thumb=>CenterX(thumb,timeline)).ToArray();
            Require(thumbs.Length==2&&timeline.ActualWidth>150&&Near(timeline.StartSeconds,0)&&Near(timeline.EndSeconds,metadata.DurationSeconds),
                "actual WPF timeline renders two A/B handles spanning the current source duration");
            return(timeline,thumbs[0],thumbs[1],pane,metadata.DurationSeconds);
        }
        try
        {
            Directory.CreateDirectory(run);File.Delete(Path.Combine(destination,"passed.json"));File.Delete(Path.Combine(destination,"error.txt"));
            Require(IsDiagnosticRun&&_bridge is null&&!ProductivityIoAllowed&&!PersonalizationIoAllowed,
                "diagnostic starts without user settings, history, tray or native clipboard access");
            Require(Path.GetTempPath().StartsWith(AppContext.BaseDirectory,StringComparison.OrdinalIgnoreCase),"all temporary work belongs to the isolated portable diagnostic");
            var fixtures=Path.Combine(run,"fixtures");Directory.CreateDirectory(fixtures);_outputDirectory=Path.Combine(run,"Новые фрагменты [1]");Directory.CreateDirectory(_outputDirectory);
            _historyStore=new HistoryStore(Path.Combine(run,"history.json"));_presetStore=new PresetStore(Path.Combine(run,"presets.json"));_allowDiagnosticProductivityIo=true;
            var sink=new ResultsClipboardCapture(Dispatcher);_resultClipboard=new ResultClipboard(Dispatcher,sink);
            var wave=Path.Combine(fixtures,"длинный источник.wav");WriteDownloadFlowWave(wave,32,440);
            var video=Path.Combine(fixtures,"исходное видео.mp4");await WriteEditingMediaAsync(video,true);
            var media=new MediaEngine();var encoded=await media.ExecuteAsync(wave,MediaOperation.AudioMp3,new MediaOptions{OutputDirectory=fixtures,AudioBitrateKbps=192});string mp3=encoded.OutputPath;
            foreach(var path in new[]{wave,mp3,video})originals[path]=SystemsDigest(path);
            Require((await media.InspectAsync(mp3)).DurationSeconds>31.9,"audio fixture is an actual native MP3 longer than thirty seconds");
            _compactSize=380;SetCompactMode(true);var audio=await Open(mp3);var timeline=audio.Timeline;
            await Drag(timeline,audio.Start,audio.End,false,2);await Drag(timeline,audio.Start,audio.End,true,.5);
            Require(Near(timeline.StartSeconds,.5)&&Near(timeline.EndSeconds,2)&&Fields(.5,2),"A/B Thumb drag events update both timeline range and retained numerical fields");
            string intervalText=Tagged<TextBlock>("DownloadTrimRange").Text;
            Require(intervalText.StartsWith("Фрагмент ",StringComparison.Ordinal)&&MediaTimecode.TryParse(intervalText["Фрагмент ".Length..],out double shownDuration)
                &&Near(shownDuration,1.5)&&Near(shownDuration,timeline.EndSeconds-timeline.StartSeconds)&&Tagged<Button>("DownloadTrimSave").IsEnabled,
                "A/B interval displays its 1.5-second duration and enables Save fragment");
            Require(!audio.Player.IsPlaying,"dragging either A/B handle never starts playback");

            var start=Tagged<TextBox>("DownloadTrimStart");var end=Tagged<TextBox>("DownloadTrimEnd");var save=Tagged<Button>("DownloadTrimSave");
            start.Text="1.25";end.Text="2.75";await Render();
            Require(Near(timeline.StartSeconds,1.25)&&Near(timeline.EndSeconds,2.75)&&save.IsEnabled,"editing the retained numerical fields moves both timeline handles");
            double validStart=timeline.StartSeconds,validEnd=timeline.EndSeconds;int count=_jobs.Count;
            start.Text="не время";save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(!save.IsEnabled&&Near(timeline.StartSeconds,validStart)&&Near(timeline.EndSeconds,validEnd)&&_jobs.Count==count,
                "invalid numerical text disables Save without changing the last valid A/B interval or queuing work");
            start.Text="5";end.Text="2";save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(!save.IsEnabled&&Near(timeline.StartSeconds,validStart)&&Near(timeline.EndSeconds,validEnd)&&_jobs.Count==count,
                "reversed numerical interval cannot replace the valid A/B range or enqueue a trim");
            start.Text="0.5";end.Text="2";await Render();
            await Drag(timeline,audio.Start,audio.End,true,audio.Duration*2);
            Require(timeline.StartSeconds>=0&&timeline.StartSeconds<timeline.EndSeconds&&timeline.EndSeconds<=audio.Duration,
                "dragging A beyond B clamps safely and cannot cross the end handle");
            timeline.SetRange(.5,2);await Render();await Drag(timeline,audio.Start,audio.End,false,-audio.Duration);
            Require(timeline.StartSeconds>=0&&timeline.StartSeconds<timeline.EndSeconds&&timeline.EndSeconds<=audio.Duration,
                "dragging B before A clamps safely and cannot cross the start handle");
            timeline.SetRange(.5,2);await Render();
            Require(Fields(.5,2)&&save.IsEnabled,"restoring a valid timeline interval synchronizes numerical fields and save validity");

            audio.Player.SeekSlider.Value=1.1;await Task.Delay(150);Click("DownloadTrimMarkStart");await Render();
            Require(Near(timeline.StartSeconds,audio.Player.PositionSeconds,.3)&&Fields(timeline.StartSeconds,timeline.EndSeconds),
                "From player marker updates A and its numerical field from the actual playback cursor");
            audio.Player.SeekSlider.Value=2.6;await Task.Delay(150);Click("DownloadTrimMarkEnd");await Render();
            Require(Near(timeline.EndSeconds,audio.Player.PositionSeconds,.3)&&Fields(timeline.StartSeconds,timeline.EndSeconds)&&!audio.Player.IsPlaying,
                "To player marker updates B without starting playback");
            timeline.SetRange(.5,2);await Render();double wide=timeline.ActualWidth;
            Require(FullyVisible(save),"audio Save fragment is fully visible at the normal 380 by 600 compact size without scrolling");
            metrics.Add(new{view="audio380",width=ActualWidth,height=ActualHeight,timelineWidth=wide,saveFullyVisible=FullyVisible(save)});await Shot("01-audio-timeline-380.png");
            _compactSize=320;PlacePopup(new Size(320,600));await Render();
            Require(timeline.ActualWidth<wide-20&&timeline.ActualWidth>120&&Near(timeline.StartSeconds,.5)&&Near(timeline.EndSeconds,2),
                "narrow compact resize preserves the A/B time interval while laying out a smaller real timeline");
            await Drag(timeline,audio.Start,audio.End,true,.75);await Drag(timeline,audio.Start,audio.End,true,.5);
            Require(Fields(.5,2)&&!audio.Player.IsPlaying,"A/B drag mapping remains correct after a narrow resize without autoplay");
            metrics.Add(new{view="audio320",width=ActualWidth,height=ActualHeight,timelineWidth=timeline.ActualWidth,saveFullyVisible=FullyVisible(save)});await Shot("02-audio-timeline-320.png");
            _compactSize=380;PlacePopup(new Size(380,600));await Render();
            timeline.SetRange(0,audio.Duration);await Render();await Drag(timeline,audio.Start,audio.End,false,2);await Drag(timeline,audio.Start,audio.End,true,.5);
            count=_jobs.Count;save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));var audioJob=_jobs[^1];
            Require(_jobs.Count==count+1&&audioJob.Options.Action==ImageAction.AudioTrim&&audioJob.InputPaths.SequenceEqual(new[]{mp3})
                &&Near(audioJob.Options.TrimStartSeconds,.5)&&audioJob.Options.TrimEndSeconds is{} audioEnd&&Near(audioEnd,2),
                "Save fragment queues exactly the final dragged A/B interval from the current MP3 rather than the entire track");
            await Finish();var audioOutput=audioJob.Result!.OutputPath;var audioMetadata=await media.InspectAsync(audioOutput);
            Require(audioJob.HistoryStatus==HistoryStatus.Succeeded&&audioJob.Result.Format=="WAV"&&Near(audioMetadata.DurationSeconds,1.5,.08),
                "native audio trim creates an actual 1.5-second WAV from the A/B interval");
            Require(Circle(audioOutput)&&audio.Player.IsClosed&&sink.Payloads.Count==1&&sink.Payloads[^1].GetFileDropList()[0]==audioOutput,
                "audio save closes its old player, copies the new fragment and returns to the current-file circle");
            count=_jobs.Count;save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(_jobs.Count==count,"detached old timeline editor cannot queue another trim after its player is closed");
            metrics.Add(new{operation="AudioTrim",path=audioOutput,duration=audioMetadata.DurationSeconds,format=audioMetadata.Format});await Shot("03-audio-fragment-circle.png");

            var movie=await Open(video);timeline=movie.Timeline;
            await Drag(timeline,movie.Start,movie.End,false,2);await Drag(timeline,movie.Start,movie.End,true,.5);
            Require(Near(timeline.StartSeconds,.5)&&Near(timeline.EndSeconds,2)&&Fields(.5,2)&&Tagged<Button>("DownloadTrimSave").IsEnabled,
                "video A/B drag selects the same requested 0.5-to-2-second interval with valid numerical fields");
            Require(movie.Player.IsVideo&&!movie.Player.IsPlaying,"video timeline drag keeps the real muted preview paused");
            metrics.Add(new{view="video380",width=ActualWidth,height=ActualHeight,timelineWidth=timeline.ActualWidth,saveFullyVisible=FullyVisible(Tagged<Button>("DownloadTrimSave"))});await Shot("04-video-timeline-380.png");
            count=_jobs.Count;Click("DownloadTrimSave");var videoJob=_jobs[^1];
            Require(_jobs.Count==count+1&&videoJob.Options.Action==ImageAction.VideoTrim&&videoJob.InputPaths.SequenceEqual(new[]{video})
                &&Near(videoJob.Options.TrimStartSeconds,.5)&&videoJob.Options.TrimEndSeconds is{} videoEnd&&Near(videoEnd,2),
                "video Save fragment queues the chosen A/B interval from the current MP4");
            await Finish();string videoOutput=videoJob.Result!.OutputPath;var videoMetadata=await media.InspectAsync(videoOutput);
            Require(videoJob.HistoryStatus==HistoryStatus.Succeeded&&videoJob.Result.Format=="MP4"&&Near(videoMetadata.DurationSeconds,1.5,.12)
                &&videoMetadata.Width==160&&videoMetadata.Height==100&&videoMetadata.HasAudio,
                "native video A/B trim creates a 1.5-second H264/AAC MP4 with the original frame dimensions");
            Require(Circle(videoOutput)&&movie.Player.IsClosed&&sink.Payloads.Count==2&&sink.Payloads[^1].GetFileDropList()[0]==videoOutput,
                "video save closes the old player and leaves only its new result in the current circle and injected clipboard");
            metrics.Add(new{operation="VideoTrim",path=videoOutput,duration=videoMetadata.DurationSeconds,width=videoMetadata.Width,height=videoMetadata.Height,format=videoMetadata.Format});await Shot("05-video-fragment-circle.png");
            if(!await LoadFilesAsync(new[]{mp3}))throw new InvalidOperationException("Owned MP3 could not load for the ordinary trim editor");
            ShowMediaTrim(ImageAction.AudioTrim);var ordinaryTimeline=Tagged<TrimTimeline>("MediaTrimTimeline");
            var ordinaryFields=_panelContent.Children.OfType<TextBox>().ToArray();ordinaryFields[0].Text="0.5";ordinaryFields[1].Text="2";
            Require(ordinaryFields.Length==2&&Near(ordinaryTimeline.StartSeconds,.5)&&Near(ordinaryTimeline.EndSeconds,2),
                "ordinary file-tools trim editor also synchronizes retained numerical input with its A/B timeline");ClosePanel();
            Require(originals.All(pair=>File.Exists(pair.Key)&&SystemsDigest(pair.Key)==pair.Value),"all A/B editing and native trims preserve SHA256 of every original source fixture");
            var history=await new HistoryStore(_historyStore.FilePath).LoadAsync();
            Require(history.Error is null&&history.Entries.Count==2&&history.Entries.All(entry=>entry.Status==HistoryStatus.Succeeded)
                &&history.Entries.Any(entry=>entry.Id==audioJob.Id&&entry.SourcePaths.SequenceEqual(new[]{mp3}))
                &&history.Entries.Any(entry=>entry.Id==videoJob.Id&&entry.SourcePaths.SequenceEqual(new[]{video})),
                "isolated history stores the exact successful audio and video A/B jobs separately");
            File.WriteAllText(Path.Combine(destination,"passed.json"),JsonSerializer.Serialize(new{checks=checks.Count,items=checks,run,metrics,
                actualWpfThumbDragEvents=true,actualNativeAudioTrim=true,actualNativeVideoTrim=true,realMediaOpened=true,
                network=false,userProfileIo=false,systemClipboardReads=false,nativeClipboardWrites=false,physicalInput=false,
                playbackMuted=true,autoplay=false,listeningPerformed=false},new JsonSerializerOptions{WriteIndented=true}));
        }
        catch(Exception error){App.Log(error);Directory.CreateDirectory(destination);File.WriteAllText(Path.Combine(destination,"error.txt"),error+"\n"+string.Join("\n",checks));}
        finally{foreach(var job in _jobs.Where(job=>!job.Finished))job.Cancellation.Cancel();SetQueuePaused(false);Quit();}
    }
}
