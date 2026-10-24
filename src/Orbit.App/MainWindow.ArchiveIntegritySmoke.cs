using System;
using System.Collections.Generic;
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
    private async Task RunArchiveIntegritySmokeAsync()
    {
        var destination=Path.Combine(AppContext.BaseDirectory,"archive-integrity-smoke");var run=Path.Combine(destination,"run-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        var checks=new List<string>();var copied=new List<string>();var opened=new List<string>();bool gateHeld=false;
        void Require(bool value,string text){if(!value)throw new InvalidOperationException("Archive integrity integration: "+text);checks.Add(text);}
        Button Named(string label)=>SystemsDescendants<Button>(_panelContent).Single(b=>b.Content as string==label);
        void Click(string label)=>Named(label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        T Tagged<T>(string tag)where T:FrameworkElement=>SystemsDescendants<T>(_panelContent).Single(c=>c.Tag as string==tag);
        async Task FinishAsync(){await Task.WhenAll(_jobTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(120));await FlushHistoryAsync();}
        async Task ReadersAsync()=>await Task.WhenAll(_resultToolTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(60));
        async Task ShotAsync(string name){_toast.Visibility=Visibility.Collapsed;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);Capture(Path.Combine(destination,name));}
        void SaveRecipe(JobEntry job,string name){ShowSavePreset(job);_panelContent.Children.OfType<TextBox>().Single().Text=name;Click("Сохранить пресет");}
        try{
            Directory.CreateDirectory(run);File.Delete(Path.Combine(destination,"passed.json"));File.Delete(Path.Combine(destination,"error.txt"));
            Require(IsDiagnosticRun&&_bridge is null&&_jobs.Count==0&&!ProductivityIoAllowed&&!PersonalizationIoAllowed,"diagnostic avoids native hotkeys, user stores, clipboard and shell integration");
            ShowMoreTools();Require(SystemsDescendants<Button>(_panelContent).Any(b=>b.Content as string=="Проверить SHA-256 файла…")&&SystemsDescendants<Button>(_panelContent).Any(b=>b.Content as string=="Проверить файл контрольных сумм…"),"checksum tools are reachable before loading any source");
            var folder=Path.Combine(run,"пакет");Directory.CreateDirectory(Path.Combine(folder,"пустая папка"));Directory.CreateDirectory(Path.Combine(folder,"вложенная"));
            var text=Path.Combine(folder,"файл [1].txt");var binary=Path.Combine(folder,"вложенная","данные.bin");var empty=Path.Combine(folder,"пустой.txt");
            File.WriteAllText(text,"Orbit TAR и SHA-256\n");File.WriteAllBytes(binary,Enumerable.Range(0,1024).Select(i=>(byte)(i%251)).ToArray());File.WriteAllBytes(empty,Array.Empty<byte>());
            var originals=new[]{text,binary,empty}.ToDictionary(p=>p,SystemsDigest);
            _historyStore=new HistoryStore(Path.Combine(run,"history.json"));_presetStore=new PresetStore(Path.Combine(run,"presets.json"));_allowDiagnosticProductivityIo=true;
            _outputDirectory=Path.Combine(run,"output");Directory.CreateDirectory(_outputDirectory);_checksumCopyOverride=value=>copied.Add(value);_resultOpenOverride=(path,_)=>{opened.Add(path);return Task.CompletedTask;};
            Require(await LoadFilesAsync(new[]{folder,text}),"folder and overlapping child load for aggregate archive creation");SetCompactMode(true);ShowMoreTools();
            Require(SystemsDescendants<Button>(_panelContent).Any(b=>b.Content as string=="Создать TAR / TAR.GZ…")&&SystemsDescendants<Button>(_panelContent).Any(b=>b.Content as string=="Проверить SHA-256 файла…"),"tools expose TAR creation and checksum verification");
            ShowTarCreate();Tagged<ComboBox>("tar-compression").SelectedIndex=0;await ShotAsync("01-create-tar.png");Click("Создать TAR-архив");await FinishAsync();var gzip=_jobs.Last();
            Require(_jobs.Count==1&&gzip.HistoryStatus==HistoryStatus.Succeeded&&gzip.Result?.Format=="TAR.GZ"&&gzip.InputPaths.Count==2,"overlapping files and folders create one real TAR.GZ aggregate job");
            Require(await LoadFilesAsync(new[]{gzip.Result!.OutputPath})&&_selectionKind==ToolkitKind.Tar&&_metadata?.Entries==6,"TAR.GZ is decoded and classified with canonical deduplicated contents");
            Require(_radial.Children.OfType<Orbit.App.Controls.SectorButton>().Select(b=>b.Action).SequenceEqual(new[]{ImageAction.ExtractTar,ImageAction.TarList,ImageAction.FileHash,ImageAction.FileInfo}),"TAR radial menu exposes archive-specific actions");
            ShowTarBrowser();await ReadersAsync();var list=Tagged<ListBox>("tar-entries");Require(list.Items.Count==6,"browser lists nested files, empty file and empty directory");
            Tagged<TextBox>("tar-search").Text="данные.bin";Require(list.Items.Count==1,"TAR browser searches by relative path");await ShotAsync("02-tar-browser.png");
            Click("Сохранить список TXT");await FinishAsync();var listing=_jobs.Last();Require(listing.HistoryStatus==HistoryStatus.Succeeded&&File.ReadAllText(listing.Result!.OutputPath).Contains("данные.bin"),"browser saves actual complete contents to TXT despite current search");
            DispatchAction(ImageAction.ExtractTar);await FinishAsync();var unpacked=_jobs.Last();
            Require(unpacked.HistoryStatus==HistoryStatus.Succeeded&&unpacked.Result?.Format=="FOLDER","one-click compact TAR extraction publishes a new folder");
            Require(originals.All(p=>SystemsDigest(Path.Combine(unpacked.Result!.OutputPath,Path.GetRelativePath(run,p.Key)))==p.Value)&&Directory.Exists(Path.Combine(unpacked.Result!.OutputPath,"пакет","пустая папка")),"extracted content hashes and empty directory match originals");
            await OpenKnownResultAsync(gzip.Result.OutputPath,false);Require(opened.Last()==gzip.Result.OutputPath,"validated TAR.GZ result can use intercepted Open route");
            Require(await LoadFilesAsync(new[]{folder}),"source folder reloads for uncompressed TAR");ShowTarCreate();Tagged<ComboBox>("tar-format").SelectedItem="TAR";
            Require(!Tagged<ComboBox>("tar-compression").IsEnabled,"uncompressed TAR disables inactive GZip compression choice");Click("Создать TAR-архив");await FinishAsync();var tar=_jobs.Last();
            Require(tar.HistoryStatus==HistoryStatus.Succeeded&&tar.Result?.Format=="TAR"&&tar.Options.TarCompression=="Optimal","plain TAR queue uses canonical inactive compression default");
            SaveRecipe(gzip,"Быстрый TAR.GZ");var preset=_customPresets.Single();Require(preset.Options is{Action:ImageAction.ArchiveTar,TarArchiveFormat:"TAR.GZ",TarCompression:"Fastest",OutputDirectory:null},"archive preset retains reusable format and compression without old output path");
            ApplyCurrentRecipe(preset);await FinishAsync();var collision=_jobs.Last();Require(collision.HistoryStatus==HistoryStatus.Succeeded&&collision.Result!.OutputPath!=gzip.Result.OutputPath&&collision.Result.OutputPath.EndsWith(".tar.gz",StringComparison.OrdinalIgnoreCase),"preset creates unique compound-extension TAR.GZ without replacing earlier archive");
            var tgz=Path.Combine(run,"копия.tgz");File.Copy(gzip.Result.OutputPath,tgz);Require(await LoadFilesAsync(new[]{tgz})&&_selectionKind==ToolkitKind.Tar,"ordinary .tgz input is recognized by real TAR decoder");
            ShowTarBrowser();ShowFileInfo();await ReadersAsync();Require(!SystemsDescendants<ListBox>(_panelContent).Any(l=>l.Tag as string=="tar-entries"),"late TAR inspection cannot overwrite a different panel");
            ShowTarBrowser();await ReadersAsync();var stale=Named("Распаковать весь архив");int count=_jobs.Count;Require(await LoadFilesAsync(new[]{text}),"plain file replaces an inspected archive");stale.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Require(_jobs.Count==count,"stale TAR browser cannot extract a newly selected file");
            ShowFileChecksum();Tagged<TextBox>("checksum-expected").Text="x";Require(!Named("Проверить файл").IsEnabled,"invalid expected SHA-256 is blocked before read");
            Tagged<TextBox>("checksum-expected").Text=originals[text].ToLowerInvariant();Click("Проверить файл");await ReadersAsync();
            bool verifiedSingle=SystemsDescendants<TextBlock>(_panelContent).Any(t=>t.Text=="Совпадает")&&Tagged<TextBox>("checksum-actual").Text.Equals(originals[text],StringComparison.OrdinalIgnoreCase);
            Require(verifiedSingle,"single checksum card verifies real bytes and displays actual hash"+(verifiedSingle?"":": "+string.Join(" | ",SystemsDescendants<TextBlock>(_panelContent).Select(t=>t.Text))));
            Click("Копировать SHA-256");Require(copied.Count==1&&copied[0].Equals(originals[text],StringComparison.OrdinalIgnoreCase),"explicit copy uses diagnostic capture without system clipboard");
            Tagged<TextBox>("checksum-expected").Text=new string('0',64);Click("Проверить файл");await ReadersAsync();Require(SystemsDescendants<TextBlock>(_panelContent).Any(t=>t.Text=="Не совпадает"),"wrong expected SHA-256 is shown as mismatch");await ShotAsync("03-sha-file.png");
            Require(await LoadFilesAsync(new[]{folder}),"folder loads for generating a compatible Orbit manifest");DispatchAction(ImageAction.FileHash);await FinishAsync();var report=_jobs.Last();
            Require(report.HistoryStatus==HistoryStatus.Succeeded,"existing folder hash command creates a real SHA-256 manifest");
            count=_jobs.Count;ShowManifestChecksum(report.Result!.OutputPath,run);Click("Проверить список");await ReadersAsync();
            Require(_jobs.Count==count&&SystemsDescendants<TextBlock>(_panelContent).Any(t=>t.Text=="Совпало: 3 из 3 · всё совпадает"),"read-only manifest card round-trips Orbit folder hashes including empty file without creating a processing job");
            Require(Tagged<ListBox>("checksum-results").Items.Count==3,"successful manifest lists every verified file");await ShotAsync("04-sha-list.png");
            var problem=Path.Combine(run,"расхождения.sha256");File.WriteAllText(problem,originals[text]+"  пакет/файл [1].txt\n"+new string('0',64)+"  пакет/вложенная/данные.bin\n"+originals[text]+"  пакет/нет.txt\n");
            ShowManifestChecksum(problem,run);Click("Проверить список");await ReadersAsync();Require(SystemsDescendants<TextBlock>(_panelContent).Any(t=>t.Text=="Совпало: 1 из 3 · есть расхождения")&&Tagged<ListBox>("checksum-results").Items.Count==2,"manifest distinguishes mismatch and missing file, filtering to problems");
            var problems=Tagged<CheckBox>("checksum-problems");problems.IsChecked=false;problems.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Require(Tagged<ListBox>("checksum-results").Items.Count==3,"problem filter can reveal matching entries too");
            var malformed=Path.Combine(run,"повреждённый список.sha256");File.WriteAllText(malformed,"bad hash  пакет/файл [1].txt\n");ShowManifestChecksum(malformed,run);Click("Проверить список");await ReadersAsync();
            Require(SystemsDescendants<TextBlock>(_panelContent).Any(t=>t.Text.StartsWith("Не удалось проверить список:",StringComparison.Ordinal))&&!SystemsDescendants<ListBox>(_panelContent).Any(t=>t.Tag as string=="checksum-results"),"malformed manifest displays a clear error without partial or false-success results");
            ShowFileChecksumFor(binary);Tagged<TextBox>("checksum-expected").Text=originals[binary];Click("Проверить файл");ShowFileInfo();await ReadersAsync();Require(!SystemsDescendants<TextBox>(_panelContent).Any(t=>t.Tag as string=="checksum-actual"),"closing checksum card cancels reading and prevents late results");
            Require(await LoadFilesAsync(new[]{folder}),"folder reloads for queued archive cancellation");await _queueGate.WaitAsync();gateHeld=true;ShowTarCreate();Click("Создать TAR-архив");var canceled=_jobs.Last();canceled.Cancellation.Cancel();_queueGate.Release();gateHeld=false;await FinishAsync();
            Require(canceled.HistoryStatus==HistoryStatus.Cancelled&&canceled.Result==null,"queued TAR creation cancels without publishing partial output");
            var history=await new HistoryStore(_historyStore.FilePath).LoadAsync();Require(history.Error==null&&history.Entries.Count==_jobs.Count&&history.Entries.Any(e=>e.Options is{Action:ImageAction.ArchiveTar,TarArchiveFormat:"TAR.GZ",TarCompression:"Fastest"}),"history restores archive formats, compression, folder results and cancellation");
            Require(originals.All(p=>SystemsDigest(p.Key)==p.Value),"archive and verification workflows preserve all original source hashes");
            Require(!Directory.GetDirectories(_outputDirectory).Any(p=>Path.GetFileName(p).StartsWith(".orbit-",StringComparison.Ordinal)),"completed, failed and canceled tasks remove private staging");
            File.WriteAllText(Path.Combine(destination,"passed.json"),JsonSerializer.Serialize(new{checks=checks.Count,items=checks,run,userProfileIo=false,nativeClipboardWrites=false,registryWrites=false,nativeShellLaunches=0,physicalInput=false},new JsonSerializerOptions{WriteIndented=true}));
        }catch(Exception ex){App.Log(ex);File.WriteAllText(Path.Combine(destination,"error.txt"),ex.ToString());}
        finally{if(gateHeld)_queueGate.Release();Quit();}
    }
}
