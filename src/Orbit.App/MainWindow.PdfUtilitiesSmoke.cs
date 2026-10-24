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
using Orbit.Core.Presets;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private async Task RunPdfUtilitiesSmokeAsync()
    {
        var destination=Path.Combine(AppContext.BaseDirectory,"pdf-utilities-smoke");
        var run=Path.Combine(destination,"run-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        var checks=new List<string>();bool gateHeld=false;
        void Require(bool value,string description){if(!value)throw new InvalidOperationException("PDF utilities integration: "+description);checks.Add(description);}
        Button Named(string label)=>SystemsDescendants<Button>(_panelContent).Single(b=>b.Content as string==label);
        void Click(string label)=>Named(label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        async Task FinishAsync(){await Task.WhenAll(_jobTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(120));await FlushHistoryAsync();}
        async Task ShotAsync(string name){_toast.Visibility=Visibility.Collapsed;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);Capture(Path.Combine(destination,name));}
        try{
            Directory.CreateDirectory(run);File.Delete(Path.Combine(destination,"passed.json"));File.Delete(Path.Combine(destination,"error.txt"));
            Require(IsDiagnosticRun&&_bridge is null&&_jobs.Count==0&&!ProductivityIoAllowed&&!ChainIoAllowed,"diagnostic avoids native hotkeys, shell integration and all user stores");
            Require(_engine.PdfTextAvailable&&_engine.PdfImagesAvailable&&_engine.PdfOptimizeAvailable,"all PDF components are available from the portable bundle");
            var first=Path.Combine(run,"первый документ.pdf");var second=Path.Combine(run,"второй документ.pdf");
            WriteSystemsPdf(first,new[]{("OrbitFirstPage",360,240),("OrbitSecondPage",480,320),("OrbitThirdPage",200,500)});
            WriteSystemsPdf(second,new[]{("OtherDocument",500,250)});
            var png1=Path.Combine(run,"первое изображение.png");var png2=Path.Combine(run,"второе изображение.png");WriteResultsImage(png1);WriteEditingTextImage(png2);
            var scan=await new ImagePdfEngine().CreateAsync(new[]{png1,png2},new ImagePdfOptions{OutputDirectory=run,PageSize=ImagePdfPageSize.FitImage,MarginMillimeters=0,JpegQuality=95});
            var originals=new[]{first,second,png1,png2,scan.OutputPath}.ToDictionary(p=>p,SystemsDigest);
            _historyStore=new HistoryStore(Path.Combine(run,"history.json"));_presetStore=new PresetStore(Path.Combine(run,"presets.json"));
            _allowDiagnosticProductivityIo=true;_outputDirectory=Path.Combine(run,"output");Directory.CreateDirectory(_outputDirectory);
            Require(await LoadFilesAsync(new[]{first,second}),"two real text PDFs load through normal context inspection");SetCompactMode(true);ShowMoreTools();
            Require(SystemsDescendants<Button>(_panelContent).Any(b=>b.Content as string=="Текст, изображения и оптимизация PDF…"),"More menu exposes a compact entry for the PDF utility group");
            Click("Текст, изображения и оптимизация PDF…");await ShotAsync("01-pdf-tools.png");
            Require(new[]{"Извлечь текст PDF…","Извлечь изображения PDF…","Оптимизировать PDF…"}.All(t=>Named(t).IsEnabled),"PDF utility group offers all three working tools");
            Click("Извлечь текст PDF…");_panelContent.Children.OfType<CheckBox>().Single().IsChecked=false;await ShotAsync("02-text-export.png");Click("Сохранить TXT");await FinishAsync();
            var textJobs=_jobs.Where(j=>j.Options.Action==ImageAction.PdfText).ToArray();
            Require(textJobs.Length==2&&textJobs.All(j=>j.HistoryStatus==HistoryStatus.Succeeded&&j.Result?.Format=="TXT"&&!j.Options.PdfTextPreserveLayout),"text card creates one successful real TXT per PDF with the selected layout option");
            var extracted=await File.ReadAllTextAsync(textJobs[0].Result!.OutputPath);
            Require(extracted.Contains("OrbitFirstPage")&&extracted.IndexOf("OrbitFirstPage",StringComparison.Ordinal)<extracted.IndexOf("OrbitSecondPage",StringComparison.Ordinal)&&extracted.IndexOf("OrbitSecondPage",StringComparison.Ordinal)<extracted.IndexOf("OrbitThirdPage",StringComparison.Ordinal),"extracted text contains the original strings in page order");
            ShowSavePreset(textJobs[0]);_panelContent.Children.OfType<TextBox>().Single().Text="Текст без колонок";Click("Сохранить пресет");
            Require(_customPresets.Single().Options is{Action:ImageAction.PdfText,PdfTextPreserveLayout:false,OutputDirectory:null},"text export settings save as a source-independent reusable preset");
            ShowPdfUtility(ImageAction.PdfOptimize);_panelContent.Children.OfType<CheckBox>().Single().IsChecked=true;await ShotAsync("03-optimize.png");Click("Создать оптимизированный PDF");await FinishAsync();
            var optimized=_jobs.Where(j=>j.Options.Action==ImageAction.PdfOptimize).ToArray();
            Require(optimized.Length==2&&optimized.All(j=>j.HistoryStatus==HistoryStatus.Succeeded&&j.Result?.Format=="PDF"&&j.Options.PdfOptimizeLinearize),"optimization card produces one real linearized PDF per source");
            Require((await ReadSystemsPdfOrderAsync(optimized[0].Result!.OutputPath)).SequenceEqual(new[]{"OrbitFirstPage","OrbitSecondPage","OrbitThirdPage"}),"optimized PDF retains text, vector content and original page order");
            Require(optimized.All(j=>j.Result!.Bytes==new FileInfo(j.Result.OutputPath).Length&&j.Result.SourceBytes==new FileInfo(j.Path).Length),"result size accounting reports actual bytes rather than an estimated saving");
            ShowSavePreset(optimized[0]);_panelContent.Children.OfType<TextBox>().Single().Text="PDF для загрузки";Click("Сохранить пресет");
            Require(_customPresets.Single(p=>p.Name=="PDF для загрузки").Options is{Action:ImageAction.PdfOptimize,PdfOptimizeLinearize:true,OutputDirectory:null},"optimization and linearization settings persist in a reusable preset");
            ShowPdfUtility(ImageAction.PdfText);var stale=Named("Сохранить TXT");int before=_jobs.Count;
            Require(await LoadFilesAsync(new[]{scan.OutputPath}),"an image-only PDF loads independently from text documents");stale.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(_jobs.Count==before,"an old card cannot queue against a changed selection");
            ShowPdfUtility(ImageAction.PdfEmbeddedImages);await ShotAsync("04-embedded-images.png");Click("Сохранить изображения");await FinishAsync();var imageJob=_jobs.Last();
            Require(imageJob.HistoryStatus==HistoryStatus.Succeeded&&imageJob.Result is{Format:"FOLDER"}&&Directory.Exists(imageJob.Result.OutputPath),"embedded image export creates a complete real result folder");
            var images=Directory.GetFiles(imageJob.Result!.OutputPath,"*.png");Require(images.Length==2,"the two image XObjects are exported as PNG files rather than complete rendered pages");
            var actual=await Task.WhenAll(images.Select(p=>new ImageMagickEngine().InspectAsync(p)));var expected=await Task.WhenAll(new[]{png1,png2}.Select(p=>new ImageMagickEngine().InspectAsync(p)));
            Require(actual.Select(m=>(m.Width,m.Height)).Order().SequenceEqual(expected.Select(m=>(m.Width,m.Height)).Order()),"extracted image dimensions match the original embedded pixel resources");
            Require(imageJob.Result.Bytes==Directory.GetFiles(imageJob.Result.OutputPath).Sum(p=>new FileInfo(p).Length),"folder result reports the actual published byte count");
            Require(!CanCompare(imageJob)&&_repeatRecipe?.Options.Action==ImageAction.PdfEmbeddedImages,"embedded image folder supports repeat without offering static-file image comparison");
            ShowSavePreset(imageJob);_panelContent.Children.OfType<TextBox>().Single().Text="Картинки из PDF";Click("Сохранить пресет");
            Require(_customPresets.Count==3&&_customPresets.All(p=>PresetCatalog.CanApply(p,_selectionMetadata)),"all new recipes reload and apply to the current PDF selection");
            ShowPdfUtility(ImageAction.PdfText);Click("Сохранить TXT");await FinishAsync();var noText=_jobs.Last();
            Require(noText.HistoryStatus==HistoryStatus.Failed&&noText.Result==null&&noText.Detail.Contains("OCR",StringComparison.OrdinalIgnoreCase),"image-only PDF gives an OCR explanation without silently recognizing text or publishing an empty TXT");
            Require(await LoadFilesAsync(new[]{first}),"text PDF loads for the no-embedded-image case");ShowPdfUtility(ImageAction.PdfEmbeddedImages);Click("Сохранить изображения");await FinishAsync();
            Require(_jobs.Last().HistoryStatus==HistoryStatus.Failed&&_jobs.Last().Result==null,"text-only PDF gives a clear no-images failure without publishing an empty folder");
            var loaded=await new HistoryStore(_historyStore.FilePath).LoadAsync();
            Require(loaded.Error==null&&loaded.Entries.Count==_jobs.Count&&loaded.Entries.Count(e=>e.Status==HistoryStatus.Succeeded)==5,"history round trip retains real TXT, optimized PDF and image-folder results plus failures");
            Require(loaded.Entries.Any(e=>e.Options.Action==ImageAction.PdfText&&!e.Options.PdfTextPreserveLayout)&&loaded.Entries.Any(e=>e.Options.Action==ImageAction.PdfOptimize&&e.Options.PdfOptimizeLinearize),"history preserves both active PDF utility options");
            await _queueGate.WaitAsync();gateHeld=true;ShowPdfUtility(ImageAction.PdfOptimize);Click("Создать оптимизированный PDF");var cancelled=_jobs.Last();cancelled.Cancellation.Cancel();_queueGate.Release();gateHeld=false;await FinishAsync();
            Require(cancelled.HistoryStatus==HistoryStatus.Cancelled&&cancelled.Result==null,"queued PDF optimization cancels without publishing a result");
            Require(originals.All(p=>SystemsDigest(p.Key)==p.Value),"all PDF commands preserve every original PDF and image hash");
            Require(!Directory.GetDirectories(_outputDirectory).Any(p=>Path.GetFileName(p).StartsWith(".orbit-",StringComparison.Ordinal)),"success, rejection and queued cancellation leave no private staging directory");
            Require(await LoadFilesAsync(new[]{png1,first}),"mixed image/PDF selection loads normally");ShowMoreTools();
            Require(!SystemsDescendants<Button>(_panelContent).Any(b=>b.Content as string=="Текст, изображения и оптимизация PDF…"),"PDF-only utility group stays absent for mixed selections");
            File.WriteAllText(Path.Combine(destination,"passed.json"),JsonSerializer.Serialize(new{checks=checks.Count,items=checks,run,userProfileIo=false,nativeClipboardWrites=false,registryWrites=false,physicalInput=false},new JsonSerializerOptions{WriteIndented=true}));
        }catch(Exception ex){App.Log(ex);File.WriteAllText(Path.Combine(destination,"error.txt"),ex.ToString());}
        finally{if(gateHeld)_queueGate.Release();Quit();}
    }
}
