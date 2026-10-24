using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using Orbit.Core.Media;
using Orbit.Core.Presets;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private async Task RunMediaTracksSmokeAsync()
    {
        var destination=Path.Combine(AppContext.BaseDirectory,"media-tracks-smoke");
        var run=Path.Combine(destination,"run-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        var checks=new List<string>();bool gateHeld=false;
        void Require(bool value,string text){if(!value)throw new InvalidOperationException("Media tracks integration: "+text);checks.Add(text);}
        Button Named(string text)=>SystemsDescendants<Button>(_panelContent).Single(b=>b.Content as string==text);
        void Click(string text)=>Named(text).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ComboBox Tagged(string tag)=>SystemsDescendants<ComboBox>(_panelContent).Single(c=>c.Tag as string==tag);
        async Task EditorsAsync()=>await Task.WhenAll(_resultToolTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(90));
        async Task FinishAsync(){await Task.WhenAll(_jobTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(120));await FlushHistoryAsync();}
        async Task ShotAsync(string name){_toast.Visibility=Visibility.Collapsed;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);Capture(Path.Combine(destination,name));}
        try{
            Directory.CreateDirectory(run);File.Delete(Path.Combine(destination,"passed.json"));File.Delete(Path.Combine(destination,"error.txt"));
            Require(IsDiagnosticRun&&_bridge is null&&_jobs.Count==0&&!ProductivityIoAllowed&&!ChainIoAllowed,"diagnostic avoids user stores, native hotkeys, clipboard and shell integration");
            var video=Path.Combine(run,"две дорожки.mkv");var cover=Path.Combine(run,"обложка.png");var album=Path.Combine(run,"альбом.mp3");var audio=Path.Combine(run,"без обложки.wav");
            await RunMediaTracksFixtureAsync(["-f","lavfi","-i","color=c=blue:s=160x100:r=20:d=4","-f","lavfi","-i","sine=frequency=220:sample_rate=44100:duration=4","-f","lavfi","-i","sine=frequency=880:sample_rate=48000:duration=4",
                "-map","0:v","-map","1:a","-map","2:a","-c:v","libx264","-pix_fmt","yuv420p","-c:a","flac","-ac:a:1","2","-metadata:s:a:0","language=rus","-metadata:s:a:0","title=Первый звук","-metadata:s:a:1","language=eng","-metadata:s:a:1","title=Second sound",video]);
            WriteResultsImage(cover);await WriteEditingMediaAsync(audio,false);
            await RunMediaTracksFixtureAsync(["-i",audio,"-i",cover,"-map","0:a","-map","1:v","-c:a","libmp3lame","-b:a","192k","-c:v","png","-disposition:v:0","attached_pic",album]);
            var originals=new[]{video,cover,album,audio}.ToDictionary(p=>p,SystemsDigest);
            _historyStore=new HistoryStore(Path.Combine(run,"history.json"));_presetStore=new PresetStore(Path.Combine(run,"presets.json"));_allowDiagnosticProductivityIo=true;
            _outputDirectory=Path.Combine(run,"output");Directory.CreateDirectory(_outputDirectory);_resultOpenOverride=(_,_)=>Task.CompletedTask;
            Require(await LoadFilesAsync(new[]{video}),"real multitrack video loads");SetCompactMode(true);ShowMoreTools();
            Require(SystemsDescendants<Button>(_panelContent).Any(b=>b.Content as string=="Извлечь звуковую дорожку…")&&SystemsDescendants<Button>(_panelContent).Any(b=>b.Content as string=="Потоки медиафайла…"),"compact tools exposes track extraction and stream information");
            ShowMediaTracks(false);await EditorsAsync();
            Require(SystemsDescendants<TextBlock>(_panelContent).Any(t=>t.Text.Contains("#2 · Аудио"))&&SystemsDescendants<TextBlock>(_panelContent).Any(t=>t.Text.Contains("Second sound")),"stream details show actual global indexes and language/title metadata");
            await ShotAsync("01-streams.png");Click("Извлечь звуковую дорожку…");await EditorsAsync();
            var tracks=Tagged("audio-track");Require(tracks.Items.Count==2&&((TrackChoice)tracks.Items[1]).Track.StreamIndex==2,"selector lists both audio tracks without video stream");
            tracks.SelectedIndex=1;Tagged("track-format").SelectedItem="WAV";
            Require(!Tagged("track-bitrate").IsEnabled,"lossless formats disable irrelevant MP3 bitrate");await ShotAsync("02-track-export.png");
            Click("Сохранить выбранную дорожку");await FinishAsync();var extracted=_jobs.Last();
            Require(extracted.HistoryStatus==HistoryStatus.Succeeded&&extracted.Result?.Format=="WAV"&&extracted.Options.AudioTrackStreamIndex==2,"queue routes explicitly chosen second track into a real WAV");
            var probe=await ProbeMediaExportAsync(extracted.Result!.OutputPath);var streams=probe.GetProperty("streams").EnumerateArray().ToArray();
            Require(streams.Length==1&&streams[0].GetProperty("channels").GetInt32()==2&&streams[0].GetProperty("sample_rate").GetString()=="48000","selected second track preserves stereo and 48 kHz, distinct from first mono 44.1 kHz track");
            Require(!PresetCatalog.CanSave(extracted.Options)&&_repeatRecipe==null&&!CanCompare(extracted),"source-bound track choice never becomes repeat, favorite, preset or image comparison");
            ShowMediaTracks(true);await EditorsAsync();Tagged("audio-track").SelectedIndex=0;Tagged("track-bitrate").SelectedItem=256;Click("Сохранить выбранную дорожку");await FinishAsync();
            Require(_jobs.Last() is{HistoryStatus:HistoryStatus.Succeeded,Options.AudioTrackFormat:"MP3",Options.AudioTrackBitrateKbps:256},"MP3 format and chosen bitrate flow through the queue");
            ShowMediaTracks(true);ShowFileInfo();await EditorsAsync();
            Require(SystemsDescendants<TextBlock>(_panelContent).Any(t=>t.Text=="Сведения")&&!SystemsDescendants<ComboBox>(_panelContent).Any(c=>c.Tag as string=="audio-track"),"late inspection cannot replace another panel");
            ShowMediaTracks(true);await EditorsAsync();var stale=Named("Сохранить выбранную дорожку");int count=_jobs.Count;
            Require(await LoadFilesAsync(new[]{album}),"audio with attached cover loads as audio");stale.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(_jobs.Count==count,"stale track control cannot process a newly selected file");
            ShowMoreTools();Require(SystemsDescendants<Button>(_panelContent).Any(b=>b.Content as string=="Сохранить обложку…"),"audio tools expose embedded cover extraction");
            ShowAudioCover();await ShotAsync("03-cover.png");Click("Сохранить обложку PNG");await FinishAsync();var coverJob=_jobs.Last();
            Require(coverJob.HistoryStatus==HistoryStatus.Succeeded&&coverJob.Result?.Format=="PNG","embedded cover actually publishes a PNG result");
            var before=await _engine.InspectAsync(cover);var after=await _engine.InspectAsync(coverJob.Result!.OutputPath);
            Require(before.Width==after.Width&&before.Height==after.Height,"cover dimensions are preserved");
            Require(PresetCatalog.CanSave(coverJob.Options)&&_repeatRecipe?.Options.Action==ImageAction.AudioCover,"cover extraction is reusable for audio and updates repeat recipe");
            ShowSavePreset(coverJob);_panelContent.Children.OfType<TextBox>().Single().Text="Обложка PNG";Click("Сохранить пресет");
            Require(_customPresets.Single().Options is{Action:ImageAction.AudioCover,AudioTrackStreamIndex:null,AudioTrackSourceSha256:null,OutputDirectory:null},"cover preset contains no source-specific track selection or old output path");
            Require(await LoadFilesAsync(new[]{album,audio}),"mixed cover availability loads as audio batch");ShowAudioCover();Click("Сохранить обложку PNG");await FinishAsync();
            var batch=_jobs.TakeLast(2).ToArray();Require(batch[0].HistoryStatus==HistoryStatus.Succeeded&&batch[1].HistoryStatus==HistoryStatus.Failed&&batch[1].Result==null,"cover batch succeeds where present and reports missing cover per file");
            Require(batch[0].Result!.OutputPath!=coverJob.Result.OutputPath&&File.Exists(coverJob.Result.OutputPath),"duplicate cover extraction uses a unique name without replacing prior result");
            Require(await LoadFilesAsync(new[]{video}),"video reloads for queued cancellation");ShowMediaTracks(true);await EditorsAsync();
            await _queueGate.WaitAsync();gateHeld=true;Click("Сохранить выбранную дорожку");var canceled=_jobs.Last();canceled.Cancellation.Cancel();_queueGate.Release();gateHeld=false;await FinishAsync();
            Require(canceled.HistoryStatus==HistoryStatus.Cancelled&&canceled.Result==null,"queued track extraction cancels without a published file");
            var history=await new HistoryStore(_historyStore.FilePath).LoadAsync();
            Require(history.Error==null&&history.Entries.Count==_jobs.Count,"history round-trips track, cover, missing-cover and canceled tasks");
            Require(history.Entries.Any(e=>e.Options is{Action:ImageAction.AudioTrackExtract,AudioTrackStreamIndex:2,AudioTrackFormat:"WAV",AudioTrackSourceSha256:{Length:64}}),"durable history retains exact stream index and inspected source fingerprint");
            Require(originals.All(p=>SystemsDigest(p.Key)==p.Value),"all sources retain their original SHA-256 hashes");
            Require(!Directory.GetDirectories(_outputDirectory).Any(p=>Path.GetFileName(p).StartsWith(".orbit-",StringComparison.Ordinal)),"completed and canceled jobs remove private staging");
            File.WriteAllText(Path.Combine(destination,"passed.json"),JsonSerializer.Serialize(new{checks=checks.Count,items=checks,run,userProfileIo=false,nativeClipboardWrites=false,registryWrites=false,nativeShellLaunches=0,physicalInput=false,listeningPerformed=false},new JsonSerializerOptions{WriteIndented=true}));
        }catch(Exception ex){App.Log(ex);File.WriteAllText(Path.Combine(destination,"error.txt"),ex.ToString());}
        finally{if(gateHeld)_queueGate.Release();Quit();}
    }
    private static async Task RunMediaTracksFixtureAsync(string[] arguments)
    {
        var start=new ProcessStartInfo(new MediaEngine().ExecutablePath){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var argument in new[]{"-hide_banner","-v","error","-nostdin","-n"}.Concat(arguments))start.ArgumentList.Add(argument);
        using var process=Process.Start(start)??throw new InvalidOperationException("Cannot start fixture generator");
        var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
        try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));await Task.WhenAll(output,error);if(process.ExitCode!=0)throw new InvalidOperationException(await error);}
        finally{if(!process.HasExited)process.Kill(true);}
    }
}
