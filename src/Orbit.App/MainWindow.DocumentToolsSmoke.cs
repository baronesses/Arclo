using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
using Orbit.Core.Presets;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private async Task RunDocumentToolsSmokeAsync()
    {
        var destination=Path.Combine(AppContext.BaseDirectory,"document-tools-smoke");
        var run=Path.Combine(destination,"run-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        var checks=new List<string>();bool gateHeld=false;
        void Require(bool value,string text){if(!value)throw new InvalidOperationException("Document tools integration: "+text);checks.Add(text);}
        Button Named(string text)=>SystemsDescendants<Button>(_panelContent).Single(b=>b.Content as string==text);
        void Click(string text)=>Named(text).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        async Task FinishAsync(){await Task.WhenAll(_jobTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(90));await FlushHistoryAsync();}
        async Task InspectAsync()=>await Task.WhenAll(_resultToolTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(45));
        async Task ShotAsync(string name){_toast.Visibility=Visibility.Collapsed;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);Capture(Path.Combine(destination,name));}
        try{
            Directory.CreateDirectory(run);File.Delete(Path.Combine(destination,"passed.json"));File.Delete(Path.Combine(destination,"error.txt"));
            Require(IsDiagnosticRun&&_bridge is null&&_jobs.Count==0&&!ProductivityIoAllowed,"diagnostic skips user profile, shell integration and native hotkeys");
            var pdf=Path.Combine(run,"пять страниц.pdf");WriteSystemsPdf(pdf,new[]{("A1",320,240),("A2",440,330),("A3",300,500),("A4",500,200),("A5",600,600)});
            var zip=Path.Combine(run,"исходный архив.zip");WriteDocumentZip(zip);
            var originals=new[]{pdf,zip}.ToDictionary(p=>p,SystemsDigest);
            _historyStore=new HistoryStore(Path.Combine(run,"history.json"));_presetStore=new PresetStore(Path.Combine(run,"presets.json"));
            _allowDiagnosticProductivityIo=true;_outputDirectory=Path.Combine(run,"output");Directory.CreateDirectory(_outputDirectory);
            Require(await LoadFilesAsync(new[]{pdf}),"real five-page PDF loads through normal selection");SetCompactMode(true);ShowMoreTools();
            Require(new[]{"Повернуть страницы PDF…","Разделить PDF…"}.All(t=>SystemsDescendants<Button>(_panelContent).Any(b=>b.Content as string==t)),"PDF More menu exposes rotation and split");
            Click("Повернуть страницы PDF…");var all=_panelContent.Children.OfType<CheckBox>().Single();all.IsChecked=false;all.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var range=_panelContent.Children.OfType<TextBox>().Single();range.Text="6";Require(!Named("Сохранить повёрнутый PDF").IsEnabled,"rotation rejects out-of-document pages before queueing");
            range.Text="2,4-5,2";await ShotAsync("01-rotate.png");Click("Сохранить повёрнутый PDF");await FinishAsync();
            var rotated=_jobs.Last();Require(rotated.HistoryStatus==HistoryStatus.Succeeded&&rotated.Options.PdfRotationDegrees==90&&rotated.Options.PdfPageSelection=="2,4-5,2","rotation UI sends exact angle and selected pages to a real successful task");
            Require((await ReadSystemsPdfOrderAsync(rotated.Result!.OutputPath)).SequenceEqual(new[]{"A1","A2","A3","A4","A5"}),"rotated PDF preserves all original text and page order without rasterizing");
            Require(!PresetCatalog.CanSave(rotated.Options),"source-bound rotation is not saved as a reusable preset");
            Require(await LoadFilesAsync(new[]{pdf}),"original PDF can be selected again");ShowPdfSplit();var size=_panelContent.Children.OfType<TextBox>().Single();size.Text="0";
            Require(!Named("Разделить на PDF").IsEnabled,"split rejects zero pages per part");size.Text="2";await ShotAsync("02-split.png");Click("Разделить на PDF");await FinishAsync();
            var split=_jobs.Last();Require(split.HistoryStatus==HistoryStatus.Succeeded&&Directory.Exists(split.Result!.OutputPath)&&split.Options.PdfSplitPagesPerFile==2,"split UI creates a real result directory with requested grouping");
            var parts=Directory.GetFiles(split.Result!.OutputPath,"*.pdf").OrderBy(p=>p,StringComparer.Ordinal).ToArray();var contents=new List<string>();var counts=new List<int>();
            foreach(var part in parts){var text=await ReadSystemsPdfOrderAsync(part);counts.Add(text.Count);contents.AddRange(text);}
            Require(counts.SequenceEqual(new[]{2,2,1})&&contents.SequenceEqual(new[]{"A1","A2","A3","A4","A5"}),"split results contain consecutive 2/2/1 pages and preserve all text");
            Require(await LoadFilesAsync(new[]{zip}),"real ZIP loads through ordinary context inspection");ShowMoreTools();Click("Просмотреть ZIP и извлечь выбранное…");await InspectAsync();
            var list=_panelContent.Children.OfType<ListBox>().Single();Require(list.Items.Count>2000,"ZIP browser lists a large real archive");
            await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);
            Require(VirtualizingPanel.GetIsVirtualizing(list)&&list.ItemContainerGenerator.ContainerFromIndex(1500)==null,"large ZIP rows are virtualized instead of allocating every row");
            var search=_panelContent.Children.OfType<TextBox>().Single();search.Text="docs/a.txt";Click("Выбрать найденные");search.Text="empty/";Click("Выбрать найденные");
            search.Text="does-not-match";Require(Named("Извлечь выбранное").IsEnabled&&SystemsDescendants<TextBlock>(_panelContent).Any(t=>t.Text.Contains("отмечено: 2")),"search preserves hidden file and directory selections");
            search.Text="docs/";await ShotAsync("03-zip-browser.png");Click("Извлечь выбранное");await FinishAsync();
            var extracted=_jobs.Last();Require(extracted.HistoryStatus==HistoryStatus.Succeeded&&extracted.Options.ZipSelectedEntries.SequenceEqual(new[]{"docs/a.txt","empty/"}),"selected ZIP task captures exactly the chosen entry IDs");
            var folder=extracted.Result!.OutputPath;
            Require(File.ReadAllText(Path.Combine(folder,"docs","a.txt"))=="alpha"&&Directory.Exists(Path.Combine(folder,"empty"))&&!File.Exists(Path.Combine(folder,"docs","b.txt"))&&!Directory.Exists(Path.Combine(folder,"many")),"extraction creates selected content and an empty directory while excluding other files");
            Require(extracted.Options.ZipSourceSha256?.Length==64&&!PresetCatalog.CanSave(extracted.Options),"archive fingerprint is captured and selected extraction stays source-bound");
            ShowZipBrowser();ShowFileInfo();await InspectAsync();Require(SystemsDescendants<TextBlock>(_panelContent).Any(t=>t.Text=="Сведения")&&!_panelContent.Children.OfType<ListBox>().Any(),"closing ZIP inspection prevents a late browser replacing the current card");
            var stale=Path.Combine(run,"изменяемая копия.zip");File.Copy(zip,stale);Require(await LoadFilesAsync(new[]{stale}),"owned stale-archive fixture loads");ShowZipBrowser();await InspectAsync();
            _panelContent.Children.OfType<TextBox>().Single().Text="docs/a.txt";Click("Выбрать найденные");
            await _queueGate.WaitAsync();gateHeld=true;Click("Извлечь выбранное");var staleJob=_jobs.Last();
            using(var changed=ZipFile.Open(stale,ZipArchiveMode.Update)){using var writer=new StreamWriter(changed.CreateEntry("added.txt").Open());writer.Write("changed after preview");}
            _queueGate.Release();gateHeld=false;await FinishAsync();
            Require(staleJob.HistoryStatus==HistoryStatus.Failed&&staleJob.Result==null&&!Directory.EnumerateDirectories(_outputDirectory).Any(p=>Path.GetFileName(p).StartsWith("изменяемая копия_selected",StringComparison.Ordinal)),"changed ZIP is rejected before publishing a result");
            Require(await LoadFilesAsync(new[]{zip}),"original ZIP can be reloaded after the rejected stale snapshot");ShowZipBrowser();await InspectAsync();_panelContent.Children.OfType<TextBox>().Single().Text="docs/a.txt";Click("Выбрать найденные");
            await _queueGate.WaitAsync();gateHeld=true;Click("Извлечь выбранное");var cancelled=_jobs.Last();cancelled.Cancellation.Cancel();_queueGate.Release();gateHeld=false;await FinishAsync();
            Require(cancelled.HistoryStatus==HistoryStatus.Cancelled&&cancelled.Result==null,"queued selected extraction supports cancellation without a result");
            var loaded=await new HistoryStore(_historyStore.FilePath).LoadAsync();
            Require(loaded.Error==null&&loaded.Entries.Count==_jobs.Count&&loaded.Entries.Any(e=>e.Options.Action==ImageAction.PdfRotate)&&loaded.Entries.Any(e=>e.Options.Action==ImageAction.PdfSplit)&&loaded.Entries.Any(e=>e.Options.ZipSelectedEntries.Count==2&&e.Status==HistoryStatus.Succeeded),"all new options and folder results survive history round trip");
            Require(originals.All(p=>SystemsDigest(p.Key)==p.Value),"all original PDF and ZIP bytes remain unchanged");
            Require(!Directory.EnumerateDirectories(_outputDirectory).Any(p=>Path.GetFileName(p).StartsWith(".orbit-",StringComparison.Ordinal)),"successful, cancelled and stale jobs leave no private temporary directories");
            File.WriteAllText(Path.Combine(destination,"passed.json"),JsonSerializer.Serialize(new{checks=checks.Count,items=checks,run,userProfileIo=false,nativeClipboardWrites=false,registryWrites=false,physicalInput=false},new JsonSerializerOptions{WriteIndented=true}));
        }catch(Exception ex){App.Log(ex);File.WriteAllText(Path.Combine(destination,"error.txt"),ex.ToString());}
        finally{if(gateHeld)_queueGate.Release();Quit();}
    }
    private static void WriteDocumentZip(string path)
    {
        using var archive=ZipFile.Open(path,ZipArchiveMode.Create);
        foreach(var pair in new[]{("docs/a.txt","alpha"),("docs/b.txt","beta")}){using var writer=new StreamWriter(archive.CreateEntry(pair.Item1).Open());writer.Write(pair.Item2);}
        archive.CreateEntry("empty/");
        for(int i=0;i<2200;i++){using var writer=new StreamWriter(archive.CreateEntry($"many/{i:0000}.txt",CompressionLevel.Fastest).Open());writer.Write("entry "+i);}
    }
}
