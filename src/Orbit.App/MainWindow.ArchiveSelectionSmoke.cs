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
using Orbit.Core.Documents;
using Orbit.Core.Presets;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private async Task RunArchiveSelectionSmokeAsync()
    {
        var destination=Path.Combine(AppContext.BaseDirectory,"archive-selection-smoke");var run=Path.Combine(destination,"run-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        const string fixturePassword="  Выбор猫🔐 [fixture]  ";const string privateMember="HiddenMember_79251.txt";
        var checks=new List<string>();bool gateHeld=false;
        void Require(bool value,string text){if(!value)throw new InvalidOperationException("Archive selection integration: "+text);checks.Add(text);}
        T Tagged<T>(string tag)where T:FrameworkElement=>SystemsDescendants<T>(_panelContent).Single(c=>c.Tag as string==tag);
        Button Named(string text)=>SystemsDescendants<Button>(_panelContent).Single(b=>b.Content as string==text);
        void Click(string text)=>Named(text).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        async Task FinishAsync(){await Task.WhenAll(_jobTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(120));await FlushHistoryAsync();}
        async Task ReadersAsync()=>await Task.WhenAll(_resultToolTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(90));
        async Task ShotAsync(string name){_toast.Visibility=Visibility.Collapsed;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);Capture(Path.Combine(destination,name));}
        void Pick(string prefix,string name){var list=Tagged<ListBox>(prefix+"-entries");list.SelectedItems.Add(list.Items.Cast<ArchiveBrowserRow>().Single(row=>row.Name.TrimEnd('/')==name.TrimEnd('/')));}
        async Task BrowseAsync(string path,bool sevenZip,bool locked=false){Require(await LoadFilesAsync(new[]{path}),"owned archive loads: "+Path.GetFileName(path));if(sevenZip)ShowSevenZipBrowser();else ShowTarBrowser();if(locked){Tagged<PasswordBox>("sevenzip-password").Password=fixturePassword;Click("Открыть содержимое");}await ReadersAsync();}
        string[] Files(string root)=>Directory.GetFiles(root,"*",SearchOption.AllDirectories).Select(p=>Path.GetRelativePath(root,p).Replace('\\','/')).Order(StringComparer.Ordinal).ToArray();
        bool ContainsPrivate(JsonElement value)=>value.ValueKind switch{
            JsonValueKind.String=>(value.GetString()??"").Contains(privateMember,StringComparison.Ordinal)||(value.GetString()??"").Contains(fixturePassword,StringComparison.Ordinal),
            JsonValueKind.Object=>value.EnumerateObject().Any(p=>ContainsPrivate(p.Value)),JsonValueKind.Array=>value.EnumerateArray().Any(ContainsPrivate),_=>false};
        try{
            Directory.CreateDirectory(run);File.Delete(Path.Combine(destination,"passed.json"));File.Delete(Path.Combine(destination,"error.txt"));
            Require(IsDiagnosticRun&&_bridge is null&&_jobs.Count==0&&!ProductivityIoAllowed&&!PersonalizationIoAllowed&&Path.GetTempPath().StartsWith(AppContext.BaseDirectory,StringComparison.OrdinalIgnoreCase),"isolated diagnostic owns temp and avoids user settings, physical hotkeys, registry and clipboard");
            var source=Path.Combine(run,"пакет 猫");var empty=Path.Combine(source,"empty");var nested=Path.Combine(source,"nested");Directory.CreateDirectory(empty);Directory.CreateDirectory(nested);
            var alpha=Path.Combine(source,"alpha [1].txt");var hidden=Path.Combine(nested,privateMember);var sibling=Path.Combine(run,"пакет 猫2");Directory.CreateDirectory(sibling);var outside=Path.Combine(sibling,"outside.txt");var loose=Path.Combine(run,"@loose.txt");
            File.WriteAllText(alpha,"selected alpha\n");File.WriteAllText(hidden,"selected nested data\n");File.WriteAllText(outside,"prefix sibling remains excluded\n");File.WriteAllText(loose,"independent row\n");
            var originals=new[]{alpha,hidden,outside,loose}.ToDictionary(p=>p,SystemsDigest);var paths=new[]{source,sibling,loose};
            _historyStore=new HistoryStore(Path.Combine(run,"history.json"));_presetStore=new PresetStore(Path.Combine(run,"presets.json"));_allowDiagnosticProductivityIo=true;
            _outputDirectory=Path.Combine(run,"output");Directory.CreateDirectory(_outputDirectory);SetCompactMode(true);
            var sevenZip=new SevenZipArchiveEngine();var tar=new TarArchiveEngine();
            var plain=await sevenZip.CreateAsync(paths,new SevenZipCreateOptions{OutputDirectory=run,Compression="Fastest"});
            string headerArchive,dataArchive;using(var password=new SevenZipPassword(fixturePassword)){
                headerArchive=(await sevenZip.CreateWithPasswordAsync(paths,new SevenZipCreateOptions{OutputDirectory=run,Compression="Fastest"},password,true)).OutputPath;
                dataArchive=(await sevenZip.CreateWithPasswordAsync(paths,new SevenZipCreateOptions{OutputDirectory=run,Compression="Fastest"},password,false)).OutputPath;
            }
            var uncompressed=await tar.CreateAsync(paths,new TarCreateOptions{Format="TAR",OutputDirectory=run});
            var compressed=await tar.CreateAsync(paths,new TarCreateOptions{Format="TAR.GZ",OutputDirectory=run});var tgz=Path.Combine(run,"копия.tgz");File.Copy(compressed.OutputPath,tgz);
            var archiveHashes=new[]{plain.OutputPath,headerArchive,dataArchive,uncompressed.OutputPath,compressed.OutputPath,tgz}.ToDictionary(p=>p,SystemsDigest);
            await BrowseAsync(plain.OutputPath,true);
            var dropPoint=Enumerable.Range(1,37).SelectMany(x=>Enumerable.Range(1,37).Select(y=>new Point(x*20,y*20))).First(point=>_radial.ActionAt(point).HasValue);
            Require(!_radialView.IsVisible&&_radial.ActionAt(dropPoint).HasValue&&!DropActionAt(dropPoint).HasValue,"a visible selector cannot activate an underlying hidden radial sector through file drop");
            Require(Tagged<ListBox>("sevenzip-entries").SelectionMode==SelectionMode.Multiple&&!Named("Извлечь выбранное").IsEnabled,"7z browser begins with a visible disabled selection action and multiple selection");
            Tagged<TextBox>("sevenzip-search").Text="alpha";Pick("sevenzip",Path.GetFileName(source)+"/alpha [1].txt");Tagged<TextBox>("sevenzip-search").Text="@loose";Click("Выбрать найденные");
            Require(Tagged<TextBlock>("sevenzip-count").Text.Contains("отмечено: 2",StringComparison.Ordinal)&&Named("Извлечь выбранное").IsEnabled,"search preserves hidden marks and Select found adds literal file rows");
            Click("Снять выбор");Require(!Named("Извлечь выбранное").IsEnabled,"Clear removes visible and hidden marks");Tagged<TextBox>("sevenzip-search").Text="";
            Pick("sevenzip",Path.GetFileName(source));Pick("sevenzip",Path.GetFileName(source)+"/alpha [1].txt");await ShotAsync("01-7z-selection.png");Click("Извлечь выбранное");await FinishAsync();var first=_jobs.Last();
            Require(first.HistoryStatus==HistoryStatus.Succeeded&&first.Options.Action==ImageAction.SevenZipExtractSelected&&first.Options.ArchiveSelectedCount==2&&first.Result?.Format=="FOLDER","selected7z creates one real job with exact row count");
            Require(Files(first.Result!.OutputPath).SequenceEqual(new[]{Path.GetFileName(source)+"/alpha [1].txt",Path.GetFileName(source)+"/nested/"+privateMember}.Order(StringComparer.Ordinal)),"selected directory includes all descendants once and excludes a prefix sibling and loose file");
            Require(SystemsDigest(Path.Combine(first.Result.OutputPath,Path.GetFileName(source),"alpha [1].txt"))==originals[alpha]&&SystemsDigest(Path.Combine(first.Result.OutputPath,Path.GetFileName(source),"nested",privateMember))==originals[hidden]&&Directory.Exists(Path.Combine(first.Result.OutputPath,Path.GetFileName(source),"empty")),"selected7z preserves exact file hashes, implicit parents and empty directories");
            Require(first.Result.Bytes==new FileInfo(alpha).Length+new FileInfo(hidden).Length,"selected result byte count excludes unselected data");
            Require(!PresetCatalog.CanSave(first.Options)&&!RepeatRecipe.TryCapture(first.Options,true,out _),"source-bound selection cannot become a reusable preset or quick repeat");
            await BrowseAsync(plain.OutputPath,true);Tagged<TextBox>("sevenzip-search").Text="empty";Click("Выбрать найденные");Click("Извлечь выбранное");await FinishAsync();var emptyResult=_jobs.Last();
            Require(emptyResult.HistoryStatus==HistoryStatus.Succeeded&&emptyResult.Result!.Bytes==0&&Files(emptyResult.Result.OutputPath).Length==0&&Directory.Exists(Path.Combine(emptyResult.Result.OutputPath,Path.GetFileName(source),"empty")),"selecting only an empty directory publishes its full path without unrelated files");
            await BrowseAsync(plain.OutputPath,true);Pick("sevenzip","@loose.txt");var stale=Named("Извлечь выбранное");int count=_jobs.Count;Require(await LoadFilesAsync(new[]{loose}),"plain file replaces selected archive");stale.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Require(_jobs.Count==count,"detached selected-extraction control cannot use a replacement selection");
            var changed=Path.Combine(run,"changed.7z");File.Copy(plain.OutputPath,changed);await BrowseAsync(changed,true);Pick("sevenzip","@loose.txt");File.Copy((await sevenZip.CreateAsync(new[]{alpha},new SevenZipCreateOptions{OutputDirectory=run})).OutputPath,changed,true);int before=Directory.GetDirectories(_outputDirectory).Length;Click("Извлечь выбранное");await FinishAsync();var rejected=_jobs.Last();
            Require(rejected.HistoryStatus==HistoryStatus.Failed&&rejected.Result is null&&Directory.GetDirectories(_outputDirectory).Length==before,"changed archive fingerprint rejects the old selection without a published result");
            await BrowseAsync(headerArchive,true,true);Tagged<TextBox>("sevenzip-search").Text=privateMember;Click("Выбрать найденные");Click("Извлечь выбранное");await FinishAsync();var protectedJob=_jobs.Last();
            Require(protectedJob.HistoryStatus==HistoryStatus.Succeeded&&protectedJob.Options is{Action:ImageAction.SevenZipExtractSelected,SevenZipPasswordProtected:true,ArchiveSelectedCount:1}&&protectedJob.Password is null&&_sevenZipBrowsePassword is null,"header-protected selection uses and releases independent panel/job credentials");
            Require(Files(protectedJob.Result!.OutputPath).SequenceEqual(new[]{Path.GetFileName(source)+"/nested/"+privateMember})&&SystemsDigest(Path.Combine(protectedJob.Result.OutputPath,Path.GetFileName(source),"nested",privateMember))==originals[hidden],"encrypted selective extraction publishes only the marked member with exact bytes");
            await BrowseAsync(dataArchive,true,true);Pick("sevenzip","@loose.txt");Click("Извлечь выбранное");await FinishAsync();Require(_jobs.Last().HistoryStatus==HistoryStatus.Succeeded&&Files(_jobs.Last().Result!.OutputPath).SequenceEqual(new[]{"@loose.txt"}),"visible-name encrypted7z supports the same selective path");
            await FlushHistoryAsync();var history=await new HistoryStore(_historyStore.FilePath).LoadAsync();
            Require(history.Error is null&&history.Entries.Any(e=>e.Options is{Action:ImageAction.SevenZipExtractSelected,SevenZipPasswordProtected:true,ArchiveSelectedCount:1}),"history reload keeps selected count and protection flags");
            Require(history.Entries.Where(e=>e.Options.Action==ImageAction.SevenZipExtractSelected).All(e=>e.Options.ArchiveSelectedEntries.Count==0&&e.Options.ArchiveSourceSha256 is null),"history contains no durable member names or archive fingerprints");
            using(var json=JsonDocument.Parse(File.ReadAllText(_historyStore.FilePath)))Require(!ContainsPrivate(json.RootElement),"decoded history never exposes a hidden member name or entered fixture password");
            count=_jobs.Count;await RetryHistoryJobAsync(protectedJob);Require(_jobs.Count==count&&Tagged<PasswordBox>("sevenzip-password").SecurePassword.Length==0,"protected selection retry requests a new password and creates no replay job");Tagged<PasswordBox>("sevenzip-password").Password=fixturePassword;Click("Открыть содержимое");await ReadersAsync();Require(!Named("Извлечь выбранное").IsEnabled,"fresh retry browser never restores old hidden marks");ClosePanel();
            foreach(var archive in new[]{uncompressed.OutputPath,compressed.OutputPath,tgz}){
                await BrowseAsync(archive,false);Require(!Named("Извлечь выбранное").IsEnabled,"TAR family browser starts with no chosen rows: "+Path.GetFileName(archive));
                Tagged<TextBox>("tar-search").Text="alpha";Click("Выбрать найденные");Tagged<TextBox>("tar-search").Text="empty";Click("Выбрать найденные");
                Require(Tagged<TextBlock>("tar-count").Text.Contains("отмечено: 2",StringComparison.Ordinal),"TAR filter preserves hidden selections: "+Path.GetFileName(archive));
                if(archive==compressed.OutputPath)await ShotAsync("02-targz-selection.png");Click("Извлечь выбранное");await FinishAsync();var selected=_jobs.Last();
                Require(selected.HistoryStatus==HistoryStatus.Succeeded&&selected.Options.Action==ImageAction.TarExtractSelected&&Files(selected.Result!.OutputPath).SequenceEqual(new[]{Path.GetFileName(source)+"/alpha [1].txt"})&&Directory.Exists(Path.Combine(selected.Result.OutputPath,Path.GetFileName(source),"empty")),"TAR family selective extraction publishes exact marked files/directories: "+Path.GetFileName(archive));
                Require(SystemsDigest(Path.Combine(selected.Result!.OutputPath,Path.GetFileName(source),"alpha [1].txt"))==originals[alpha],"TAR family selected bytes match source: "+Path.GetFileName(archive));
            }
            count=_jobs.Count;await RetryHistoryJobAsync(_jobs.Last());await ReadersAsync();Require(_jobs.Count==count&&!Named("Извлечь выбранное").IsEnabled,"TAR history retry opens a new empty selector instead of replaying a saved recipe");
            var removedOutputRetry=new JobEntry(plain.OutputPath,first.Options with{OutputDirectory=Path.Combine(run,"removed-output")}){InputPaths=new[]{plain.OutputPath},Finished=true};
            await RetryHistoryJobAsync(removedOutputRetry);await ReadersAsync();Require(_selectionKind==ToolkitKind.SevenZip&&!Named("Извлечь выбранное").IsEnabled,"selected retry ignores the unavailable old destination and opens a fresh browser for current output settings");
            await BrowseAsync(compressed.OutputPath,false);
            ShowMoreTools();Tagged<TextBox>("tool-search").Text="выборочно";Require(SystemsDescendants<Button>(_panelContent).Any(b=>b.Content as string=="Выбрать файлы из TAR…"),"tool search discovers source-bound TAR extraction through a selective alias");
            await BrowseAsync(headerArchive,true,true);Pick("sevenzip","@loose.txt");await _queueGate.WaitAsync();gateHeld=true;Click("Извлечь выбранное");var cancelled=_jobs.Last();cancelled.Cancellation.Cancel();_queueGate.Release();gateHeld=false;await FinishAsync();Require(cancelled.HistoryStatus==HistoryStatus.Cancelled&&cancelled.Result is null&&cancelled.Password is null,"queued encrypted selective extraction cancels without retaining a credential or publishing output");
            await FlushHistoryAsync();history=await new HistoryStore(_historyStore.FilePath).LoadAsync();Require(history.Error is null&&history.Entries.Any(e=>e.Options is{Action:ImageAction.TarExtractSelected,ArchiveSelectedCount:2})&&history.Entries.Where(e=>e.Options.Action is ImageAction.TarExtractSelected or ImageAction.SevenZipExtractSelected).All(e=>e.Options.ArchiveSelectedEntries.Count==0&&e.Options.ArchiveSourceSha256 is null),"both selector actions restore only counts and fresh-selection intent");
            Require(originals.All(p=>SystemsDigest(p.Key)==p.Value)&&archiveHashes.All(p=>SystemsDigest(p.Key)==p.Value),"all original fixture files and archives retain their initial SHA256");
            Require(!Directory.GetDirectories(_outputDirectory).Any(p=>Path.GetFileName(p).StartsWith(".orbit-",StringComparison.Ordinal)),"selective success, stale failure and cancellation clean private staging");
            File.WriteAllText(Path.Combine(destination,"passed.json"),JsonSerializer.Serialize(new{checks=checks.Count,items=checks,run,userSettingsWrites=false,nativeClipboardWrites=false,registryWrites=false,physicalInput=false},new JsonSerializerOptions{WriteIndented=true}));
        }catch(Exception ex){App.Log(ex);File.WriteAllText(Path.Combine(destination,"error.txt"),ex.ToString());}
        finally{if(gateHeld)_queueGate.Release();Quit();}
    }
}
