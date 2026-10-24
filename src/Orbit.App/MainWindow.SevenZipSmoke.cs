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
using Orbit.Core.Documents;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private async Task RunSevenZipSmokeAsync()
    {
        var destination=Path.Combine(AppContext.BaseDirectory,"sevenzip-smoke");var run=Path.Combine(destination,"run-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        var checks=new List<string>();var opened=new List<string>();bool gateHeld=false;
        void Require(bool value,string text){if(!value)throw new InvalidOperationException("7z integration: "+text);checks.Add(text);}
        Button Named(string label)=>SystemsDescendants<Button>(_panelContent).Single(b=>b.Content as string==label);
        void Click(string label)=>Named(label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        T Tagged<T>(string tag)where T:FrameworkElement=>SystemsDescendants<T>(_panelContent).Single(c=>c.Tag as string==tag);
        async Task FinishAsync(){await Task.WhenAll(_jobTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(120));await FlushHistoryAsync();}
        async Task ReadersAsync()=>await Task.WhenAll(_resultToolTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(60));
        async Task ShotAsync(string name){_toast.Visibility=Visibility.Collapsed;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);Capture(Path.Combine(destination,name));}
        void SaveRecipe(JobEntry job,string name){ShowSavePreset(job);_panelContent.Children.OfType<TextBox>().Single().Text=name;Click("Сохранить пресет");}
        try{
            Directory.CreateDirectory(run);File.Delete(Path.Combine(destination,"passed.json"));File.Delete(Path.Combine(destination,"error.txt"));
            Require(IsDiagnosticRun&&_bridge is null&&_jobs.Count==0&&!ProductivityIoAllowed&&!PersonalizationIoAllowed&&Path.GetTempPath().StartsWith(AppContext.BaseDirectory,StringComparison.OrdinalIgnoreCase),"diagnostic uses owned temporary storage and avoids user stores, native hotkeys, clipboard and shell integration");
            var folder=Path.Combine(run,"пакет [Ω] %d");Directory.CreateDirectory(Path.Combine(folder,"пусто"));Directory.CreateDirectory(Path.Combine(folder,"猫"));
            var text=Path.Combine(folder,"данные [1].txt");var binary=Path.Combine(folder,"猫","файл.bin");var loose=Path.Combine(run,"@отдельный.txt");
            File.WriteAllText(text,"Orbit 7z — Unicode\n");File.WriteAllBytes(binary,Enumerable.Range(0,4096).Select(i=>(byte)(i%251)).ToArray());File.WriteAllText(loose,"@file names are literal\n");
            var originals=new[]{text,binary,loose}.ToDictionary(p=>p,SystemsDigest);
            _historyStore=new HistoryStore(Path.Combine(run,"history.json"));_presetStore=new PresetStore(Path.Combine(run,"presets.json"));_allowDiagnosticProductivityIo=true;
            _outputDirectory=Path.Combine(run,"output");Directory.CreateDirectory(_outputDirectory);_resultOpenOverride=(path,_)=>{opened.Add(path);return Task.CompletedTask;};SetCompactMode(true);
            Require(await LoadFilesAsync(new[]{folder,loose,text}),"folders, loose file and overlapping child load for aggregate 7z creation");ShowMoreTools();Click("Создать 7z…");
            Tagged<ComboBox>("sevenzip-compression").SelectedIndex=0;await ShotAsync("01-create-7z.png");Click("Создать 7z-архив");await FinishAsync();var first=_jobs.Last();
            Require(_jobs.Count==1&&first.HistoryStatus==HistoryStatus.Succeeded&&first.Result?.Format=="7Z"&&first.InputPaths.Count==3&&first.Options.SevenZipCompression=="Fastest","UI creates one real 7z aggregate job with chosen compression");
            var archive=first.Result!.OutputPath;var archiveHash=SystemsDigest(archive);var engine=new SevenZipArchiveEngine();var listing=await engine.InspectAsync(archive);
            Require(listing.Entries.Count==6&&listing.Entries.Count(e=>!e.IsDirectory)==3&&listing.TotalBytes==originals.Sum(p=>new FileInfo(p.Key).Length),"archive deduplicates overlapping selection and retains all normal files and directories");
            Require(listing.Entries.Any(e=>e.RelativeName.Contains("猫",StringComparison.Ordinal))&&listing.Entries.Any(e=>e.RelativeName.Contains("@отдельный",StringComparison.Ordinal)),"native archive preserves exact Unicode and literal at-sign filenames");
            await RunSevenZipFixtureAsync(["t","-y","-bd",archive]);Require(true,"independent native test verifies the created 7z payload and CRCs");
            await OpenKnownResultAsync(archive,false);Require(opened.Last()==archive,"7z output uses the validated intercepted Open route");
            SaveRecipe(first,"Быстрый 7z");var preset=_customPresets.Single();Require(preset.Options is{Action:ImageAction.Archive7z,SevenZipCompression:"Fastest",OutputDirectory:null},"7z preset retains active compression without the old output path");
            ApplyCurrentRecipe(preset);await FinishAsync();var collision=_jobs.Last();Require(collision.HistoryStatus==HistoryStatus.Succeeded&&collision.Result!.OutputPath!=archive&&SystemsDigest(archive)==archiveHash,"7z preset creates a unique archive and preserves an earlier result");
            Require(await LoadFilesAsync(new[]{archive})&&_selectionKind==ToolkitKind.SevenZip&&_metadata?.Entries==6,"real 7z is decoded and classified for the radial menu");
            Require(_radial.Children.OfType<Orbit.App.Controls.SectorButton>().Select(b=>b.Action).SequenceEqual(new[]{ImageAction.Extract7z,ImageAction.SevenZipList,ImageAction.FileHash,ImageAction.FileInfo}),"7z radial menu exposes extract, list, hash and information actions");await ShotAsync("02-7z-circle.png");
            ShowSevenZipBrowser();await ReadersAsync();var list=Tagged<ListBox>("sevenzip-entries");Require(list.Items.Count==6,"7z browser includes empty directories and nested contents");Tagged<TextBox>("sevenzip-search").Text="файл.bin";
            Require(list.Items.Count==1,"7z browser filters paths without changing the archive");await ShotAsync("03-7z-browser.png");Click("Сохранить список 7z TXT");await FinishAsync();var txt=_jobs.Last();
            Require(txt.HistoryStatus==HistoryStatus.Succeeded&&File.ReadAllText(txt.Result!.OutputPath).Contains("@отдельный",StringComparison.Ordinal)&&File.ReadAllText(txt.Result.OutputPath).Contains("猫",StringComparison.Ordinal),"filtered browser saves the complete Unicode contents to TXT");
            DispatchAction(ImageAction.Extract7z);await FinishAsync();var extracted=_jobs.Last();Require(extracted.HistoryStatus==HistoryStatus.Succeeded&&extracted.Result?.Format=="FOLDER","compact one-click extraction publishes a new folder");
            Require(originals.All(p=>SystemsDigest(Path.Combine(extracted.Result!.OutputPath,Path.GetRelativePath(run,p.Key)))==p.Value)&&Directory.Exists(Path.Combine(extracted.Result!.OutputPath,Path.GetFileName(folder),"пусто")),"all extracted file hashes and empty directories match original sources");
            ShowSevenZipBrowser();ShowMoreTools();await ReadersAsync();Require(Tagged<TextBox>("tool-search") is not null,"late 7z inspection cannot overwrite a different panel");
            ShowSevenZipBrowser();await ReadersAsync();var stale=Named("Распаковать весь 7z");int count=_jobs.Count;Require(await LoadFilesAsync(new[]{loose}),"plain source replaces an inspected 7z archive");stale.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Require(_jobs.Count==count,"stale browser controls cannot extract a newly selected plain file");
            ShowMoreTools();Tagged<TextBox>("tool-search").Text="7zip";Require(SystemsDescendants<Button>(_panelContent).Any(b=>b.Tag as string=="tool-command"&&b.Content as string=="Создать 7z…"),"7zip alias discovers archive creation for plain files");
            ApplyCurrentRecipe(preset);await FinishAsync();var second=_jobs.Last();Require(second.HistoryStatus==HistoryStatus.Succeeded&&second.InputPaths.Count==1&&second.Options.SevenZipCompression=="Fastest","7z recipe applies to a different new source");
            Require(await LoadFilesAsync(new[]{archive,second.Result!.OutputPath}),"two native 7z archives load for a batch");count=_jobs.Count;DispatchAction(ImageAction.SevenZipList);await FinishAsync();
            Require(_jobs.Count==count+2&&_jobs.Skip(count).All(j=>j.HistoryStatus==HistoryStatus.Succeeded&&j.Result?.Format=="TXT"),"batch 7z list creates one independent result per archive");
            count=_jobs.Count;DispatchAction(ImageAction.Extract7z);await FinishAsync();Require(_jobs.Count==count+2&&_jobs.Skip(count).All(j=>j.HistoryStatus==HistoryStatus.Succeeded&&j.Result?.Format=="FOLDER"),"batch 7z extraction creates distinct folders per archive");
            Require(await LoadFilesAsync(new[]{loose}),"owned file loads for queued cancellation");await _queueGate.WaitAsync();gateHeld=true;ShowSevenZipCreate();Click("Создать 7z-архив");var cancelled=_jobs.Last();cancelled.Cancellation.Cancel();_queueGate.Release();gateHeld=false;await FinishAsync();
            Require(cancelled.HistoryStatus==HistoryStatus.Cancelled&&cancelled.Result is null,"queued 7z creation cancels without publishing a partial archive");
            var bad=Path.Combine(run,"повреждён.7z");File.WriteAllText(bad,"not an archive");Require(!await LoadFilesAsync(new[]{bad})&&_files.Count==0,"corrupt 7z fails loading and clears the previous selection");
            var encrypted=Path.Combine(run,"зашифрован.7z");await RunSevenZipFixtureAsync(["a","-t7z","-y","-bd","-pfixture-only","-mhe=on",encrypted,loose]);
            Require(await LoadFilesAsync(new[]{encrypted})&&_metadata is{Kind:ToolkitKind.SevenZip,ArchiveLocked:true},"encrypted headers load as locked7z without prompting or reusing an old selection");
            var history=await new HistoryStore(_historyStore.FilePath).LoadAsync();Require(history.Error is null&&history.Entries.Count==_jobs.Count&&history.Entries.Any(e=>e.Options.Action==ImageAction.Archive7z&&e.Options.SevenZipCompression=="Fastest")&&history.Entries.Any(e=>e.Options.Action==ImageAction.Extract7z),"history restores all7z actions, active options, results and cancellation");
            var presets=new PresetStore(_presetStore.FilePath).Load();Require(presets.Warning is null&&presets.Presets.Any(p=>p.Options.Action==ImageAction.Archive7z&&p.Options.SevenZipCompression=="Fastest"),"7z recipe survives isolated store reload");
            Require(originals.All(p=>SystemsDigest(p.Key)==p.Value)&&SystemsDigest(archive)==archiveHash,"all original sources and previously published archives retain SHA256");
            Require(!Directory.GetDirectories(_outputDirectory).Any(p=>Path.GetFileName(p).StartsWith(".orbit-",StringComparison.Ordinal)),"all successful and cancelled 7z jobs clean private staging");
            File.WriteAllText(Path.Combine(destination,"passed.json"),JsonSerializer.Serialize(new{checks=checks.Count,items=checks,run,userProfileIo=false,nativeClipboardWrites=false,registryWrites=false,nativeShellLaunches=0,physicalInput=false},new JsonSerializerOptions{WriteIndented=true}));
        }catch(Exception ex){App.Log(ex);File.WriteAllText(Path.Combine(destination,"error.txt"),ex.ToString());}
        finally{if(gateHeld)_queueGate.Release();Quit();}
    }
    private static async Task RunSevenZipFixtureAsync(string[] arguments)
    {
        var start=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"tools","7zip","7zr.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,RedirectStandardInput=true};
        foreach(var argument in arguments)start.ArgumentList.Add(argument);
        using var process=Process.Start(start)??throw new InvalidOperationException("Cannot launch owned 7z fixture helper");process.StandardInput.Close();
        var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
        try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));await Task.WhenAll(output,error);if(process.ExitCode!=0)throw new InvalidOperationException(await error);}
        finally{if(!process.HasExited)process.Kill(true);}
    }
}
