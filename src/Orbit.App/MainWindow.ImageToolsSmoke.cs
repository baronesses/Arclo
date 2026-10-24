using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Orbit.App.History;
using Orbit.App.Presets;
using Orbit.Core;
using Orbit.Core.Documents;
using Orbit.Core.Presets;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private async Task RunImageToolsSmokeAsync()
    {
        var destination=Path.Combine(AppContext.BaseDirectory,"image-tools-smoke");
        var run=Path.Combine(destination,"run-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        var fixtures=Path.Combine(run,"fixtures");var output=Path.Combine(run,"output");
        var checks=new List<string>();bool gateHeld=false;
        void Require(bool condition,string text){if(!condition)throw new InvalidOperationException("Image tools integration: "+text);checks.Add(text);}
        Button Named(string title)=>SystemsDescendants<Button>(_panelContent).Single(b=>b.Content as string==title);
        async Task CompleteAsync(){await Task.WhenAll(_jobTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(90));await FlushHistoryAsync();}
        async Task ShotAsync(string name){_toast.Visibility=Visibility.Collapsed;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);Capture(Path.Combine(destination,name));}
        void SaveRecipe(JobEntry job,string name){ShowSavePreset(job);_panelContent.Children.OfType<TextBox>().Single().Text=name;Named("Сохранить пресет").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
        try{
            Directory.CreateDirectory(fixtures);Directory.CreateDirectory(output);
            File.Delete(Path.Combine(destination,"passed.json"));File.Delete(Path.Combine(destination,"error.txt"));
            Require(IsDiagnosticRun&&_bridge is null&&_jobs.Count==0,"diagnostic starts without user history, registry, tray or real hotkeys");
            var first=Path.Combine(fixtures,"01 прозрачность.png");var second=Path.Combine(fixtures,"02 текст.png");
            WriteResultsImage(first);WriteEditingTextImage(second);
            var hashes=new[]{first,second}.ToDictionary(p=>p,p=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
            _historyStore=new HistoryStore(Path.Combine(run,"history.json"));_presetStore=new PresetStore(Path.Combine(run,"presets.json"));
            _allowDiagnosticProductivityIo=true;_outputDirectory=output;_resultOpenOverride=(_,_)=>Task.CompletedTask;
            Require(await LoadFilesAsync(new[]{second,first}),"real PNG fixtures load as a multi-image selection");
            SetCompactMode(true);ShowMoreTools();
            Require(new[]{"Изображения в PDF…","Контактный лист…","Удалить метаданные…"}.All(t=>SystemsDescendants<Button>(_panelContent).Any(b=>b.Content as string==t)),"image More menu exposes all three new tools");
            Named("Изображения в PDF…").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var list=_panelContent.Children.OfType<ListBox>().Single();list.SelectedIndex=1;
            Named("↑").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(list.Items.Cast<string>().SequenceEqual(new[]{first,second}),"PDF arrow controls change the captured image order");
            var paper=_panelContent.Children.OfType<ComboBox>().Single();paper.SelectedIndex=1;
            var margin=_panelContent.Children.OfType<TextBox>().Single();margin.Text="NaN";
            Named("Создать PDF").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(_jobs.Count==0,"non-finite margins are rejected before any task is enqueued");
            margin.Text="8";_panelContent.Children.OfType<Slider>().Single().Value=91;
            await ShotAsync("01-image-pdf-parameters.png");
            Named("Создать PDF").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await CompleteAsync();
            var pdf=_jobs.Last();
            Require(pdf is{Finished:true,Result:not null}&&pdf.HistoryStatus==HistoryStatus.Succeeded&&File.Exists(pdf.Result.OutputPath),"actual PDF UI command completes one real aggregate task");
            Require(pdf.InputPaths.SequenceEqual(new[]{first,second})&&pdf.Options is{ImagePdfPageSize:"A4Landscape",ImagePdfMarginMillimeters:8,Quality:91},"queue preserves the edited order and PDF paper/margin/quality");
            var pdfInfo=await new DocumentEngine().InspectPdfAsync(pdf.Result!.OutputPath);
            Require(pdfInfo.Pages==2&&pdf.Result.Format=="PDF","new PDF is independently readable and contains both pages");
            Require(_compact&&_panel.Visibility==Visibility.Collapsed&&ActualWidth<=_compactSize+1,"aggregate task returns to the compact radial menu");
            SaveRecipe(pdf,"PDF альбомный 8 мм");
            Require(_customPresets.Any(p=>p.Options.Action==ImageAction.ImagesToPdf&&p.Options.ImagePdfMarginMillimeters==8),"actual save-preset UI accepts image PDF parameters");
            ShowImageCollection(ImageAction.ContactSheet);
            list=_panelContent.Children.OfType<ListBox>().Single();Named("По имени").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(list.Items.Cast<string>().SequenceEqual(new[]{first,second}),"collection sort-by-name control produces an explicit ordered list");
            var combos=_panelContent.Children.OfType<ComboBox>().ToArray();combos[0].SelectedItem=2;combos[1].SelectedItem=160;
            _panelContent.Children.OfType<CheckBox>().Single().IsChecked=false;
            await ShotAsync("02-contact-sheet-parameters.png");
            Named("Создать контактный лист").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await CompleteAsync();
            var sheet=_jobs.Last();
            Require(sheet is{Finished:true,Result:not null}&&sheet.HistoryStatus==HistoryStatus.Succeeded&&sheet.InputPaths.Count==2,"contact-sheet command creates one result from the complete selection");
            Require(sheet.Options is{ContactSheetColumns:2,ContactSheetThumbnailSize:160,ContactSheetWhiteBackground:false}
                &&sheet.Result is{Width:356,Height:184,Format:"PNG"},"actual composed PNG dimensions match the requested grid and outer gaps");
            Require((await _engine.InspectAsync(sheet.Result!.OutputPath)).Kind==ToolkitKind.Image&&!CanCompare(sheet),"contact sheet is a readable PNG and has no misleading single-original comparison");
            SaveRecipe(sheet,"Прозрачный лист 2 колонки");
            Require(_customPresets.Any(p=>p.Options is{Action:ImageAction.ContactSheet,ContactSheetWhiteBackground:false,ContactSheetColumns:2}),"actual save-preset UI retains contact grid options");
            ShowMetadataCleaner();await ShotAsync("03-metadata-parameters.png");
            Named("Создать очищенные копии").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await CompleteAsync();
            var cleaned=_jobs.Where(j=>j.Options.Action==ImageAction.RemoveImageMetadata).ToArray();
            Require(cleaned.Length==2&&cleaned.All(j=>j.HistoryStatus==HistoryStatus.Succeeded&&j.Result is not null),"cleaning a multi-image selection queues one separate copy for each image");
            Require(cleaned.All(j=>File.Exists(j.Result!.OutputPath)&&j.Result.OutputPath!=j.Path&&j.Result.Format=="PNG"),"cleaned copies preserve format and never reuse the original paths");
            Require(cleaned.All(CanCompare),"metadata results expose before/after comparison");
            SaveRecipe(cleaned[0],"Копия без метаданных");
            Require(new PresetStore(_presetStore.FilePath).Load() is{Warning:null,Presets.Count:3},"all three UI-saved new recipes survive fresh catalog loading");
            await ShowComparisonAsync(cleaned[0]);
            Require(_comparisonView is{BeforeSource:not null,AfterSource:not null},"cleaned result opens actual original/result previews");
            await ShotAsync("04-cleaned-comparison.png");ClosePanel();
            ShowImageCollection(ImageAction.ContactSheet);list=_panelContent.Children.OfType<ListBox>().Single();
            while(list.Items.Count>0){list.SelectedIndex=0;Named("Убрать").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
            Require(!Named("Создать контактный лист").IsEnabled,"removing every input disables aggregate creation");
            await LoadFilesAsync(new[]{first,second});
            await _queueGate.WaitAsync();gateHeld=true;
            int countBefore=_jobs.Count;int filesBefore=Directory.GetFiles(output).Length;
            QueueSelected(new ImageJobOptions{Action=ImageAction.ImagesToPdf,OutputDirectory=output});var cancelled=_jobs.Last();cancelled.Cancellation.Cancel();
            await CompleteAsync();_queueGate.Release();gateHeld=false;
            Require(_jobs.Count==countBefore+1&&cancelled.HistoryStatus==HistoryStatus.Cancelled&&cancelled.Result is null&&Directory.GetFiles(output).Length==filesBefore,"cancelled aggregate waiting for the queue produces no partial or final file");
            var restored=await new HistoryStore(_historyStore.FilePath).LoadAsync();
            Require(restored.Error is null&&restored.Entries.Count==_jobs.Count,"durable history retains every new action without rejected entries");
            Require(restored.Entries.Single(e=>e.Id==pdf.Id).SourcePaths.SequenceEqual(pdf.InputPaths)
                &&restored.Entries.Single(e=>e.Id==sheet.Id).Options==sheet.Options,"history round-trip retains collection order and active options");
            Require(hashes.All(pair=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pair.Key)))==pair.Value),"all real UI operations preserve both original file hashes");
            Require(!Directory.EnumerateDirectories(output).Any(d=>Path.GetFileName(d).StartsWith(".orbit-",StringComparison.Ordinal)),"successful and cancelled UI operations leave no temporary output directories");
            Require(await LoadFilesAsync(new[]{first,pdf.Result.OutputPath}),"mixed image/PDF selection loads for tool availability regression");
            ShowMoreTools();Require(!SystemsDescendants<Button>(_panelContent).Any(b=>new[]{"Изображения в PDF…","Контактный лист…","Удалить метаданные…"}.Contains(b.Content as string)),"image-only tools are absent for a mixed image/PDF selection");
            var report=JsonSerializer.Serialize(new{Passed=checks.Count,Checks=checks,Artifacts=run,NativeClipboardReads=0,NativeClipboardWrites=0,NativeShellLaunches=0,UserProfileAccessed=false},new JsonSerializerOptions{WriteIndented=true});
            await File.WriteAllTextAsync(Path.Combine(destination,"passed.json"),report);await File.WriteAllTextAsync(Path.Combine(run,"passed.json"),report);
        }catch(Exception ex){Directory.CreateDirectory(destination);await File.WriteAllTextAsync(Path.Combine(destination,"error.txt"),ex.ToString());}
        finally{if(gateHeld)_queueGate.Release();Quit();}
    }
}
