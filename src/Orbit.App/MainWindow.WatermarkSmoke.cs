using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Orbit.App.History;
using Orbit.App.Presets;
using Orbit.Core;
using Orbit.Core.Presets;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private async Task RunWatermarkSmokeAsync()
    {
        var destination=Path.Combine(AppContext.BaseDirectory,"watermark-smoke");var run=Path.Combine(destination,"run-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        var checks=new List<string>();bool gateHeld=false;const string literal="© Баро 100% %[fx:1+2] @file \\";
        void Require(bool value,string text){if(!value)throw new InvalidOperationException("Watermark integration: "+text);checks.Add(text);}
        T Tagged<T>(string tag)where T:FrameworkElement=>SystemsDescendants<T>(_panelContent).Single(c=>c.Tag as string==tag);
        Button Named(string name)=>SystemsDescendants<Button>(_panelContent).Single(b=>b.Content as string==name);
        void Click(string name)=>Named(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Choose(string tag,string id){var box=Tagged<ComboBox>(tag);box.SelectedItem=box.Items.Cast<WatermarkChoice>().Single(c=>c.Id==id);}
        async Task ReadersAsync()=>await Task.WhenAll(_resultToolTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(120));
        async Task FinishAsync(){await Task.WhenAll(_jobTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(180));await FlushHistoryAsync();}
        async Task ShotAsync(string name){_toast.Visibility=Visibility.Collapsed;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);Capture(Path.Combine(destination,name));}
        byte[] Pixels(string path){var image=new BitmapImage();using(var input=File.OpenRead(path)){image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.StreamSource=input;image.EndInit();}var converted=new System.Windows.Media.Imaging.FormatConvertedBitmap(image,System.Windows.Media.PixelFormats.Bgra32,null,0);var bytes=new byte[converted.PixelWidth*converted.PixelHeight*4];converted.CopyPixels(bytes,converted.PixelWidth*4,0);return bytes;}
        bool HasLogoReference(JsonElement value,string path,string hash)=>value.ValueKind switch{
            JsonValueKind.String=>(value.GetString()??"").Contains(Path.GetFileName(path),StringComparison.Ordinal)||(value.GetString()??"").Contains(hash,StringComparison.OrdinalIgnoreCase),
            JsonValueKind.Object=>value.EnumerateObject().Any(p=>HasLogoReference(p.Value,path,hash)),JsonValueKind.Array=>value.EnumerateArray().Any(v=>HasLogoReference(v,path,hash)),_=>false};
        try{
            Directory.CreateDirectory(run);File.Delete(Path.Combine(destination,"passed.json"));File.Delete(Path.Combine(destination,"error.txt"));
            Require(IsDiagnosticRun&&_bridge is null&&!ProductivityIoAllowed&&!PersonalizationIoAllowed&&Path.GetTempPath().StartsWith(AppContext.BaseDirectory,StringComparison.OrdinalIgnoreCase),"diagnostic owns temp and avoids user stores, real hotkeys, clipboard, shell and registry writes");
            var first=Path.Combine(run,"исходник [1].png");var second=Path.Combine(run,"другое фото.png");var logo=Path.Combine(run,"private-logo-selector-19371.png");WriteResultsImage(first);WriteResultsImage(second);WriteResultsImage(logo);
            var originals=new[]{first,second,logo}.ToDictionary(p=>p,SystemsDigest);var logoBackup=Path.Combine(run,"logo-backup.png");File.Copy(logo,logoBackup);
            _historyStore=new HistoryStore(Path.Combine(run,"history.json"));_presetStore=new PresetStore(Path.Combine(run,"presets.json"));_allowDiagnosticProductivityIo=true;
            _outputDirectory=Path.Combine(run,"output");Directory.CreateDirectory(_outputDirectory);_watermarkLogoPickerOverride=()=>logo;SetCompactMode(true);
            bool loaded=await LoadFilesAsync(new[]{first});Require(loaded,$"source image loads for watermark tools; status={_status.Text}; toast={_toastText.Text}; version={_loadVersion}; cancelled={_loadCancellation?.IsCancellationRequested}; window={WindowState}");ShowMoreTools();Tagged<TextBox>("tool-search").Text="watermark";Require(SystemsDescendants<Button>(_panelContent).Any(b=>b.Content as string=="Водяной знак…"),"English alias finds the real watermark editor");Click("Водяной знак…");
            Require(!Tagged<Button>("watermark-save").IsEnabled&&!Tagged<Button>("watermark-preview-button").IsEnabled,"empty text cannot preview or save");
            Tagged<TextBox>("watermark-text").Text=literal;Tagged<TextBox>("watermark-color").Text="#XYZ123";Require(!Tagged<Button>("watermark-save").IsEnabled,"invalid color is rejected before native processing");
            Tagged<TextBox>("watermark-color").Text="#000000";Choose("watermark-position","Center");Tagged<ComboBox>("watermark-size").SelectedItem=10;Tagged<ComboBox>("watermark-opacity").SelectedItem=75;
            Require(Tagged<Button>("watermark-save").IsEnabled&&Tagged<Button>("watermark-preview-button").IsEnabled,"literal Cyrillic text and valid layout enable operations");
            Choose("watermark-mode","Logo");Require(!Tagged<Button>("watermark-save").IsEnabled,"logo mode requires an explicitly checked asset");Tagged<ComboBox>("watermark-size").SelectedItem=33;Choose("watermark-mode","Text");Require((int)Tagged<ComboBox>("watermark-size").SelectedItem==10,"switching modes retains independent text and logo sizes");Choose("watermark-mode","Logo");Require((int)Tagged<ComboBox>("watermark-size").SelectedItem==33,"returning to logo mode restores its edited width");Choose("watermark-mode","Text");
            await ShotAsync("01-text-editor.png");Click("Предпросмотр");await ReadersAsync();Require(Tagged<System.Windows.Controls.Image>("watermark-preview").Source is BitmapSource bitmap&&bitmap.PixelWidth==280&&bitmap.PixelHeight==180,"preview renders the actual watermark with original small-image dimensions");
            Require(_jobs.Count==0&&Directory.GetFiles(_outputDirectory).Length==0,"preview publishes no image or history job");await ShotAsync("02-text-preview.png");Click("Изменить параметры");
            Require(Tagged<TextBox>("watermark-text").Text==literal&&(int)Tagged<ComboBox>("watermark-size").SelectedItem==10,"Back restores literal text and layout");Click("Сохранить изображения");await FinishAsync();var textJob=_jobs.Last();
            Require(textJob.HistoryStatus==HistoryStatus.Succeeded&&textJob.Options is{Action:ImageAction.ImageWatermark,WatermarkMode:"Text",WatermarkFontSizePercent:10,WatermarkOpacityPercent:75,WatermarkPosition:"Center"}&&textJob.Result is{Format:"PNG",Width:280,Height:180},"editor queues a real text watermark job with canonical options and source format");
            Require(!Pixels(first).SequenceEqual(Pixels(textJob.Result!.OutputPath))&&CanCompare(textJob),"result pixels contain a mark and support standard before/after comparison");
            ShowSavePreset(textJob);_panelContent.Children.OfType<TextBox>().Single().Text="Подпись Баро";Click("Сохранить пресет");var preset=_customPresets.Single();Require(preset.Options.WatermarkText==literal&&preset.Options.OutputDirectory is null&&preset.Options.WatermarkLogoPath is null,"text preset keeps literal settings without output path or logo reference");
            Require(await LoadFilesAsync(new[]{first,second}),"two images load for a shared text recipe");int count=_jobs.Count;ApplyCurrentRecipe(preset);await FinishAsync();Require(_jobs.Count==count+2&&_jobs.Skip(count).All(j=>j.HistoryStatus==HistoryStatus.Succeeded&&j.Options.WatermarkText==literal)&&_jobs.Skip(count).Select(j=>j.Result!.OutputPath).Distinct().Count()==2,"text recipe creates one independent new result per selected file");
            Require(Pixels(textJob.Result.OutputPath).SequenceEqual(Pixels(_jobs.Skip(count).First().Result!.OutputPath)),"text recipe repeats identical decoded pixels without replacing the first output");
            Require(_repeatRecipe is not null&&_repeatRecipe.Options.Action==ImageAction.ImageWatermark,"successful text watermark becomes a reusable session recipe");
            Require(await LoadFilesAsync(new[]{first}),"source reloads for preview freshness checks");ShowWatermarkEditor(preset.Options);Click("Предпросмотр");Tagged<TextBox>("watermark-text").Text="Изменённая подпись";await ReadersAsync();Require(Tagged<TextBox>("watermark-text").Text=="Изменённая подпись"&&!SystemsDescendants<System.Windows.Controls.Image>(_panelContent).Any(i=>i.Tag as string=="watermark-preview"),"editing parameters cancels a stale preview before it replaces the editor");
            Click("Предпросмотр");ShowMoreTools();await ReadersAsync();Require(Tagged<TextBox>("tool-search") is not null,"closing the editor during preview cannot replace another panel");
            ShowWatermarkEditor(preset.Options);Click("Предпросмотр");ShowWatermarkEditor(preset.Options);Require(Tagged<Button>("watermark-preview-button").IsEnabled,"a replacement editor can preview while the old task cancels");await ReadersAsync();Require(Tagged<Button>("watermark-preview-button").IsEnabled,"old preview cleanup cannot disable a replacement editor");
            ShowWatermarkEditor(preset.Options);Tagged<TextBox>("watermark-text").Text="\U0010FFFF";Click("Предпросмотр");await ReadersAsync();Require(Tagged<TextBlock>("watermark-status").Text.StartsWith("Не удалось создать предпросмотр:",StringComparison.Ordinal)&&!SystemsDescendants<System.Windows.Controls.Image>(_panelContent).Any(i=>i.Tag as string=="watermark-preview"),"unsupported font glyph yields a persistent visible error instead of a silent replacement box");
            ShowWatermarkEditor(preset.Options);Choose("watermark-mode","Logo");Click("Выбрать логотип…");await ReadersAsync();Require(Tagged<Button>("watermark-save").IsEnabled&&Tagged<TextBlock>("watermark-logo-caption").Text==Path.GetFileName(logo),"logo picker validates the owned image and captures a fingerprint without a real file dialog");
            Choose("watermark-position","BottomRight");Tagged<ComboBox>("watermark-size").SelectedItem=33;Tagged<ComboBox>("watermark-opacity").SelectedItem=75;await ShotAsync("03-logo-editor.png");Click("Предпросмотр");await ReadersAsync();Require(Tagged<System.Windows.Controls.Image>("watermark-preview").Source is BitmapSource,"logo mode renders a real composed preview");
            var staleSave=Named("Сохранить изображения");count=_jobs.Count;Require(await LoadFilesAsync(new[]{second}),"different selection replaces a logo preview");staleSave.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Require(_jobs.Count==count,"detached preview save cannot watermark replacement sources");
            ShowWatermarkEditor(new ImageJobOptions{Action=ImageAction.ImageWatermark,WatermarkMode="Logo"});Click("Выбрать логотип…");await ReadersAsync();Click("Сохранить изображения");await FinishAsync();var logoJob=_jobs.Last();
            Require(logoJob.HistoryStatus==HistoryStatus.Succeeded&&logoJob.Options.WatermarkMode=="Logo"&&string.Equals(logoJob.Options.WatermarkLogoSha256,originals[logo],StringComparison.OrdinalIgnoreCase)&&logoJob.Result?.Format=="PNG","logo mode applies the exact checked asset through the normal queue");
            Require(!Pixels(second).SequenceEqual(Pixels(logoJob.Result!.OutputPath))&&!PresetCatalog.CanSave(logoJob.Options)&&!RepeatRecipe.TryCapture(logoJob.Options,true,out _),"logo result changes pixels while its source-bound selector cannot enter presets or quick repeat");
            ShowWatermarkEditor(new ImageJobOptions{Action=ImageAction.ImageWatermark,WatermarkMode="Logo"});Click("Выбрать логотип…");await ReadersAsync();File.WriteAllText(logo,"changed fixture");Click("Сохранить изображения");await FinishAsync();var changed=_jobs.Last();Require(changed.HistoryStatus==HistoryStatus.Failed&&changed.Result is null,"a changed logo is rejected without publishing output");File.Copy(logoBackup,logo,true);
            await FlushHistoryAsync();var history=await new HistoryStore(_historyStore.FilePath).LoadAsync();Require(history.Error is null&&history.Entries.Any(e=>e.Options.WatermarkText==literal)&&history.Entries.Any(e=>e.Options is{Action:ImageAction.ImageWatermark,WatermarkMode:"Logo"}),"history restores literal text and logo layout modes");
            Require(history.Entries.Where(e=>e.Options is{Action:ImageAction.ImageWatermark,WatermarkMode:"Logo"}).All(e=>e.Options.WatermarkLogoPath is null&&e.Options.WatermarkLogoSha256 is null),"history reload never restores a logo path or fingerprint");using(var json=JsonDocument.Parse(File.ReadAllText(_historyStore.FilePath)))Require(!HasLogoReference(json.RootElement,logo,originals[logo]),"decoded JSON contains no logo selector or fingerprint even for failed jobs");
            var retry=new JobEntry(second,logoJob.Options with{OutputDirectory=Path.Combine(run,"removed-output")}){InputPaths=new[]{second},Finished=true};count=_jobs.Count;await RetryHistoryJobAsync(retry);Require(_jobs.Count==count&&!Tagged<Button>("watermark-save").IsEnabled&&Tagged<TextBlock>("watermark-logo-caption").Text!=Path.GetFileName(logo),"logo retry ignores an old destination and requires a fresh asset before queueing");
            Click("Выбрать логотип…");ShowMoreTools();await ReadersAsync();Require(Tagged<TextBox>("tool-search") is not null&&_watermarkLogoCancellation is null,"closing during logo inspection cancels the task without overwriting a newer panel");
            ShowWatermarkEditor(new ImageJobOptions{Action=ImageAction.ImageWatermark,WatermarkMode="Logo"});Click("Выбрать логотип…");await ReadersAsync();await _queueGate.WaitAsync();gateHeld=true;Click("Сохранить изображения");var cancelled=_jobs.Last();cancelled.Cancellation.Cancel();_queueGate.Release();gateHeld=false;await FinishAsync();Require(cancelled.HistoryStatus==HistoryStatus.Cancelled&&cancelled.Result is null,"queued logo watermark cancels without a published result");
            Require(originals.All(p=>SystemsDigest(p.Key)==p.Value),"original photos and restored logo retain exact SHA256");Require(!Directory.GetDirectories(_outputDirectory).Any(p=>Path.GetFileName(p).StartsWith(".orbit-",StringComparison.Ordinal)),"success, preview cancellation, stale logo and queue cancellation clean private staging");
            File.WriteAllText(Path.Combine(destination,"passed.json"),JsonSerializer.Serialize(new{checks=checks.Count,items=checks,run,userSettingsWrites=false,nativeClipboardWrites=false,registryWrites=false,physicalInput=false},new JsonSerializerOptions{WriteIndented=true}));
        }catch(Exception ex){App.Log(ex);File.WriteAllText(Path.Combine(destination,"error.txt"),ex.ToString());}
        finally{if(gateHeld)_queueGate.Release();Quit();}
    }
}
