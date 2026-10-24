using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Orbit.App.History;
using Orbit.App.Platform;
using Orbit.App.Settings;
using Orbit.Core;
using Orbit.Core.Localization;
using Orbit.Core.Presets;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private async Task RunLocalizationSmokeAsync()
    {
        string directory=Path.Combine(AppContext.BaseDirectory,"localization-smoke");
        string run=Path.Combine(directory,"run-"+Guid.NewGuid().ToString("N"));
        var checks=new List<string>();
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool previousManagedPreference=ImageMagickEngine.PreferManagedCodecs;
        var originalPresets=_customPresets;
        void Require(bool value,string label){if(!value)throw new InvalidOperationException(label);checks.Add(label);}
        async Task Render(){await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);await Task.Delay(60);}
        Button Header(string tag)=>SystemsDescendants<Button>(_header).Single(button=>Equals(button.Tag,tag));
        ComboBox Selector()=>SystemsDescendants<ComboBox>(_panelContent).Single(combo=>Equals(combo.Tag,"LanguageSelector"));
        async Task SelectLanguage(string language)
        {
            ShowSettings();await Render();Selector().SelectedValue=language;await Render();
            Require(L.Language==language&&Equals(Selector().SelectedValue,language),"Language selector applies "+language+" without restart");
        }
        async Task Shot(string name){_toast.Visibility=Visibility.Collapsed;await Render();Capture(Path.Combine(directory,name));}
        static bool Cyrillic(string value)=>Regex.IsMatch(value,"[А-Яа-яЁё]");
        static bool ResourceKey(string value)=>Regex.IsMatch(value,@"\bText_[0-9A-F]{12}\b");
        IEnumerable<string> CardStrings()=>SystemsDescendants<TextBlock>(_panelContent).Select(text=>text.Text)
            .Concat(SystemsDescendants<Button>(_panelContent).Select(button=>button.Content as string??""))
            .Where(text=>!string.IsNullOrWhiteSpace(text)).Distinct(StringComparer.Ordinal);
        try
        {
            Directory.CreateDirectory(run);File.Delete(Path.Combine(directory,"error.txt"));File.Delete(Path.Combine(directory,"passed.json"));
            Require(IsDiagnosticRun&&_bridge is null&&!ProductivityIoAllowed&&!PersonalizationIoAllowed,"Diagnostic has no user stores, global keys, tray or native clipboard access");
            Require(ImageMagickEngine.ManagedFallback is Codecs.WindowsImageCodec,"Windows image codecs are available for the owned conversion");
            ImageMagickEngine.PreferManagedCodecs=true;
            BeginAnimation(OpacityProperty,null);Opacity=1;SetCompactMode(false);
            var capture=new ResultsClipboardCapture(Dispatcher);_resultClipboard=new ResultClipboard(Dispatcher,capture);
            L.SetLanguage("ru");ShowSettings();await Render();
            Require(L.EffectiveLanguage=="ru"&&Cyrillic(Header("Header.Files").Content as string??""),"Russian header is rendered initially");
            Require(Selector().Items.Count==3,"Settings offer automatic, Russian and English languages");
            Require(AutomationProperties.GetName(Header("WindowMinimize"))==L.Get("Window.Minimize"),"Minimize accessibility starts in the selected language");
            string russianPreset=PresetCatalog.BuiltIns.Single(p=>p.Id=="builtin-resize-1600").Name;
            string russianPurpose=AppComponents.Catalog.Single(component=>component.Id=="ffmpeg").Purpose;
            await Shot("locale-settings-ru.png");
            await SelectLanguage("en");
            Require(!Cyrillic(Header("Header.Files").Content as string??"")&&Equals(Header("Header.Files").Content,L.Get("Header.Files")),"Existing header switches to English");
            Require(Equals(Header("WindowMinimize").ToolTip,"Minimize")&&AutomationProperties.GetName(Header("WindowMinimize"))=="Minimize","Existing minimize tooltip and accessibility switch to English");
            string englishPreset=PresetCatalog.BuiltIns.Single(p=>p.Id=="builtin-resize-1600").Name;
            string englishPurpose=AppComponents.Catalog.Single(component=>component.Id=="ffmpeg").Purpose;
            Require(russianPreset!=englishPreset&&!Cyrillic(englishPreset),"Built-in preset names are resolved in the current language");
            Require(russianPurpose!=englishPurpose&&!Cyrillic(englishPurpose),"Component descriptions are resolved in the current language");
            Require(PresetCatalog.BuiltIns.All(p=>!Cyrillic(p.Name)&&!ResourceKey(p.Name)),"English built-in preset names contain no Russian or missing resource keys");
            Require(AppComponents.Catalog.All(c=>!Cyrillic(c.Purpose)&&!ResourceKey(c.Purpose)),"English component descriptions contain no Russian or missing resource keys");
            await Shot("locale-settings-en.png");
            var store=new SettingsStore(Path.Combine(run,"settings.json"));
            foreach(string language in new[]{"en","ru","auto"})
            {
                Require(store.Save(new OrbitSettings{Language=language,OutputDirectory=run}) is null&&store.Load().Language==language,"Owned settings preserve the "+language+" language preference");
            }
            Require((new OrbitSettings{Language="invalid"}).Normalize().Language=="auto","Unknown saved language normalizes to automatic");
            string source=Path.Combine(run,"Пользовательское имя [φ].png");
            var pixels=new byte[48*32*4];for(int i=0;i<pixels.Length;i+=4){pixels[i]=20;pixels[i+1]=90;pixels[i+2]=180;pixels[i+3]=255;}
            var bitmap=BitmapSource.Create(48,32,96,96,PixelFormats.Bgra32,null,pixels,48*4);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var output=File.Create(source))encoder.Save(output);
            string sourceHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
            const string customName="Личный пресет [φ]";
            _customPresets=new[]{new ProcessingPreset("owned-localization",customName,"User description",new ImageJobOptions{Action=ImageAction.Png},false)};
            Require(await LoadFilesAsync(new[]{source}),"Owned image with a Russian user file name loads");
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _queueExecutionBarrier=token=>{entered.TrySetResult();return release.Task.WaitAsync(token);};
            _outputDirectory=run;Enqueue(source,new ImageJobOptions{Action=ImageAction.Png,OutputDirectory=run});
            var job=_jobs.Last();await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Require(!job.Finished&&job.HistoryStatus==HistoryStatus.Running,"Real conversion is running behind the owned execution barrier");
            foreach(string language in new[]{"ru","en"})
            {
                await SelectLanguage(language);
                Require(_jobs.Any(item=>ReferenceEquals(item,job))&&!job.Finished&&!job.Cancellation.IsCancellationRequested,"Language switch preserves the active conversion: "+language);
                Require(_files.Count==1&&_files[0]==source&&_customPresets.Single().Name==customName,"Language switch preserves exact user file and preset names: "+language);
            }
            int jobsBefore=_jobs.Count;
            Header("WindowMinimize").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Render();
            Require(WindowState==WindowState.Minimized&&IsVisible&&ShowInTaskbar,"Minimize uses the taskbar and retains the actual native window");
            Require(_jobs.Count==jobsBefore&&!job.Finished&&!job.Cancellation.IsCancellationRequested,"Minimize preserves the running conversion and its cancellation token");
            WindowState=WindowState.Normal;await Render();
            Require(WindowState==WindowState.Normal&&IsVisible&&_jobs.Count==jobsBefore&&!job.Cancellation.IsCancellationRequested,"Restoring the same window preserves the conversion");
            release.TrySetResult();await Task.WhenAll(_jobTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(45));_queueExecutionBarrier=null;
            Require(job.HistoryStatus==HistoryStatus.Succeeded&&job.Result is not null&&File.Exists(job.Result.OutputPath),"The real conversion completes successfully after restore");
            Require(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)))==sourceHash&&_customPresets.Single().Name==customName,"Original bytes and custom preset text remain unchanged");
            ClosePanel();ClearSelection();await Shot("locale-main-en.png");
            string audio=Path.Combine(run,"sample.wav");
            using(var output=new BinaryWriter(File.Create(audio)))
            {
                const int sampleRate=48000,samples=48000;
                output.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));output.Write(36+samples*2);output.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                output.Write(16);output.Write((short)1);output.Write((short)1);output.Write(sampleRate);output.Write(sampleRate*2);output.Write((short)2);output.Write((short)16);
                output.Write(System.Text.Encoding.ASCII.GetBytes("data"));output.Write(samples*2);output.Write(new byte[samples*2]);
            }
            _files=new List<string>{audio};_selectionKind=ToolkitKind.Audio;
            var audioMetadata=new ToolkitMetadata(audio,"WAV",0,0,new FileInfo(audio).Length,ToolkitKind.Audio,DurationSeconds:1,HasAudio:true);
            _metadata=audioMetadata;_selectionMetadata=new[]{audioMetadata};_selectionLoading=false;ShowAudioExport();await Render();
            var audioStrings=CardStrings().ToArray();
            Require(audioStrings.Length>=7&&!audioStrings.Any(Cyrillic),"The actual English audio export panel has no Russian captions or descriptions");
            Require(!audioStrings.Any(ResourceKey)&&SystemsDescendants<ComboBox>(_panelContent).Any(combo=>Equals(combo.Tag,"audio-export-format")),"Audio export resolves resource values and retains invariant format IDs");
            ClosePanel();ClearSelection();ShowComponents();await Render();
            Require(!CardStrings().Any(Cyrillic)&&!CardStrings().Any(ResourceKey),"The actual English components panel has translated controls and descriptions");
            await Shot("locale-components-en.png");
            ShowMoreTools();await Render();_toolSearchBox!.Text="youtube";await Render();
            Require(SystemsDescendants<Button>(_panelContent).Count(button=>Equals(button.Tag,"tool-command"))==1,"English tool search retains the YouTube download alias");
            _toolSearchBox.Text="очередь";await Render();
            Require(SystemsDescendants<Button>(_panelContent).Count(button=>Equals(button.Tag,"tool-command"))==1,"English interface retains Russian queue search aliases");
            await SelectLanguage("ru");
            Require(Cyrillic(Header("Header.Files").Content as string??"")&&Equals(Header("WindowMinimize").ToolTip,"Свернуть"),"Switching back updates the existing header and minimize control to Russian");
            await SelectLanguage("en");
            Require(!Cyrillic(Header("Header.Files").Content as string??""),"The same window switches to English again");
            File.WriteAllText(Path.Combine(directory,"passed.json"),JsonSerializer.Serialize(new{checks,run,Language=L.Language,NativeInput=false,UserPreferencesWritten=false,UserDataChanged=false,RealPngConversion=true,MinimizedAndRestored=true},new JsonSerializerOptions{WriteIndented=true}));
        }
        catch(Exception error)
        {
            Directory.CreateDirectory(directory);File.WriteAllText(Path.Combine(directory,"error.txt"),error.ToString());Environment.ExitCode=1;
        }
        finally
        {
            release.TrySetResult();_queueExecutionBarrier=null;_customPresets=originalPresets;ImageMagickEngine.PreferManagedCodecs=previousManagedPreference;
            try{await Task.WhenAll(_jobTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(15));}catch{ }
            Quit();
        }
    }
}
