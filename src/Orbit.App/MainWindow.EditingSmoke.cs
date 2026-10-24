using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Orbit.App.Editing;
using Orbit.Core;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private async Task RunEditingSmokeAsync()
    {
        var destination=Path.Combine(AppContext.BaseDirectory,"editing-smoke");
        var fixtures=Path.Combine(destination,"fixtures");var output=Path.Combine(destination,"output");
        var checks=new List<string>();
        void Require(bool passed,string message){if(!passed)throw new InvalidOperationException("Editing integration: "+message);checks.Add(message);}
        Button ButtonNamed(string name)=>SystemsDescendants<Button>(_panelContent).Single(b=>b.Content as string==name);
        async Task<ImageJobResult[]> ClickJobs(Button button,ImageAction action,int count=1){
            var before=_jobs.Count;button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(_jobs.Count==before+count,"actual card queues expected job count for "+action);
            await Task.WhenAll(_jobTasks).WaitAsync(TimeSpan.FromMinutes(2));
            var jobs=_jobs.Skip(before).ToArray();
            Require(jobs.All(j=>j.Options.Action==action&&j.Finished&&j.Result!=null&&File.Exists(j.Result.OutputPath)),"all actual card jobs create files: "+action+" · "+string.Join("; ",jobs.Select(j=>j.Detail)));
            Require(_compact&&_panel.Visibility==Visibility.Collapsed,"card returns to compact circle for "+action);
            return jobs.Select(j=>j.Result!).ToArray();
        }
        try{
            Directory.CreateDirectory(fixtures);Directory.CreateDirectory(output);
            File.Delete(Path.Combine(destination,"passed.json"));File.Delete(Path.Combine(destination,"error.txt"));
            Require(IsDiagnosticRun&&_bridge==null,"diagnostic has no native shortcuts, tray or real preference writes");
            foreach(var (input,expected) in new[]{("12,5",12.5),("01:12.500",72.5),("1:02:03.125",3723.125),("0",0d),("120:00",7200d)})
                Require(MediaTimecode.TryParse(input,out var parsed)&&Math.Abs(parsed-expected)<0.00001,"timecode parses "+input);
            foreach(var invalid in new[]{"","-1","NaN","Infinity","1e2","1:60","1:60:00","1::2","2.5:10","0:0:60","1,2,3"})
                Require(!MediaTimecode.TryParse(invalid,out _),"timecode rejects "+invalid);
            Require(MediaTimecode.Format(1.99999)=="00:00:01.999","displayed end is floored to source milliseconds");
            _outputDirectory=output;if(!_compact)SetCompactMode(true);
            var image=Path.Combine(fixtures,"печатный текст.png");WriteEditingTextImage(image);
            var originalDigest=SystemsDigest(image);
            Require(await LoadFilesAsync(new[]{image}),"image loads through ordinary selection path");
            ShowMoreTools();Require(ButtonNamed("Повернуть и отразить…")!=null&&ButtonNamed("Распознать текст (OCR)…")!=null,"image more menu exposes transform and OCR");
            ButtonNamed("Повернуть и отразить…").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var angles=SystemsDescendants<ComboBox>(_panelContent).Single();angles.SelectedIndex=1;
            var horizontal=SystemsDescendants<CheckBox>(_panelContent).Single(c=>c.Content as string=="Отразить слева направо");horizontal.IsChecked=true;
            var preview=SystemsDescendants<System.Windows.Controls.Image>(_panelContent).Single();
            var group=(TransformGroup)preview.LayoutTransform;
            Require(((RotateTransform)group.Children[0]).Angle==90&&((ScaleTransform)group.Children[1]).ScaleX==-1,"actual preview applies rotation before mirror");
            await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);Capture(Path.Combine(destination,"01-transform-card.png"));
            var transformed=(await ClickJobs(ButtonNamed("Сохранить PNG"),ImageAction.ImageTransform))[0];
            Require(transformed.Width==360&&transformed.Height==900&&transformed.Format=="PNG","transform card returns correctly rotated PNG");
            Require(_jobs[^1].Options.FlipHorizontal&&_jobs[^1].Options.RotationDegrees==90,"UI passes selected transform options to queued job");
            Require(SystemsDigest(image)==originalDigest,"image transform preserves original SHA-256");

            DispatchAction(ImageAction.OcrText);var languages=SystemsDescendants<ComboBox>(_panelContent).Single();languages.SelectedIndex=0;
            await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);Capture(Path.Combine(destination,"02-ocr-card.png"));
            var text=(await ClickJobs(ButtonNamed("Распознать в TXT"),ImageAction.OcrText))[0];
            var recognized=await File.ReadAllTextAsync(text.OutputPath);
            Require(recognized.Contains("ORBIT",StringComparison.OrdinalIgnoreCase)&&recognized.Contains("2026"),"actual OCR card recognizes real English image content");
            Require(recognized.Contains("ПРИВЕТ",StringComparison.OrdinalIgnoreCase),"actual OCR card recognizes real Russian image content");
            Require(SystemsDigest(image)==originalDigest,"OCR preserves original image SHA-256");
            var pdf=Path.Combine(fixtures,"two pages.pdf");WriteSystemsPdf(pdf,new[]{("ORBIT ALPHA 2026",650,200),("ORBIT BETA 2027",650,200)});
            var pdfDigest=SystemsDigest(pdf);
            Require(await LoadFilesAsync(new[]{pdf}),"PDF loads for actual OCR card");
            DispatchAction(ImageAction.OcrText);var pdfCombos=SystemsDescendants<ComboBox>(_panelContent).ToArray();pdfCombos[0].SelectedIndex=2;pdfCombos[1].SelectedItem=200;
            var pdfText=(await ClickJobs(ButtonNamed("Распознать в TXT"),ImageAction.OcrText))[0];
            var recognizedPdf=await File.ReadAllTextAsync(pdfText.OutputPath);
            Require(recognizedPdf.Contains("ALPHA")&&recognizedPdf.Contains("BETA")&&recognizedPdf.IndexOf("ALPHA",StringComparison.Ordinal)<recognizedPdf.IndexOf("BETA",StringComparison.Ordinal),"actual PDF OCR card recognizes both pages in order");
            Require(SystemsDigest(pdf)==pdfDigest,"PDF OCR preserves original bytes");
            Require(await LoadFilesAsync(new[]{image,pdf}),"mixed image/PDF selection loads for OCR batch");
            ShowMoreTools();ButtonNamed("Распознать текст (OCR)…").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var batch=await ClickJobs(ButtonNamed("Распознать в TXT"),ImageAction.OcrText,2);
            Require(batch.Select(r=>r.OutputPath).Distinct(StringComparer.OrdinalIgnoreCase).Count()==2,"mixed OCR batch creates separate unique TXT files");

            foreach(var (filename,kind,action) in new[]{("video.mp4",ToolkitKind.Video,ImageAction.VideoTrim),("audio.wav",ToolkitKind.Audio,ImageAction.AudioTrim)}){
                var source=Path.Combine(fixtures,filename);await WriteEditingMediaAsync(source,kind==ToolkitKind.Video);
                var digest=SystemsDigest(source);Require(await LoadFilesAsync(new[]{source}),"real media loads for trim card: "+kind);
                ShowMoreTools();ButtonNamed("Обрезать по времени…").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var times=SystemsDescendants<TextBox>(_panelContent).ToArray();var run=ButtonNamed("Сохранить фрагмент");
                times[0].Text="-1";Require(!run.IsEnabled,"trim disables invalid negative start: "+kind);
                times[0].Text="0";times[1].Text="100";Require(!run.IsEnabled,"trim disables end outside source: "+kind);
                times[0].Text="1,0";times[1].Text="00:00:02.500";Require(run.IsEnabled,"trim accepts decimal comma and timecode: "+kind);
                await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);Capture(Path.Combine(destination,kind==ToolkitKind.Video?"03-video-trim-card.png":"04-audio-trim-card.png"));
                var clipped=(await ClickJobs(run,action))[0];var actual=await new MediaEngine().InspectAsync(clipped.OutputPath);
                Require(Math.Abs(actual.DurationSeconds-1.5)<0.15,"trim card output duration independently probed: "+kind);
                Require(actual.Kind==kind.ToString()&&actual.HasAudio,"trim card retains expected real streams: "+kind);
                Require(_jobs[^1].Options.TrimStartSeconds==1&&_jobs[^1].Options.TrimEndSeconds==2.5,"trim card forwards exact selected range: "+kind);
                Require(SystemsDigest(source)==digest,"trim preserves original SHA-256: "+kind);
            }
            Require(!Directory.EnumerateFileSystemEntries(output).Any(p=>Path.GetFileName(p).StartsWith(".orbit-",StringComparison.OrdinalIgnoreCase)),"all editing jobs clean intermediate output files");
            await File.WriteAllTextAsync(Path.Combine(destination,"passed.json"),JsonSerializer.Serialize(new{Passed=true,Count=checks.Count,Checks=checks},new JsonSerializerOptions{WriteIndented=true}));
        }catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(destination,"error.txt"),ex.ToString());}
        Quit();
    }

    private static void WriteEditingTextImage(string path)
    {
        var visual=new DrawingVisual();using(var drawing=visual.RenderOpen()){
            drawing.DrawRectangle(Brushes.White,null,new Rect(0,0,900,360));
            var font=new Typeface("Arial");
            drawing.DrawText(new FormattedText("ORBIT ALPHA 2026",CultureInfo.InvariantCulture,FlowDirection.LeftToRight,font,60,Brushes.Black,1),new Point(35,55));
            drawing.DrawText(new FormattedText("ПРИВЕТ МИР",CultureInfo.GetCultureInfo("ru-RU"),FlowDirection.LeftToRight,font,60,Brushes.Black,1),new Point(35,165));
        }
        var bitmap=new RenderTargetBitmap(900,360,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(path);encoder.Save(stream);
    }

    private static async Task WriteEditingMediaAsync(string path,bool video)
    {
        var executable=Path.Combine(AppContext.BaseDirectory,"tools","ffmpeg","ffmpeg.exe");
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
        foreach(var arg in new[]{"-hide_banner","-loglevel","error","-y"})start.ArgumentList.Add(arg);
        if(video)foreach(var arg in new[]{"-f","lavfi","-i","testsrc2=size=160x100:rate=20:duration=4"})start.ArgumentList.Add(arg);
        foreach(var arg in new[]{"-f","lavfi","-i","sine=frequency=440:duration=4","-t","4"})start.ArgumentList.Add(arg);
        if(video)foreach(var arg in new[]{"-c:v","libx264","-pix_fmt","yuv420p","-c:a","aac"})start.ArgumentList.Add(arg);
        else foreach(var arg in new[]{"-c:a","pcm_s16le"})start.ArgumentList.Add(arg);
        start.ArgumentList.Add(path);
        using var process=Process.Start(start)??throw new IOException("Could not start FFmpeg fixture generator");
        var stderr=process.StandardError.ReadToEndAsync();var stdout=process.StandardOutput.ReadToEndAsync();
        try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));}catch{if(!process.HasExited)process.Kill(true);throw;}
        await stdout;if(process.ExitCode!=0)throw new IOException(await stderr);
    }
}
