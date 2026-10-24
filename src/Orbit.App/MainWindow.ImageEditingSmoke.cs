using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Orbit.App.Controls;
using Orbit.App.History;
using Orbit.App.Presets;
using Orbit.Core;
using Orbit.Core.Chains;
using Orbit.Core.Presets;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private async Task RunImageEditingSmokeAsync()
    {
        var destination=Path.Combine(AppContext.BaseDirectory,"image-editing-smoke");var run=Path.Combine(destination,"run-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        var checks=new List<string>();bool gateHeld=false;
        void Require(bool value,string text){if(!value)throw new InvalidOperationException("Image editing integration: "+text);checks.Add(text);}
        Button Named(string text)=>SystemsDescendants<Button>(_panelContent).Single(b=>b.Content as string==text);
        void Click(string text)=>Named(text).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        T Tagged<T>(string tag)where T:FrameworkElement=>SystemsDescendants<T>(_panelContent).Single(c=>c.Tag as string==tag);
        async Task FinishAsync(){await Task.WhenAll(_jobTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(120));await FlushHistoryAsync();}
        async Task ShotAsync(string name){_toast.Visibility=Visibility.Collapsed;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);Capture(Path.Combine(destination,name));}
        void SaveRecipe(JobEntry job,string name){ShowSavePreset(job);_panelContent.Children.OfType<TextBox>().Single().Text=name;Click("Сохранить пресет");}
        try{
            Directory.CreateDirectory(run);File.Delete(Path.Combine(destination,"passed.json"));File.Delete(Path.Combine(destination,"error.txt"));
            Require(IsDiagnosticRun&&_bridge is null&&_jobs.Count==0&&!ProductivityIoAllowed&&!ChainIoAllowed,"diagnostic avoids user stores, hotkeys, clipboard and shell integration");
            var first=Path.Combine(run,"прозрачность 400.png");var second=Path.Combine(run,"прозрачность 200.png");WriteImageEditorFixture(first,400,300);WriteImageEditorFixture(second,200,100);
            var hashes=new[]{first,second}.ToDictionary(p=>p,SystemsDigest);
            _historyStore=new HistoryStore(Path.Combine(run,"history.json"));_presetStore=new PresetStore(Path.Combine(run,"presets.json"));_allowDiagnosticProductivityIo=true;_outputDirectory=Path.Combine(run,"output");Directory.CreateDirectory(_outputDirectory);
            Require(await LoadFilesAsync(new[]{first,second}),"two alpha PNG sources load with different original sizes");SetCompactMode(true);ShowAction(ImageAction.Resize);
            Tagged<ComboBox>("resize-mode").SelectedIndex=1;Tagged<TextBox>("resize-percent").Text="NaN";
            Require(!Named("Изменить размер").IsEnabled,"nonfinite percentage is rejected before queueing");
            Tagged<TextBox>("resize-percent").Text="50";Tagged<ComboBox>("resize-filter").SelectedIndex=3;await ShotAsync("01-percent-resize.png");Click("Изменить размер");await FinishAsync();
            var resized=_jobs.ToArray();Require(resized.Length==2&&resized.All(j=>j.HistoryStatus==HistoryStatus.Succeeded&&j.Options is{ResizePercent:50,ResizeFilter:"Point",Width:null,Height:null}),"percentage card queues actual per-image Point resizing without conflicting pixel dimensions");
            Require(resized[0].Result is{Width:200,Height:150}&&resized[1].Result is{Width:100,Height:50},"50 percent is calculated independently from each real source size");
            var alpha=await new ImageMagickEngine().CreatePreviewAsync(resized[0].Result!.OutputPath,640);Require(ReadEditorPixel(alpha,150,75).A==0,"percentage resizing preserves transparent PNG pixels");
            SaveRecipe(resized[0],"Половина Point");Require(_customPresets.Single().Options is{ResizePercent:50,ResizeFilter:"Point",Width:null,Height:null,OutputDirectory:null},"saved resize preset retains percentage/filter and drops the old output path");
            ShowImageResize();Tagged<ComboBox>("resize-mode").SelectedIndex=1;Tagged<TextBox>("resize-percent").Text="200";
            Require(SystemsDescendants<TextBlock>(_panelContent).Any(t=>t.Text.Contains("400 × 300")),"no-upscale preview caps a requested 200 percent at the original size");
            ShowImageResize();Tagged<TextBox>("resize-width").Text="120";Tagged<TextBox>("resize-height").Text="80";var aspect=_panelContent.Children.OfType<CheckBox>().First();aspect.IsChecked=false;aspect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await ShotAsync("02-pixel-resize.png");Click("Изменить размер");await FinishAsync();
            Require(_jobs.TakeLast(2).All(j=>j.HistoryStatus==HistoryStatus.Succeeded&&j.Options.ResizePercent==null&&j.Result is{Width:120,Height:80}),"pixel mode still produces the exact independent width/height when proportions are unlocked");
            Require(await LoadFilesAsync(new[]{first}),"single alpha source loads for JPEG matte and crop");ShowMoreTools();Click("JPEG с цветом фона…");
            Tagged<TextBox>("jpeg-background").Text="@file";Require(!Named("Создать JPEG").IsEnabled,"non-hex matte input cannot queue a conversion");
            Tagged<TextBox>("jpeg-background").Text="#102030";_panelContent.Children.OfType<Slider>().Single().Value=100;await ShotAsync("03-jpeg-background.png");Click("Создать JPEG");await FinishAsync();var jpeg=_jobs.Last();
            Require(jpeg.HistoryStatus==HistoryStatus.Succeeded&&jpeg.Result?.Format=="JPEG"&&jpeg.Options.JpegBackgroundColor=="#102030","JPEG card sends its selected RGB background to a real encoder");
            var matte=ReadEditorPixel(await new ImageMagickEngine().CreatePreviewAsync(jpeg.Result!.OutputPath,640),300,150);
            Require(Math.Abs(matte.R-16)<=5&&Math.Abs(matte.G-32)<=5&&Math.Abs(matte.B-48)<=5&&matte.A==255,"independent decoded JPEG pixels contain the chosen matte rather than white");
            SaveRecipe(jpeg,"JPEG с фоном");Require(_customPresets.Single(p=>p.Name=="JPEG с фоном").Options.JpegBackgroundColor=="#102030","JPEG background survives reusable preset saving");
            var chain=ImageChainPolicy.Create("Половина и JPEG",new[]{_customPresets.Single(p=>p.Name=="Половина Point").Options,_customPresets.Single(p=>p.Name=="JPEG с фоном").Options});RunImageChain(chain);await FinishAsync();var chained=_jobs.Last();
            Require(chained.HistoryStatus==HistoryStatus.Succeeded&&chained.Result is{Format:"JPEG",Width:200,Height:150},"image chain executes percentage resize before the colored JPEG conversion");
            var chainPixel=ReadEditorPixel(await new ImageMagickEngine().CreatePreviewAsync(chained.Result!.OutputPath,640),150,75);Require(Math.Abs(chainPixel.R-16)<=5&&Math.Abs(chainPixel.G-32)<=5&&Math.Abs(chainPixel.B-48)<=5,"chain's final JPEG retains the configured matte");
            ShowAction(ImageAction.Crop);var crop=SystemsDescendants<CropPreview>(_panelContent).Single();var ratio=Tagged<ComboBox>("crop-ratio");ratio.SelectedIndex=1;
            Require(crop.Selection.Width==crop.Selection.Height&&crop.Selection.Width==300,"1:1 preset creates a centered maximal square inside the current full selection");
            for(int i=0;i<4;i++)Tagged<TextBox>("crop-"+i).Text=new[]{"10","20","100","80"}[i];Click("Применить координаты");
            Require(!Named("Сохранить обрезку").IsEnabled&&crop.Selection.Width==300,"a numeric rectangle that violates the locked ratio is rejected without changing the old crop");
            ratio.SelectedIndex=0;for(int i=0;i<4;i++)Tagged<TextBox>("crop-"+i).Text=new[]{"390","20","100","80"}[i];
            Require(!Named("Применить координаты").IsEnabled&&!Named("Сохранить обрезку").IsEnabled,"out-of-bounds numeric coordinates disable both apply and export");
            for(int i=0;i<4;i++)Tagged<TextBox>("crop-"+i).Text=new[]{"10","20","100","80"}[i];Click("Применить координаты");await ShotAsync("04-numeric-crop.png");
            Require(crop.Selection==new Int32Rect(10,20,100,80)&&Named("Сохранить обрезку").IsEnabled,"valid numeric crop coordinates apply exactly without clamping");
            Click("Сохранить обрезку");await FinishAsync();var cropped=_jobs.Last();
            Require(cropped.HistoryStatus==HistoryStatus.Succeeded&&cropped.Result is{Width:100,Height:80}&&cropped.Options is{CropX:10,CropY:20,CropWidth:100,CropHeight:80},"numeric crop runs a real source-bound task with exact dimensions and coordinates");
            Require(!PresetCatalog.CanSave(cropped.Options)&&CanCompare(cropped),"crop keeps source-bound preset policy while supporting result comparison");
            ShowImageCrop();Tagged<ComboBox>("crop-ratio").SelectedIndex=4;crop=SystemsDescendants<CropPreview>(_panelContent).Single();Click("Сбросить область");await ShotAsync("05-portrait-ratio.png");
            Require(Math.Abs(crop.Selection.Width-crop.Selection.Height*9d/16)<=1,"9:16 reset respects portrait ratio within one-pixel rounding");
            var stale=Named("Сохранить обрезку");int count=_jobs.Count;Require(await LoadFilesAsync(new[]{second}),"another image replaces the old crop context");stale.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Require(_jobs.Count==count,"old crop controls cannot act on a newly selected source");
            await _queueGate.WaitAsync();gateHeld=true;ShowImageResize();Tagged<ComboBox>("resize-mode").SelectedIndex=1;Click("Изменить размер");var canceled=_jobs.Last();canceled.Cancellation.Cancel();_queueGate.Release();gateHeld=false;await FinishAsync();
            Require(canceled.HistoryStatus==HistoryStatus.Cancelled&&canceled.Result==null,"queued percentage resize cancels without a partial output");
            var history=await new HistoryStore(_historyStore.FilePath).LoadAsync();Require(history.Error==null&&history.Entries.Count==_jobs.Count&&history.Entries.Any(e=>e.Options.ResizePercent==50&&e.Options.ResizeFilter=="Point")&&history.Entries.Any(e=>e.Options.JpegBackgroundColor=="#102030")&&history.Entries.Any(e=>e.Options.CropX==10&&e.Options.CropHeight==80),"history round trip retains new resize/matte parameters and exact crop coordinates");
            Require(hashes.All(p=>SystemsDigest(p.Key)==p.Value),"all operations preserve original image SHA256 hashes");
            Require(!Directory.GetDirectories(_outputDirectory).Any(p=>Path.GetFileName(p).StartsWith(".orbit-",StringComparison.Ordinal)),"image resize, JPEG, crop, chain and cancellation leave no private staging");
            File.WriteAllText(Path.Combine(destination,"passed.json"),JsonSerializer.Serialize(new{checks=checks.Count,items=checks,run,userProfileIo=false,nativeClipboardWrites=false,registryWrites=false,nativeShellLaunches=0,physicalInput=false},new JsonSerializerOptions{WriteIndented=true}));
        }catch(Exception ex){App.Log(ex);File.WriteAllText(Path.Combine(destination,"error.txt"),ex.ToString());}
        finally{if(gateHeld)_queueGate.Release();Quit();}
    }
    private static void WriteImageEditorFixture(string path,int width,int height)
    {
        var bytes=new byte[width*height*4];for(int y=0;y<height;y++)for(int x=0;x<width/2;x++){int at=(y*width+x)*4;bytes[at]=20;bytes[at+1]=30;bytes[at+2]=220;bytes[at+3]=255;}
        var bitmap=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,bytes,width*4);bitmap.Freeze();var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(path);encoder.Save(output);
    }
    private static Color ReadEditorPixel(byte[] bytes,int x,int y)
    {
        using var input=new MemoryStream(bytes);var decoder=BitmapDecoder.Create(input,BitmapCreateOptions.PreservePixelFormat|BitmapCreateOptions.IgnoreColorProfile,BitmapCacheOption.OnLoad);var bitmap=new FormatConvertedBitmap(decoder.Frames[0],PixelFormats.Bgra32,null,0);var pixel=new byte[4];bitmap.CopyPixels(new Int32Rect(x,y,1,1),pixel,4,0);return Color.FromArgb(pixel[3],pixel[2],pixel[1],pixel[0]);
    }
}
