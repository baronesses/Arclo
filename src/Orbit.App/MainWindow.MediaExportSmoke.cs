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
using Orbit.Core.Presets;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private async Task RunMediaExportSmokeAsync()
    {
        var destination=Path.Combine(AppContext.BaseDirectory,"media-export-smoke");
        var run=Path.Combine(destination,"run-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        var checks=new List<string>();var opened=new List<string>();bool gateHeld=false;
        void Require(bool value,string text){if(!value)throw new InvalidOperationException("Media export integration: "+text);checks.Add(text);}
        Button Named(string text)=>SystemsDescendants<Button>(_panelContent).Single(b=>b.Content as string==text);
        void Click(string text)=>Named(text).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        async Task FinishAsync(){await Task.WhenAll(_jobTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(120));await FlushHistoryAsync();}
        async Task EditorsAsync()=>await Task.WhenAll(_resultToolTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(60));
        async Task ShotAsync(string name){_toast.Visibility=Visibility.Collapsed;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);Capture(Path.Combine(destination,name));}
        void SaveRecipe(JobEntry job,string name){ShowSavePreset(job);_panelContent.Children.OfType<TextBox>().Single().Text=name;Click("Сохранить пресет");}
        ComboBox Tagged(string tag)=>SystemsDescendants<ComboBox>(_panelContent).Single(c=>c.Tag as string==tag);
        try{
            Directory.CreateDirectory(run);File.Delete(Path.Combine(destination,"passed.json"));File.Delete(Path.Combine(destination,"error.txt"));
            Require(IsDiagnosticRun&&_bridge is null&&_jobs.Count==0&&!ProductivityIoAllowed&&!ChainIoAllowed,"diagnostic avoids user stores, native hotkeys, clipboard and shell integration");
            var audio=Path.Combine(run,"первый звук.wav");var other=Path.Combine(run,"второй звук.wav");var video=Path.Combine(run,"видео.mp4");
            await WriteEditingMediaAsync(audio,false);File.Copy(audio,other);await WriteEditingMediaAsync(video,true);
            var originals=new[]{audio,other,video}.ToDictionary(p=>p,SystemsDigest);
            _historyStore=new HistoryStore(Path.Combine(run,"history.json"));_presetStore=new PresetStore(Path.Combine(run,"presets.json"));_allowDiagnosticProductivityIo=true;
            _outputDirectory=Path.Combine(run,"output");Directory.CreateDirectory(_outputDirectory);_resultOpenOverride=(path,_)=>{opened.Add(path);return Task.CompletedTask;};
            Require(await LoadFilesAsync(new[]{audio,other}),"two real WAV files load for audio batch export");SetCompactMode(true);ShowMoreTools();
            Require(SystemsDescendants<Button>(_panelContent).Any(b=>b.Content as string=="Экспорт M4A / Opus / OGG…"),"audio More menu exposes the new codec export card");Click("Экспорт M4A / Opus / OGG…");
            var combos=_panelContent.Children.OfType<ComboBox>().ToArray();combos[1].SelectedItem=256;await ShotAsync("01-aac-export.png");Click("Сохранить аудио");await FinishAsync();
            var aac=_jobs.Where(j=>j.Options.Action==ImageAction.AudioExport).ToArray();
            Require(aac.Length==2&&aac.All(j=>j.HistoryStatus==HistoryStatus.Succeeded&&j.Result?.Format=="M4A"&&j.Options.AudioExportBitrateKbps==256),"audio card creates one actual M4A/AAC file per source with the chosen bitrate");
            var aacProbe=await ProbeMediaExportAsync(aac[0].Result!.OutputPath);var aacStream=aacProbe.GetProperty("streams").EnumerateArray().Single();
            Require(aacStream.GetProperty("codec_name").GetString()=="aac"&&aacStream.GetProperty("sample_rate").GetString()=="44100","independent ffprobe confirms actual AAC and preserved 44.1kHz source rate");
            await OpenKnownResultAsync(aac[0].Result!.OutputPath,false);Require(opened.Last()==aac[0].Result!.OutputPath,"M4A result Open uses the validated intercepted viewer route");
            SaveRecipe(aac[0],"AAC 256");Require(_customPresets.Single().Options is{Action:ImageAction.AudioExport,AudioExportFormat:"M4A",AudioExportBitrateKbps:256,OutputDirectory:null},"AAC export stores only reusable parameters without old output paths");
            ShowAudioExport();combos=_panelContent.Children.OfType<ComboBox>().ToArray();combos[1].SelectedItem=320;combos[0].SelectedIndex=1;
            Require(combos[1].Items.Cast<int>().All(v=>v<=256)&&combos[1].SelectedItem is 192,"changing AAC to Opus removes unsupported 320k and selects a valid bitrate");
            combos[1].SelectedItem=96;await ShotAsync("02-opus-export.png");Click("Сохранить аудио");await FinishAsync();var opus=_jobs.TakeLast(2).ToArray();
            Require(opus.All(j=>j.HistoryStatus==HistoryStatus.Succeeded&&j.Result?.Format=="OPUS"&&Path.GetExtension(j.Result!.OutputPath)==".opus"),"Opus batch produces actual successful .opus results");
            var opusProbe=await ProbeMediaExportAsync(opus[0].Result!.OutputPath);var opusStream=opusProbe.GetProperty("streams").EnumerateArray().Single();
            Require(opusProbe.GetProperty("format").GetProperty("format_name").GetString()=="ogg"&&opusStream.GetProperty("codec_name").GetString()=="opus"&&opusStream.GetProperty("sample_rate").GetString()=="48000","independent ffprobe confirms Ogg Opus and its 48kHz rate");
            await OpenKnownResultAsync(opus[0].Result!.OutputPath,false);Require(opened.Last()==opus[0].Result!.OutputPath,"Opus result Open uses the validated intercepted viewer route");SaveRecipe(opus[0],"Opus 96");
            Require(new PresetStore(_presetStore.FilePath).Load().Presets.Count==2,"both audio codec presets survive a fresh store instance");
            Require(await LoadFilesAsync(new[]{video}),"real video loads for MP4 FPS conversion and frame extraction");ShowMoreTools();
            Require(SystemsDescendants<Button>(_panelContent).Any(b=>b.Content as string=="Кадры видео в PNG…")&&!SystemsDescendants<Button>(_panelContent).Any(b=>b.Content as string=="Экспорт M4A / Opus / OGG…"),"video exposes frames and excludes audio-only encoding");
            ShowVideoExport();Tagged("video-fps").SelectedIndex=3;_panelContent.Children.OfType<CheckBox>().Last().IsChecked=true;await ShotAsync("03-video-fps.png");Click("Сохранить MP4");await FinishAsync();var converted=_jobs.Last();
            Require(converted.HistoryStatus==HistoryStatus.Succeeded&&converted.Options.VideoExportFramesPerSecond==30&&converted.Options.VideoExportRemoveAudio,"FPS and mute choices reach a real successful MP4 export");
            var fpsProbe=await ProbeMediaExportAsync(converted.Result!.OutputPath);var videoStream=fpsProbe.GetProperty("streams").EnumerateArray().Single();
            Require(videoStream.GetProperty("avg_frame_rate").GetString()=="30/1"&&videoStream.GetProperty("codec_name").GetString()=="h264","independent probe verifies exact 30fps H264 and the absent audio stream");
            SaveRecipe(converted,"MP4 30 FPS");Require(_customPresets.Single(p=>p.Name=="MP4 30 FPS").Options.VideoExportFramesPerSecond==30,"explicit FPS persists in the existing reusable video recipe");
            int beforeOversized=_jobs.Count;OpenPanel("Проверка ограничений","");RenderVideoFrames(90001);
            Require(!SystemsDescendants<Button>(_panelContent).Any(b=>b.Content as string=="Сохранить кадры PNG")&&_jobs.Count==beforeOversized&&!_historyBlocked,"over-24-hour frame duration is rejected before queueing and cannot block durable history");
            ShowVideoFrames();await EditorsAsync();var times=_panelContent.Children.OfType<TextBox>().ToArray();times[0].Text="1";times[1].Text="0.5";
            Require(!Named("Сохранить кадры PNG").IsEnabled,"frame export rejects a reversed interval before queueing");
            times[0].Text="0.5";times[1].Text="2.5";Tagged("frames-fps").SelectedItem=2;Tagged("frames-width").SelectedItem=160;await ShotAsync("04-video-frames.png");Click("Сохранить кадры PNG");await FinishAsync();var frames=_jobs.Last();
            Require(frames.HistoryStatus==HistoryStatus.Succeeded&&frames.Result?.Format=="FOLDER"&&frames.Options is{FramesStartSeconds:0.5,FramesDurationSeconds:2,FramesPerSecond:2,FramesWidth:160},"frame card captures the exact source interval and sampling options in a real task");
            var files=Directory.GetFiles(frames.Result!.OutputPath,"*.png");Require(files.Length==4&&files.Select(Path.GetFileName).Order().SequenceEqual(new[]{"frame_0001.png","frame_0002.png","frame_0003.png","frame_0004.png"}),"two-second fragment at two samples per second creates four sequential PNG frames");
            var decoded=await Task.WhenAll(files.Select(p=>new ImageMagickEngine().InspectAsync(p)));
            Require(decoded.All(m=>m.Format=="PNG"&&m.Width==160&&m.Height==100),"all published frames actually decode at the requested bounded display dimensions");
            Require(!PresetCatalog.CanSave(frames.Options)&&_repeatRecipe?.Options.Action==ImageAction.VideoExport&&!CanCompare(frames),"source-bound frame ranges do not replace repeat or become reusable presets/static comparison");
            ShowVideoFrames();ShowFileInfo();await EditorsAsync();Require(SystemsDescendants<TextBlock>(_panelContent).Any(t=>t.Text=="Сведения"),"replacing an inspecting frame card prevents a late editor from replacing the new panel");
            ShowVideoFrames();await EditorsAsync();var stale=Named("Сохранить кадры PNG");int count=_jobs.Count;Require(await LoadFilesAsync(new[]{audio}),"audio selection replaces a prepared frame card");stale.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(_jobs.Count==count,"stale frame controls cannot queue a newly selected incompatible source");
            await _queueGate.WaitAsync();gateHeld=true;ShowAudioExport();Click("Сохранить аудио");var canceled=_jobs.Last();canceled.Cancellation.Cancel();_queueGate.Release();gateHeld=false;await FinishAsync();
            Require(canceled.HistoryStatus==HistoryStatus.Cancelled&&canceled.Result==null,"a queued audio codec export cancels without publishing a partial file");
            var history=await new HistoryStore(_historyStore.FilePath).LoadAsync();
            Require(history.Error==null&&history.Entries.Count==_jobs.Count&&history.Entries.Count(e=>e.Status==HistoryStatus.Succeeded)==6,"history restores successful M4A, Opus, FPS MP4 and frame-folder results plus cancellation");
            Require(history.Entries.Any(e=>e.Options.AudioExportFormat=="OPUS"&&e.Options.AudioExportBitrateKbps==96)&&history.Entries.Any(e=>e.Options.VideoExportFramesPerSecond==30)&&history.Entries.Any(e=>e.Options.Action==ImageAction.VideoFrames&&e.Options.FramesStartSeconds==0.5),"durable history retains every new active media export parameter");
            Require(originals.All(p=>SystemsDigest(p.Key)==p.Value),"all codec, FPS, frame and cancellation operations preserve original source hashes");
            Require(!Directory.GetDirectories(_outputDirectory).Any(p=>Path.GetFileName(p).StartsWith(".orbit-",StringComparison.Ordinal)),"all successful and canceled operations remove private output staging");
            File.WriteAllText(Path.Combine(destination,"passed.json"),JsonSerializer.Serialize(new{checks=checks.Count,items=checks,run,userProfileIo=false,nativeClipboardWrites=false,registryWrites=false,nativeShellLaunches=0,physicalInput=false,listeningPerformed=false},new JsonSerializerOptions{WriteIndented=true}));
        }catch(Exception ex){App.Log(ex);File.WriteAllText(Path.Combine(destination,"error.txt"),ex.ToString());}
        finally{if(gateHeld)_queueGate.Release();Quit();}
    }

    private static async Task<JsonElement> ProbeMediaExportAsync(string path)
    {
        var start=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"tools","ffmpeg","ffprobe.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var argument in new[]{"-v","error","-show_streams","-show_format","-of","json",path})start.ArgumentList.Add(argument);
        using var process=Process.Start(start)??throw new InvalidOperationException("Cannot start own media probe");var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
        try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));await Task.WhenAll(output,error);if(process.ExitCode!=0)throw new InvalidOperationException(await error);using var json=JsonDocument.Parse(await output);return json.RootElement.Clone();}
        finally{if(!process.HasExited)process.Kill(true);}
    }
}
