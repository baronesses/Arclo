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
    private async Task RunSevenZipPasswordSmokeAsync()
    {
        var destination=Path.Combine(AppContext.BaseDirectory,"sevenzip-password-smoke");var run=Path.Combine(destination,"run-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        const string fixturePassword="  Орбита猫🔐e\u0301 [fixture]  ";
        var checks=new List<string>();bool gateHeld=false;
        void Require(bool value,string text){if(!value)throw new InvalidOperationException("Protected7z integration: "+text);checks.Add(text);}
        Button Named(string label)=>SystemsDescendants<Button>(_panelContent).Single(b=>b.Content as string==label);
        void Click(string label)=>Named(label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        T Tagged<T>(string tag)where T:FrameworkElement=>SystemsDescendants<T>(_panelContent).Single(c=>c.Tag as string==tag);
        void Password(string value=fixturePassword)=>Tagged<PasswordBox>("sevenzip-password").Password=value;
        void Protect(bool headers=true){var box=Tagged<CheckBox>("sevenzip-protect");box.IsChecked=true;box.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));Tagged<CheckBox>("sevenzip-encrypt-headers").IsChecked=headers;}
        async Task FinishAsync(){await Task.WhenAll(_jobTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(120));await FlushHistoryAsync();}
        async Task ReadersAsync()=>await Task.WhenAll(_resultToolTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(60));
        async Task ShotAsync(string name){_toast.Visibility=Visibility.Collapsed;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);Capture(Path.Combine(destination,name));}
        bool Disposed(SevenZipPassword? password){try{using var copy=password?.Clone();return false;}catch(ObjectDisposedException){return true;}}
        try{
            Directory.CreateDirectory(run);File.Delete(Path.Combine(destination,"passed.json"));File.Delete(Path.Combine(destination,"error.txt"));
            Require(IsDiagnosticRun&&_bridge is null&&_jobs.Count==0&&!ProductivityIoAllowed&&!PersonalizationIoAllowed&&Path.GetTempPath().StartsWith(AppContext.BaseDirectory,StringComparison.OrdinalIgnoreCase),"isolated runtime owns temp and avoids user settings, hotkeys, clipboard and shell writes");
            var folder=Path.Combine(run,"пакет 猫");Directory.CreateDirectory(Path.Combine(folder,"пусто"));var source=Path.Combine(folder,"данные.txt");File.WriteAllText(source,"Protected archive Unicode payload\n");var sourceHash=SystemsDigest(source);
            _historyStore=new HistoryStore(Path.Combine(run,"history.json"));_presetStore=new PresetStore(Path.Combine(run,"presets.json"));_allowDiagnosticProductivityIo=true;
            _outputDirectory=Path.Combine(run,"output");Directory.CreateDirectory(_outputDirectory);SetCompactMode(true);
            Require(await LoadFilesAsync(new[]{folder}),"folder loads for password creation");ShowSevenZipCreate();
            Require(Tagged<CheckBox>("sevenzip-protect").IsChecked!=true&&Tagged<PasswordBox>("sevenzip-password").SecurePassword.Length==0,"password creation is opt-in and begins with blank masked fields");
            Protect();Password();Tagged<PasswordBox>("sevenzip-password-confirm").Password="different fixture";Click("Создать 7z-архив");
            Require(_jobs.Count==0&&Tagged<TextBlock>("sevenzip-password-status").Text.Contains("не совпадают",StringComparison.Ordinal),"mismatched confirmation blocks queueing");
            Require(Tagged<PasswordBox>("sevenzip-password").SecurePassword.Length==0&&Tagged<PasswordBox>("sevenzip-password-confirm").SecurePassword.Length==0,"mismatch clears both password fields");
            Password();Tagged<PasswordBox>("sevenzip-password-confirm").Password=fixturePassword;await ShotAsync("01-protected-create.png");
            var oldPasswordBox=Tagged<PasswordBox>("sevenzip-password");Click("Создать 7z-архив");await FinishAsync();var first=_jobs.Last();
            Require(first.HistoryStatus==HistoryStatus.Succeeded&&first.Options is{SevenZipPasswordProtected:true,SevenZipEncryptHeaders:true}&&first.Password is null,"protected create completes with encryption flags and releases its credential");
            Require(oldPasswordBox.SecurePassword.Length==0,"queueing clears detached password controls");
            var headerArchive=first.Result!.OutputPath;var headerHash=SystemsDigest(headerArchive);
            Require(!PresetCatalog.CanSave(first.Options)&&!RepeatRecipe.TryCapture(first.Options,true,out _),"protected recipes cannot enter presets or quick repeat");
            var engine=new SevenZipArchiveEngine();using(var password=new SevenZipPassword(fixturePassword)){
                var listing=await engine.InspectAsync(headerArchive,password);Require(listing.Entries.Count==3&&listing.Entries.Any(e=>e.RelativeName.EndsWith("данные.txt",StringComparison.Ordinal)),"created encrypted headers open with exact Unicode and edge-space password");
            }
            Require(await LoadFilesAsync(new[]{headerArchive})&&_metadata is{Kind:ToolkitKind.SevenZip,ArchiveLocked:true}&&_metadata.Description.Contains("пароль",StringComparison.Ordinal),"header-encrypted source loads into a locked7z radial context");
            Require(!_metadata!.Description.Contains("элементов: 0",StringComparison.Ordinal),"locked content is not presented as an empty archive");
            ShowSevenZipBrowser();Password("wrong fixture");Click("Открыть содержимое");await ReadersAsync();
            Require(!SystemsDescendants<ListBox>(_panelContent).Any()&&!Tagged<TextBlock>("sevenzip-password-status").Text.Contains("wrong fixture",StringComparison.Ordinal)&&_sevenZipBrowsePassword is null,"wrong browser password gives a safe error without content or retained credential");
            Password();Click("Открыть содержимое");await ReadersAsync();Require(Tagged<ListBox>("sevenzip-entries").Items.Count==3&&_sevenZipBrowsePassword is not null,"browser unlocks protected contents and retains a panel-local credential");
            Require(SystemsDescendants<TextBlock>(_panelContent).Any(t=>t.Text=="Содержимое 7z")&&Named("‹") is not null,"unlock preserves the panel title and visible return-to-menu control");
            Tagged<TextBox>("sevenzip-search").Text="данные";Require(Tagged<ListBox>("sevenzip-entries").Items.Count==1,"protected contents support the usual path search");await ShotAsync("02-protected-browser.png");
            var browserPassword=_sevenZipBrowsePassword;Click("Распаковать весь 7z");await FinishAsync();var extraction=_jobs.Last();
            Require(extraction.HistoryStatus==HistoryStatus.Succeeded&&SystemsDigest(Path.Combine(extraction.Result!.OutputPath,Path.GetFileName(folder),"данные.txt"))==sourceHash,"browser extracts the full protected archive with matching source bytes");
            Require(_sevenZipBrowsePassword is null&&Disposed(browserPassword)&&extraction.Password is null,"closing browser after extraction disposes panel and job credentials");
            ShowSevenZipBrowser();Password();var detached=Tagged<PasswordBox>("sevenzip-password");ClosePanel();
            Require(detached.SecurePassword.Length==0&&_sevenZipPasswordBoxes.Count==0,"closing an unsubmitted prompt clears its masked controls");
            DispatchAction(ImageAction.Extract7z);Require(Tagged<PasswordBox>("sevenzip-password").SecurePassword.Length==0,"radial extract asks for a new password");Password("wrong fixture");Click("Распаковать с паролем");await FinishAsync();var failed=_jobs.Last();
            Require(failed.HistoryStatus==HistoryStatus.Failed&&failed.Result is null&&failed.Password is null&&!failed.Detail.Contains("wrong fixture",StringComparison.Ordinal),"wrong queued password fails safely without a result or retained credential");
            int count=_jobs.Count;await RetryHistoryJobAsync(failed);Require(_jobs.Count==count&&Tagged<PasswordBox>("sevenzip-password").SecurePassword.Length==0,"history retry requests a new credential before queueing");Password();Click("Распаковать с паролем");await FinishAsync();Require(_jobs.Last().HistoryStatus==HistoryStatus.Succeeded,"failed protected extraction can be retried with a fresh correct password");
            ShowSevenZipBrowser();var stale=Named("Открыть содержимое");Password();Require(await LoadFilesAsync(new[]{source}),"new source replaces a pending protected prompt");count=_jobs.Count;stale.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await ReadersAsync();Require(_jobs.Count==count&&_sevenZipBrowsePassword is null,"stale unlock button cannot act on a replacement selection");
            ShowSevenZipCreate();Protect(false);Password();Tagged<PasswordBox>("sevenzip-password-confirm").Password=fixturePassword;Click("Создать 7z-архив");await FinishAsync();var dataArchive=_jobs.Last().Result!.OutputPath;
            Require(_jobs.Last().Options is{SevenZipPasswordProtected:true,SevenZipEncryptHeaders:false},"creation also supports data encryption with visible file names");
            Require(await LoadFilesAsync(new[]{headerArchive,dataArchive})&&_selectionMetadata.All(m=>m.ArchiveLocked),"both encryption modes are classified as requiring credentials");
            count=_jobs.Count;DispatchAction(ImageAction.SevenZipList);Password();Click("Сохранить список с паролем");await FinishAsync();
            Require(_jobs.Count==count+2&&_jobs.Skip(count).All(j=>j.HistoryStatus==HistoryStatus.Succeeded&&j.Result?.Format=="TXT"&&j.Password is null),"one explicit password creates independent list jobs for a protected batch");
            Require(await LoadFilesAsync(new[]{headerArchive}),"protected archive loads for cancellation");await _queueGate.WaitAsync();gateHeld=true;DispatchAction(ImageAction.Extract7z);Password();Click("Распаковать с паролем");var cancelled=_jobs.Last();var cancelledPassword=cancelled.Password;cancelled.Cancellation.Cancel();_queueGate.Release();gateHeld=false;await FinishAsync();
            Require(cancelled.HistoryStatus==HistoryStatus.Cancelled&&cancelled.Result is null&&cancelled.Password is null&&Disposed(cancelledPassword),"queued cancellation disposes an unused password and publishes nothing");
            count=_jobs.Count;await RetryHistoryJobAsync(first);Require(_jobs.Count==count&&Tagged<PasswordBox>("sevenzip-password-confirm").SecurePassword.Length==0,"protected creation retry asks for fresh confirmation");ClosePanel();
            ShowSevenZipBrowser();Password();Click("Открыть содержимое");ShowMoreTools();await ReadersAsync();Require(_sevenZipBrowsePassword is null&&Tagged<TextBox>("tool-search") is not null,"closing during protected inspection cancels stale UI and releases credentials");
            await FlushHistoryAsync();var restored=await new HistoryStore(_historyStore.FilePath).LoadAsync();
            Require(restored.Error is null&&restored.Entries.Any(e=>e.Options is{SevenZipPasswordProtected:true,SevenZipEncryptHeaders:true})&&restored.Entries.Any(e=>e.Options is{Action:ImageAction.Extract7z,SevenZipPasswordProtected:true}),"history reload retains encryption flags for creation and extraction");
            var json=File.ReadAllText(_historyStore.FilePath);
            bool ContainsPassword(JsonElement value)=>value.ValueKind switch{
                JsonValueKind.String=>(value.GetString()??"").Contains(fixturePassword,StringComparison.Ordinal)||(value.GetString()??"").Contains("wrong fixture",StringComparison.Ordinal),
                JsonValueKind.Object=>value.EnumerateObject().Any(p=>ContainsPassword(p.Value)),
                JsonValueKind.Array=>value.EnumerateArray().Any(ContainsPassword),_=>false};
            using(var document=JsonDocument.Parse(json)){
                Require(document.RootElement.GetProperty("version").GetInt32()==2,"protected history uses downgrade-safe version2");
                Require(!ContainsPassword(document.RootElement),"decoded history string values omit Unicode and ASCII fixture credentials");
            }
            var log=Path.Combine(AppContext.BaseDirectory,"diagnostic-logs","diagnostics.log");
            Require(!json.Contains(fixturePassword,StringComparison.Ordinal)&&!json.Contains("wrong fixture",StringComparison.Ordinal)&&(!File.Exists(log)||!File.ReadAllText(log).Contains(fixturePassword,StringComparison.Ordinal)),"history and diagnostics omit all entered fixture passwords");
            var retryStarted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var resumeRetry=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _historyRetryValidationBarrier=async()=>{retryStarted.TrySetResult();await resumeRetry.Task;};
            var delayedRetry=RetryHistoryJobAsync(failed);await retryStarted.Task;ShowMoreTools();resumeRetry.TrySetResult();await delayedRetry;_historyRetryValidationBarrier=null;
            Require(Tagged<TextBox>("tool-search") is not null&&_sevenZipPasswordBoxes.Count==0,"delayed history validation cannot replace a newer panel with a stale password prompt");
            Require(SystemsDigest(source)==sourceHash&&SystemsDigest(headerArchive)==headerHash,"original data and the first protected archive remain unchanged");
            Require(!Directory.GetDirectories(_outputDirectory).Any(p=>Path.GetFileName(p).StartsWith(".orbit-",StringComparison.Ordinal)),"protected success, failure and cancellation clean private staging");
            File.WriteAllText(Path.Combine(destination,"passed.json"),JsonSerializer.Serialize(new{checks=checks.Count,items=checks,run,userSettingsWrites=false,nativeClipboardWrites=false,registryWrites=false,physicalInput=false},new JsonSerializerOptions{WriteIndented=true}));
        }catch(Exception ex){App.Log(ex);File.WriteAllText(Path.Combine(destination,"error.txt"),ex.ToString());}
        finally{if(gateHeld)_queueGate.Release();Quit();}
    }
}
