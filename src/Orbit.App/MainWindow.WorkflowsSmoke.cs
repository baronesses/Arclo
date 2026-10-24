using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Orbit.App.Chains;
using Orbit.App.History;
using Orbit.App.Presets;
using Orbit.Core;
using Orbit.Core.Chains;
using Orbit.Core.Media;
using Orbit.Core.Presets;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private async Task RunWorkflowsSmokeAsync()
    {
        var destination=Path.Combine(AppContext.BaseDirectory,"workflows-smoke");
        var run=Path.Combine(destination,"run-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        var checks=new List<string>();bool gateHeld=false;
        void Require(bool value,string text){if(!value)throw new InvalidOperationException("Workflow integration: "+text);checks.Add(text);}
        Button Named(string text)=>SystemsDescendants<Button>(_panelContent).Single(b=>b.Content as string==text);
        void Click(string text)=>Named(text).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        async Task FinishAsync(){await Task.WhenAll(_jobTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(120));await FlushHistoryAsync();}
        async Task WorkAsync()=>await Task.WhenAll(_resultToolTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(60));
        async Task ShotAsync(string name){_toast.Visibility=Visibility.Collapsed;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);Capture(Path.Combine(destination,name));}
        async Task OpenPlayerAsync(){
            for(int i=0;i<100&&_mediaPreviewPane is{IsOpened:false,ErrorMessage:null};i++)await Task.Delay(100);
            Require(_mediaPreviewPane is{IsMuted:true,IsOpened:true,IsPlaying:false},"real WPF player opens the generated proxy muted and without autoplay");
            _mediaPreviewPane!.Play();await Task.Delay(650);
            Require(_mediaPreviewPane.PositionSeconds>0&&_mediaPreviewPane.IsPlaying,"muted real media playback advances its position");_mediaPreviewPane.Pause();
            Require(!_mediaPreviewPane.IsPlaying,"pause stops the playback state");
            _mediaPreviewPane.SeekSlider.Value=0.3;await Task.Delay(250);
            Require(Math.Abs(_mediaPreviewPane.PositionSeconds-0.3)<0.12&&!_mediaPreviewPane.IsPlaying,"real seek changes position without restarting paused playback");
            _mediaPreviewPane.Stop();Require(_mediaPreviewPane.PositionSeconds<0.05&&!_mediaPreviewPane.IsPlaying,"stop returns the real player to the beginning");
        }
        try{
            Directory.CreateDirectory(run);File.Delete(Path.Combine(destination,"passed.json"));File.Delete(Path.Combine(destination,"error.txt"));
            Require(IsDiagnosticRun&&_bridge is null&&_jobs.Count==0&&!ChainIoAllowed,"diagnostic skips user stores, shell integration and native hotkeys");
            var video=Path.Combine(run,"preview.mp4");var audio=Path.Combine(run,"voice.wav");var first=Path.Combine(run,"first.png");var second=Path.Combine(run,"second.png");
            await WriteEditingMediaAsync(video,true);await WriteEditingMediaAsync(audio,false);WriteResultsImage(first);WriteEditingTextImage(second);
            var originals=new[]{video,audio,first,second}.ToDictionary(p=>p,SystemsDigest);
            _historyStore=new HistoryStore(Path.Combine(run,"history.json"));_presetStore=new PresetStore(Path.Combine(run,"presets.json"));_chainStore=new ImageChainStore(Path.Combine(run,"chain-presets.json"));
            _allowDiagnosticProductivityIo=true;_allowDiagnosticChainIo=true;_outputDirectory=Path.Combine(run,"output");Directory.CreateDirectory(_outputDirectory);
            _previewTemporaryRoot=Path.Combine(run,"preview-temp");Directory.CreateDirectory(_previewTemporaryRoot);
            Require(await LoadFilesAsync(new[]{video}),"real video loads into the normal selection");SetCompactMode(true);ShowMediaTrim(ImageAction.VideoTrim);
            var times=_panelContent.Children.OfType<TextBox>().ToArray();times[0].Text="0.5";times[1].Text="2.5";Click("Подготовить предпросмотр");await WorkAsync();
            Require(_mediaPreviewResult is{IsVideo:true,SourceStartSeconds:0.5}&&File.Exists(_mediaPreviewResult.Path),"trim preview creates a private real video proxy for the chosen interval");
            await OpenPlayerAsync();await ShotAsync("01-video-preview.png");var previewPath=_mediaPreviewResult!.Path;
            times[1].Text="2";await WorkAsync();Require(_mediaPreviewPane==null&&!File.Exists(previewPath),"editing the interval closes playback and deletes the old private proxy");
            Click("Подготовить предпросмотр");times[1].Text="1.5";await WorkAsync();Require(Named("Подготовить предпросмотр").IsEnabled&&_mediaPreviewPane==null,"editing during preview preparation cancels it and keeps the prepare button usable");
            Require(await LoadFilesAsync(new[]{audio}),"real audio loads for preview and normalization");ShowMediaTrim(ImageAction.AudioTrim);Click("Подготовить предпросмотр");await WorkAsync();
            Require(_mediaPreviewResult is{IsVideo:false},"audio trim produces an audio preview instead of a video proxy");await OpenPlayerAsync();var audioProxy=_mediaPreviewResult!.Path;
            ClosePanel();await WorkAsync();Require(_mediaPreviewPane==null&&!File.Exists(audioProxy),"closing the card releases the player and audio preview file");
            ShowMediaTrim(ImageAction.AudioTrim);Click("Подготовить предпросмотр");ShowFileInfo();await WorkAsync();
            Require(_mediaPreviewPane==null&&SystemsDescendants<TextBlock>(_panelContent).Any(t=>t.Text=="Сведения"),"replacing a loading preview card cancels work without attaching a late player");
            ShowAudioNormalize();Click("Измерить первый исходник");await WorkAsync();
            Require(SystemsDescendants<TextBlock>(_panelContent).Any(t=>t.Text.StartsWith("Исходник:")&&t.Text.Contains("LUFS")),"normalization card measures the real original before export");
            await ShotAsync("02-normalization.png");Click("Нормализовать в WAV");await FinishAsync();var normalized=_jobs.Last();
            Require(normalized.HistoryStatus==HistoryStatus.Succeeded&&normalized.Options.NormalizeTargetLufs==-16&&normalized.Result!.Format=="WAV","normalization UI creates a real successful WAV with requested loudness target");
            var loudness=await new AudioNormalizeEngine().AnalyzeAsync(normalized.Result!.OutputPath);
            Require(Math.Abs(loudness.IntegratedLufs+16)<=0.6&&loudness.TruePeakDb<=-0.8,"exported audio meets measured loudness and true-peak limits");
            ShowSavePreset(normalized);_panelContent.Children.OfType<TextBox>().Single().Text="Моя нормализация";Click("Сохранить пресет");
            Require(_customPresets.Single().Options.Action==ImageAction.AudioNormalize&&_customPresets.Single().Options.OutputDirectory==null,"normalization settings save as a reusable source-independent preset");
            Require(await LoadFilesAsync(new[]{first,second}),"two real images load for a batch chain");
            var custom=_presetStore.Add("Ширина 120",new ImageJobOptions{Action=ImageAction.Resize,Width=120,NoUpscale=true});Require(custom.Success,"custom resize recipe is available to the chain builder");_customPresets=custom.Presets;
            ShowImageChainEditor();_panelContent.Children.OfType<TextBox>().Single().Text="WebP для сайта";
            SystemsDescendants<Button>(_panelContent).First(b=>b.Content as string=="Убрать").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            _panelContent.Children.OfType<ComboBox>().Single().SelectedItem=_customPresets.Single(p=>p.Name=="Ширина 120");Click("Добавить шаг");
            SystemsDescendants<Button>(_panelContent).Where(b=>b.Content as string=="↑").Last().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await ShotAsync("03-chain-editor.png");Click("Сохранить цепочку");
            var chain=_savedChains.Single();Require(chain.Steps.Select(s=>s.Action).SequenceEqual(new[]{ImageAction.Resize,ImageAction.WebP})&&chain.Steps[0].Width==120,"builder reorders preset steps and persists the exact chain");
            Require(new ImageChainStore(_chainStore.FilePath).Load().Chains.Single().Id==chain.Id,"saved chain survives a fresh store instance");
            Click("Применить цепочку");await FinishAsync();var imageJobs=_jobs.Where(j=>j.Options.Action==ImageAction.ImageChain).ToArray();
            Require(imageJobs.Length==2&&imageJobs.All(j=>j.HistoryStatus==HistoryStatus.Succeeded&&j.Result is{Format:"WEBP",Width:120}),"one chain task per image creates actual resized WebP results");
            Require(Directory.GetFiles(_outputDirectory).Length==3&&!Directory.GetDirectories(_outputDirectory).Any(),"only final audio and chain outputs are published; intermediate files are absent");
            Require(CanCompare(imageJobs[0])&&_repeatRecipe?.Options.Action==ImageAction.ImageChain,"chain result supports image comparison and the last reusable recipe");
            ShowSavePreset(imageJobs[0]);_panelContent.Children.OfType<TextBox>().Single().Text="Готовая цепочка";Click("Сохранить пресет");
            var saved=_customPresets.Single(p=>p.Name=="Готовая цепочка");Require(saved.Options.ChainSteps.Count==2&&saved.Options.OutputDirectory==null&&PresetCatalog.CanApply(saved,_selectionMetadata),"whole chain can also be a standard preset for favorites and repeat");
            var loaded=await new HistoryStore(_historyStore.FilePath).LoadAsync();Require(loaded.Error==null&&loaded.Entries.Count==3&&loaded.Entries.Count(e=>e.Options.ChainSteps.Count==2)==2,"history restores normalization and complete ordered chain options");
            await _queueGate.WaitAsync();gateHeld=true;RunImageChain(chain);var queued=_jobs.Where(j=>!j.Finished).ToArray();foreach(var job in queued)job.Cancellation.Cancel();_queueGate.Release();gateHeld=false;await FinishAsync();
            Require(queued.Length==2&&queued.All(j=>j.HistoryStatus==HistoryStatus.Cancelled)&&Directory.GetFiles(_outputDirectory).Length==3,"queued batch chains cancel independently without publishing extra files");
            Require(!Directory.EnumerateFileSystemEntries(_previewTemporaryRoot).Any(),"all private preview proxies are removed after close and cancellation");
            Require(originals.All(p=>SystemsDigest(p.Key)==p.Value),"preview, normalization and chains preserve all original source hashes");
            File.WriteAllText(Path.Combine(destination,"passed.json"),JsonSerializer.Serialize(new{checks=checks.Count,items=checks,run,actualWpfMediaPlayback=true,playbackMuted=true,listeningPerformed=false,userProfileIo=false,nativeClipboardWrites=false,registryWrites=false,physicalInput=false},new JsonSerializerOptions{WriteIndented=true}));
        }catch(Exception ex){App.Log(ex);File.WriteAllText(Path.Combine(destination,"error.txt"),ex.ToString());}
        finally{if(gateHeld)_queueGate.Release();Quit();}
    }
}
