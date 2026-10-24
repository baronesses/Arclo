using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
    private async Task RunMediaToolsSmokeAsync()
    {
        var destination=Path.Combine(AppContext.BaseDirectory,"media-tools-smoke");
        var run=Path.Combine(destination,"run-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        var fixtures=Path.Combine(run,"fixtures");var output=Path.Combine(run,"output");
        var checks=new List<string>();var opened=new List<string>();bool gateHeld=false;
        void Require(bool condition,string text){if(!condition)throw new InvalidOperationException("Media tools integration: "+text);checks.Add(text);}
        Button Named(string title)=>SystemsDescendants<Button>(_panelContent).Single(b=>b.Content as string==title);
        async Task CompleteAsync(){await Task.WhenAll(_jobTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(90));await FlushHistoryAsync();}
        async Task EditorsAsync()=>await Task.WhenAll(_resultToolTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(90));
        async Task ShotAsync(string name){_toast.Visibility=Visibility.Collapsed;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);Capture(Path.Combine(destination,name));}
        void SaveRecipe(JobEntry job,string name){ShowSavePreset(job);_panelContent.Children.OfType<TextBox>().Single().Text=name;Named("Сохранить пресет").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
        try{
            Directory.CreateDirectory(fixtures);Directory.CreateDirectory(output);
            File.Delete(Path.Combine(destination,"passed.json"));File.Delete(Path.Combine(destination,"error.txt"));
            Require(IsDiagnosticRun&&_bridge is null&&_jobs.Count==0,"diagnostic avoids user profile, tray and native hotkey registration");
            var video=Path.Combine(fixtures,"видео с музыкой.mp4");var audio=Path.Combine(fixtures,"голос.wav");
            await WriteEditingMediaAsync(video,true);await WriteEditingMediaAsync(audio,false);
            var hashes=new[]{video,audio}.ToDictionary(p=>p,p=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
            _historyStore=new HistoryStore(Path.Combine(run,"history.json"));_presetStore=new PresetStore(Path.Combine(run,"presets.json"));
            _allowDiagnosticProductivityIo=true;_outputDirectory=output;_resultOpenOverride=(p,_)=>{opened.Add(p);return Task.CompletedTask;};
            Require(await LoadFilesAsync(new[]{video}),"real four-second video is loaded through the normal decoder");
            SetCompactMode(true);ShowMoreTools();
            Require(new[]{"Видео в GIF…","Размер и звук видео…"}.All(t=>SystemsDescendants<Button>(_panelContent).Any(b=>b.Content as string==t)),"video More menu exposes GIF and export controls");
            Named("Видео в GIF…").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await EditorsAsync();
            var times=_panelContent.Children.OfType<TextBox>().ToArray();times[0].Text="1";times[1].Text="50";
            Require(!Named("Создать GIF").IsEnabled&&_jobs.Count==0,"GIF range outside the source is rejected before queueing");
            times[1].Text="2";var choices=_panelContent.Children.OfType<ComboBox>().ToArray();choices[0].SelectedItem=160;choices[1].SelectedItem=12;
            await ShotAsync("01-gif-parameters.png");Named("Создать GIF").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await CompleteAsync();
            var gif=_jobs.Last();
            Require(gif.HistoryStatus==HistoryStatus.Succeeded&&gif.Result is{Format:"GIF",Width:>0,Height:>0},"GIF UI runs a real conversion with validated output geometry");
            Require(gif.Options is{GifStartSeconds:1,GifDurationSeconds:1,GifWidth:160,GifFramesPerSecond:12},"GIF queue captures the selected interval, width and frame rate");
            Require(File.ReadAllBytes(gif.Result!.OutputPath).AsSpan(0,3).SequenceEqual("GIF"u8)&&!CanCompare(gif),"published GIF has a real GIF header and no misleading static image comparison");
            await OpenKnownResultAsync(gif.Result.OutputPath,false);
            Require(opened.SequenceEqual(new[]{gif.Result.OutputPath})&&!PresetCatalog.CanSave(gif.Options),"GIF result can be opened through the intercepted viewer and its source-bound interval is not a preset");
            Require(_compact&&_panel.Visibility==Visibility.Collapsed&&ActualWidth<=_compactSize+1,"GIF returns to the compact menu without expanding the manager");
            ShowVideoExport();SystemsDescendants<ComboBox>(_panelContent).Single(c=>c.Tag as string=="video-width").SelectedIndex=1;
            var toggles=_panelContent.Children.OfType<CheckBox>().ToArray();toggles[0].IsChecked=true;toggles[1].IsChecked=true;
            _panelContent.Children.OfType<Slider>().Single().Value=25;
            await ShotAsync("02-video-export-parameters.png");Named("Сохранить MP4").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await CompleteAsync();
            var export=_jobs.Last();
            Require(export.HistoryStatus==HistoryStatus.Succeeded&&export.Result is{Format:"MP4",Width:160,Height:100},"no-upscale export preserves a smaller video's actual dimensions");
            Require(export.Options is{VideoExportWidth:640,VideoExportNoUpscale:true,VideoExportRemoveAudio:true,VideoCrf:25},"export task retains the requested resolution, mute and quality settings");
            using(var probe=await ReadMediaToolsProbeAsync(export.Result!.OutputPath)){
                var streams=probe.RootElement.GetProperty("streams").EnumerateArray().ToArray();
                Require(streams.Count(s=>s.GetProperty("codec_type").GetString()=="video")==1&&!streams.Any(s=>s.GetProperty("codec_type").GetString()=="audio"),"independent ffprobe confirms the exported MP4 has no audio stream");
            }
            SaveRecipe(export,"Тихое видео 640");
            Require(_customPresets.Single().Options is{Action:ImageAction.VideoExport,VideoExportRemoveAudio:true},"actual preset UI saves the export operation");
            Require(await LoadFilesAsync(new[]{audio}),"real audio loads independently from the video selection");ShowMoreTools();
            Require(SystemsDescendants<Button>(_panelContent).Any(b=>b.Content as string=="Громкость и плавные переходы…")
                &&!SystemsDescendants<Button>(_panelContent).Any(b=>b.Content as string=="Видео в GIF…"),"audio More menu exposes audio controls and excludes GIF");
            Named("Громкость и плавные переходы…").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await EditorsAsync();
            times=_panelContent.Children.OfType<TextBox>().ToArray();times[0].Text="5";
            Require(!Named("Сохранить WAV").IsEnabled,"fade longer than the shortest audio is rejected in the UI");
            times[0].Text="0.5";times[1].Text="1";_panelContent.Children.OfType<Slider>().Single().Value=50;
            await ShotAsync("03-audio-adjust-parameters.png");Named("Сохранить WAV").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await CompleteAsync();
            var adjusted=_jobs.Last();
            Require(adjusted.HistoryStatus==HistoryStatus.Succeeded&&adjusted.Result is{Format:"WAV",Width:0,Height:0},"audio controls create a real WAV task result");
            Require(adjusted.Options is{AudioVolumePercent:50,AudioFadeInSeconds:.5,AudioFadeOutSeconds:1},"audio queue captures volume and both fade durations");
            using(var probe=await ReadMediaToolsProbeAsync(adjusted.Result!.OutputPath)){
                var track=probe.RootElement.GetProperty("streams").EnumerateArray().Single(s=>s.GetProperty("codec_type").GetString()=="audio");
                Require(track.GetProperty("codec_name").GetString()=="pcm_s24le"&&track.GetProperty("sample_rate").GetString()=="44100"&&track.GetProperty("channels").GetInt32()==1,"independent ffprobe verifies PCM24 and the source sample rate/channel count");
            }
            SaveRecipe(adjusted,"Плавный тихий звук");
            Require(new PresetStore(_presetStore.FilePath).Load() is{Warning:null,Presets.Count:2},"both new reusable recipes survive a fresh catalog load");
            await _queueGate.WaitAsync();gateHeld=true;int existing=Directory.GetFiles(output).Length;
            QueueSelected(new ImageJobOptions{Action=ImageAction.AudioAdjust,OutputDirectory=output});var cancelled=_jobs.Last();cancelled.Cancellation.Cancel();
            await CompleteAsync();_queueGate.Release();gateHeld=false;
            Require(cancelled.HistoryStatus==HistoryStatus.Cancelled&&cancelled.Result is null&&Directory.GetFiles(output).Length==existing,"cancelling a queued media task leaves no output file");
            var restored=await new HistoryStore(_historyStore.FilePath).LoadAsync();
            Require(restored.Error is null&&restored.Entries.Count==_jobs.Count&&restored.Entries.Single(e=>e.Id==gif.Id).Result?.Format=="GIF","new media actions and GIF format survive durable history reload");
            Require(restored.Entries.Single(e=>e.Id==adjusted.Id).Options==adjusted.Options&&restored.Entries.Single(e=>e.Id==export.Id).Options==export.Options,"history restores every active audio/video option exactly");
            var mismatched=Path.Combine(fixtures,"короткое видео и длинный звук.mkv");
            await MediaToolsFixtureAsync("-hide_banner","-loglevel","error","-y","-i",video,"-filter_complex","[0:v:0]trim=duration=1.2,setpts=PTS-STARTPTS[v]","-map","[v]","-map","0:a:0","-c:v","libx264","-threads:v","1","-c:a","pcm_s16le",mismatched);
            hashes.Add(mismatched,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(mismatched))));
            Require(await LoadFilesAsync(new[]{mismatched})&&_selectionMetadata.Single().DurationSeconds>2,"duration fixture contains a short video and longer audio/container");
            ShowVideoGif();await EditorsAsync();
            Require(Math.Abs(_selectionMetadata.Single().DurationSeconds-1.2)<.02,"GIF editor measures the real video track instead of its longer audio");
            times=_panelContent.Children.OfType<TextBox>().ToArray();times[0].Text="0";times[1].Text="2";
            Require(!Named("Создать GIF").IsEnabled,"GIF editor rejects a range beyond actual video EOF before a task can start");
            ShowVideoGif();ShowJobs();await EditorsAsync();
            Require(_jobsVisible&&SystemsDescendants<TextBlock>(_panelContent).Any(t=>t.Text=="Задачи"),"closing/replacing a duration editor cancels it without late controls replacing the new panel");
            var shortAudio=Path.Combine(fixtures,"короткий MP3.mp3");
            await MediaToolsFixtureAsync("-hide_banner","-loglevel","error","-y","-i",audio,"-t","0.1","-ar","8000","-c:a","libmp3lame",shortAudio);
            hashes.Add(shortAudio,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(shortAudio))));
            Require(await LoadFilesAsync(new[]{shortAudio})&&_selectionMetadata.Single().DurationSeconds>.2,"MP3 fixture metadata includes encoder padding beyond the actual audio samples");
            ShowAudioAdjust();await EditorsAsync();
            Require(Math.Abs(_selectionMetadata.Single().DurationSeconds-.1)<.002,"audio editor measures exact decoded duration without MP3 encoder padding");
            times=_panelContent.Children.OfType<TextBox>().ToArray();times[0].Text="0";times[1].Text="0.2";
            Require(!Named("Сохранить WAV").IsEnabled,"oversized gapless MP3 fades are rejected before queueing");
            await LoadFilesAsync(new[]{shortAudio});
            var recipe=_presetStore.Add("Проверка короткого MP3",new ImageJobOptions{Action=ImageAction.AudioAdjust,AudioFadeOutSeconds=.2});
            _customPresets=recipe.Presets;ShowPresets();
            _panelContent.Children.OfType<ComboBox>().Single().SelectedItem=_customPresets.Single(p=>p.Name=="Проверка короткого MP3");
            int countBeforePreset=_jobs.Count;Require(Named("Применить пресет").IsEnabled,"cheap preset metadata check is a preliminary compatibility check");
            Named("Применить пресет").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await EditorsAsync();
            Require(_jobs.Count==countBeforePreset&&!Named("Сохранить WAV").IsEnabled,"applying an audio recipe verifies decoded duration and opens correction controls instead of queueing an invalid task");
            Require(hashes.All(pair=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pair.Key)))==pair.Value),"all actual UI jobs and metadata preflights preserve original file hashes");
            Require(!Directory.EnumerateDirectories(output).Any(d=>Path.GetFileName(d).StartsWith(".orbit-",StringComparison.Ordinal)),"new UI jobs and cancellation leave no private temporary output directories");
            Require(!_historyBlocked&&_jobs.All(j=>j.HistoryStatus is HistoryStatus.Succeeded or HistoryStatus.Cancelled),"the complete media sequence has no blocked history writes or unexpected job failures");
            var report=JsonSerializer.Serialize(new{Passed=checks.Count,Checks=checks,Artifacts=run,NativeClipboardReads=0,NativeClipboardWrites=0,NativeShellLaunches=0,UserProfileAccessed=false,ListeningPerformed=false},new JsonSerializerOptions{WriteIndented=true});
            await File.WriteAllTextAsync(Path.Combine(destination,"passed.json"),report);await File.WriteAllTextAsync(Path.Combine(run,"passed.json"),report);
        }catch(Exception ex){Directory.CreateDirectory(destination);await File.WriteAllTextAsync(Path.Combine(destination,"error.txt"),ex.ToString());}
        finally{if(gateHeld)_queueGate.Release();Quit();}
    }

    private static async Task<JsonDocument> ReadMediaToolsProbeAsync(string path)
    {
        var start=new ProcessStartInfo(new MediaEngine().ProbeExecutablePath){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var value in new[]{"-v","error","-show_streams","-show_format","-of","json",path})start.ArgumentList.Add(value);
        using var process=Process.Start(start)??throw new IOException("Could not start the diagnostic probe");
        var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));await stderr;if(process.ExitCode!=0)throw new IOException("Native probe failed");return JsonDocument.Parse(await stdout);}
        finally{if(!process.HasExited)process.Kill(true);}
    }

    private static async Task MediaToolsFixtureAsync(params string[] arguments)
    {
        var start=new ProcessStartInfo(new MediaEngine().ExecutablePath){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var argument in arguments)start.ArgumentList.Add(argument);
        using var process=Process.Start(start)??throw new IOException("Could not start the diagnostic encoder");
        var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));await stdout;if(process.ExitCode!=0)throw new IOException(await stderr);}
        finally{if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}}
    }
}
