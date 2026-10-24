using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Orbit.App.History;
using Orbit.App.Presets;
using Orbit.Core;
using Orbit.Core.Media;
using Orbit.Core.Presets;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private async Task RunAudioMetadataSmokeAsync()
    {
        var destination=Path.Combine(AppContext.BaseDirectory,"audio-metadata-smoke");var run=Path.Combine(destination,"run-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        var checks=new List<string>();var opened=new List<string>();bool gateHeld=false;
        void Require(bool value,string text){if(!value)throw new InvalidOperationException("Audio metadata integration: "+text);checks.Add(text);}
        Button Named(string label)=>SystemsDescendants<Button>(_panelContent).Single(b=>b.Content as string==label);
        void Click(string label)=>Named(label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        T Tagged<T>(string tag)where T:FrameworkElement=>SystemsDescendants<T>(_panelContent).Single(c=>c.Tag as string==tag);
        async Task FinishAsync(){await Task.WhenAll(_jobTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(120));await FlushHistoryAsync();}
        async Task EditorsAsync()=>await Task.WhenAll(_resultToolTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(90));
        async Task ShotAsync(string name){_toast.Visibility=Visibility.Collapsed;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);Capture(Path.Combine(destination,name));}
        void SaveRecipe(JobEntry job,string name){ShowSavePreset(job);_panelContent.Children.OfType<TextBox>().Single().Text=name;Click("Сохранить пресет");}
        async Task SelectTagsAsync(string[] paths){Require(await LoadFilesAsync(paths),"owned audio sources load for tag editing");ShowAudioTags();await EditorsAsync();}
        try{
            Directory.CreateDirectory(run);File.Delete(Path.Combine(destination,"passed.json"));File.Delete(Path.Combine(destination,"error.txt"));
            Require(IsDiagnosticRun&&_bridge is null&&_jobs.Count==0&&!ProductivityIoAllowed&&!PersonalizationIoAllowed,"diagnostic avoids user stores, real hotkeys, registry, clipboard and shell launches");
            var wave=Path.Combine(run,"звук.wav");var otherWave=Path.Combine(run,"другой звук.wav");var cover=Path.Combine(run,"обложка.png");await WriteEditingMediaAsync(wave,false);File.Copy(wave,otherWave);WriteResultsImage(cover);
            var mp3=Path.Combine(run,"песня.mp3");var flac=Path.Combine(run,"песня.flac");var m4a=Path.Combine(run,"песня.m4a");
            foreach(var item in new[]{(Path:mp3,Codec:"libmp3lame",Name:"MP3"),(Path:flac,Codec:"flac",Name:"FLAC"),(Path:m4a,Codec:"aac",Name:"M4A")}){
                await RunMediaTracksFixtureAsync(["-i",wave,"-i",cover,"-map","0:a:0","-map","1:v:0","-c:a",item.Codec,"-c:v","png","-disposition:v:0","attached_pic",
                    "-metadata","title=Исходное "+item.Name,"-metadata","artist=Первый исполнитель","-metadata","album=Оригинальный альбом",item.Path]);
            }
            var originals=new[]{wave,otherWave,cover,mp3,flac,m4a}.ToDictionary(p=>p,SystemsDigest);
            _historyStore=new HistoryStore(Path.Combine(run,"history.json"));_presetStore=new PresetStore(Path.Combine(run,"presets.json"));_allowDiagnosticProductivityIo=true;
            _outputDirectory=Path.Combine(run,"output");Directory.CreateDirectory(_outputDirectory);_resultOpenOverride=(path,_)=>{opened.Add(path);return Task.CompletedTask;};SetCompactMode(true);
            await SelectTagsAsync(new[]{mp3});
            Require(Tagged<TextBox>("tag-title").Text=="Исходное MP3"&&Tagged<TextBox>("tag-artist").Text=="Первый исполнитель"&&Tagged<TextBox>("tag-album").Text=="Оригинальный альбом","single-file card displays actual title, artist and album");
            Require(!Named("Сохранить теги в копию").IsEnabled&&!Tagged<TextBox>("tag-artist").IsEnabled,"tag card requires an explicit field selection");
            Tagged<CheckBox>("tag-set-artist").IsChecked=true;Tagged<TextBox>("tag-artist").Text="Новый исполнитель 🎵";
            Tagged<TextBox>("tag-album").Text="Это не должно попасть в результат";await ShotAsync("01-tag-editor.png");Click("Сохранить теги в копию");await FinishAsync();var first=_jobs.Last();
            Require(first.HistoryStatus==HistoryStatus.Succeeded&&first.Result?.Format=="MP3"&&first.Options is{AudioTagSetArtist:true,AudioTagSetTitle:false,AudioTagSetAlbum:false,AudioTagAlbum:""},"queue changes only the selected artist and discards inactive editor values");
            var tagEngine=new AudioTagsEngine();var read=await tagEngine.InspectAsync(first.Result!.OutputPath);
            Require(read.Artist=="Новый исполнитель 🎵"&&read.Title=="Исходное MP3"&&read.Album=="Оригинальный альбом","real exported MP3 retains untouched title/album and exact Unicode artist");
            var extractedCover=await new MediaCoverEngine().ExtractAsync(first.Result.OutputPath,new MediaCoverOptions{OutputDirectory=_outputDirectory});
            Require(AudioMetadataPixels(cover)==AudioMetadataPixels(extractedCover.OutputPath),"tag editing preserves all decoded pixels of the attached cover");
            await OpenKnownResultAsync(first.Result.OutputPath,false);Require(opened.Last()==first.Result.OutputPath,"tagged audio opens through validated intercepted viewer route");
            SaveRecipe(first,"Исполнитель 🎵");var artistPreset=_customPresets.Single();
            Require(artistPreset.Options is{Action:ImageAction.AudioTags,AudioTagSetArtist:true,AudioTagArtist:"Новый исполнитель 🎵",AudioTagSetTitle:false,AudioTagTitle:"",AudioTagSetAlbum:false,AudioTagAlbum:"",OutputDirectory:null},"tag preset keeps only explicit reusable changes without source tag values or output path");
            Require(await LoadFilesAsync(new[]{flac,m4a}),"FLAC and M4A batch loads for a shared artist preset");ApplyCurrentRecipe(artistPreset);await FinishAsync();var taggedBatch=_jobs.TakeLast(2).ToArray();
            Require(taggedBatch.All(j=>j.HistoryStatus==HistoryStatus.Succeeded)&&taggedBatch.Select(j=>j.Result!.Format).SequenceEqual(new[]{"FLAC","M4A"}),"same tag recipe creates format-preserving FLAC and M4A copies");
            var batchTags=await Task.WhenAll(taggedBatch.Select(j=>tagEngine.InspectAsync(j.Result!.OutputPath)));
            Require(batchTags.All(t=>t.Artist=="Новый исполнитель 🎵")&&batchTags[0].Title=="Исходное FLAC"&&batchTags[1].Title=="Исходное M4A","batch recipe preserves each file's individual unselected title");
            ShowAudioTags();Require(Tagged<TextBox>("tag-title").Text==""&&Tagged<TextBox>("tag-artist").Text=="","batch editor starts blank to avoid copying first-file metadata implicitly");
            Tagged<CheckBox>("tag-set-title").IsChecked=true;Click("Сохранить теги в копию");await FinishAsync();var cleared=_jobs.TakeLast(2).ToArray();
            var clearTags=await Task.WhenAll(cleared.Select(j=>tagEngine.InspectAsync(j.Result!.OutputPath)));
            Require(cleared.All(j=>j.HistoryStatus==HistoryStatus.Succeeded)&&clearTags.All(t=>t.Title==""&&t.Artist=="Первый исполнитель"&&t.Album=="Оригинальный альбом"),"checked empty title explicitly removes that tag while preserving other fields");
            await SelectTagsAsync(new[]{mp3});Tagged<CheckBox>("tag-set-album").IsChecked=true;Tagged<TextBox>("tag-album").Text="bad\nvalue";
            Require(!Named("Сохранить теги в копию").IsEnabled,"control characters cannot queue tag values or corrupt durable history");
            Tagged<TextBox>("tag-album").Text="bad\u2028value";Require(!Named("Сохранить теги в копию").IsEnabled,"Unicode line separators cannot queue multiline tag values");
            Tagged<TextBox>("tag-album").Text="  Альбом 2026  ";Require(Named("Сохранить теги в копию").IsEnabled,"intentional leading/trailing spaces remain valid tag text");Click("Сохранить теги в копию");await FinishAsync();
            Require(_jobs.Last().HistoryStatus==HistoryStatus.Succeeded&&(await tagEngine.InspectAsync(_jobs.Last().Result!.OutputPath)).Album=="  Альбом 2026  ","tag output preserves exact whitespace without trimming");
            ShowAudioTags();ShowFileInfo();await EditorsAsync();Require(!SystemsDescendants<TextBox>(_panelContent).Any(t=>t.Tag as string=="tag-title"),"late tag inspection cannot replace another panel");
            ShowAudioTags();await EditorsAsync();Tagged<CheckBox>("tag-set-title").IsChecked=true;Tagged<TextBox>("tag-title").Text="Устаревшее значение";var stale=Named("Сохранить теги в копию");int count=_jobs.Count;
            Require(await LoadFilesAsync(new[]{wave}),"WAV replaces an inspected MP3 selection");stale.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Require(_jobs.Count==count,"stale tag controls cannot process another source");ShowMoreTools();
            Require(!SystemsDescendants<Button>(_panelContent).Any(b=>b.Content as string=="Теги аудио…")&&SystemsDescendants<Button>(_panelContent).Any(b=>b.Content as string=="Экспорт M4A / Opus / OGG…"),"WAV offers codec export and excludes unsupported tag editing");
            ShowAudioExport();var format=Tagged<ComboBox>("audio-export-format");format.SelectedIndex=2;Tagged<Slider>("audio-vorbis-quality").Value=8;
            Require(Tagged<Slider>("audio-vorbis-quality").Visibility==Visibility.Visible&&!_panelContent.Children.OfType<ComboBox>().Last().IsEnabled,"Vorbis exposes quality while disabling inactive fixed bitrate");await ShotAsync("02-vorbis-export.png");Click("Сохранить аудио");await FinishAsync();var vorbis=_jobs.Last();
            Require(vorbis.HistoryStatus==HistoryStatus.Succeeded&&vorbis.Result?.Format=="OGG"&&vorbis.Options is{AudioExportFormat:"VORBIS",AudioExportVorbisQuality:8,AudioExportBitrateKbps:192},"queue carries Vorbis quality and canonical inactive bitrate into a real OGG");
            var probe=await ProbeMediaExportAsync(vorbis.Result!.OutputPath);var stream=probe.GetProperty("streams").EnumerateArray().Single();
            Require(probe.GetProperty("format").GetProperty("format_name").GetString()=="ogg"&&stream.GetProperty("codec_name").GetString()=="vorbis"&&stream.GetProperty("sample_rate").GetString()=="44100"&&stream.GetProperty("channels").GetInt32()==1,"independent probe confirms actual Ogg/Vorbis, source sample rate and mono channel");
            await OpenKnownResultAsync(vorbis.Result.OutputPath,false);Require(opened.Last()==vorbis.Result.OutputPath,"OGG output uses validated intercepted Open route");SaveRecipe(vorbis,"Vorbis q8");var vorbisPreset=_customPresets.Single(p=>p.Name=="Vorbis q8");
            Require(vorbisPreset.Options is{AudioExportFormat:"VORBIS",AudioExportVorbisQuality:8,AudioExportBitrateKbps:192,AudioTagSetArtist:false,AudioTagArtist:"",OutputDirectory:null},"Vorbis preset stores quality without previous tag edits or output folder");
            Require(await LoadFilesAsync(new[]{wave,otherWave}),"two WAV files load for Vorbis preset batch");ApplyCurrentRecipe(vorbisPreset);await FinishAsync();var vorbisBatch=_jobs.TakeLast(2).ToArray();
            Require(vorbisBatch.All(j=>j.HistoryStatus==HistoryStatus.Succeeded&&j.Result?.Format=="OGG")&&vorbisBatch.Select(j=>j.Result!.OutputPath).Distinct().Count()==2&&vorbisBatch[0].Result!.OutputPath!=vorbis.Result.OutputPath,"Vorbis recipe creates distinct batch results and preserves prior output");
            Require(_repeatRecipe?.Options is{AudioExportFormat:"VORBIS",AudioExportVorbisQuality:8},"repeat recipe retains the chosen Vorbis quality");
            await SelectTagsAsync(new[]{mp3});Tagged<CheckBox>("tag-set-artist").IsChecked=true;Tagged<TextBox>("tag-artist").Text="Не записывать";
            await _queueGate.WaitAsync();gateHeld=true;Click("Сохранить теги в копию");var canceled=_jobs.Last();canceled.Cancellation.Cancel();_queueGate.Release();gateHeld=false;await FinishAsync();
            Require(canceled.HistoryStatus==HistoryStatus.Cancelled&&canceled.Result==null,"queued tag edit cancels without publishing a partial copy");
            var history=await new HistoryStore(_historyStore.FilePath).LoadAsync();Require(history.Error==null&&history.Entries.Count==_jobs.Count&&history.Entries.Any(e=>e.Options is{Action:ImageAction.AudioTags,AudioTagSetTitle:true,AudioTagTitle:""})&&history.Entries.Any(e=>e.Options is{AudioExportFormat:"VORBIS",AudioExportVorbisQuality:8}),"history restores exact cleared-tag flags, Unicode edits, Vorbis quality and cancellation");
            Require(new PresetStore(_presetStore.FilePath).Load().Presets.Count==2,"both reusable tag and Vorbis recipes survive store reload");
            Require(originals.All(p=>SystemsDigest(p.Key)==p.Value),"all original audio and cover SHA-256 hashes remain unchanged");
            Require(!Directory.GetDirectories(_outputDirectory).Any(p=>Path.GetFileName(p).StartsWith(".orbit-",StringComparison.Ordinal)),"all successful and canceled jobs remove private staging");
            File.WriteAllText(Path.Combine(destination,"passed.json"),JsonSerializer.Serialize(new{checks=checks.Count,items=checks,run,userProfileIo=false,nativeClipboardWrites=false,registryWrites=false,nativeShellLaunches=0,physicalInput=false,listeningPerformed=false},new JsonSerializerOptions{WriteIndented=true}));
        }catch(Exception ex){App.Log(ex);File.WriteAllText(Path.Combine(destination,"error.txt"),ex.ToString());}
        finally{if(gateHeld)_queueGate.Release();Quit();}
    }
    private static string AudioMetadataPixels(string path)
    {
        using var input=File.OpenRead(path);var frame=BitmapFrame.Create(input,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
        var image=new FormatConvertedBitmap(frame,PixelFormats.Bgra32,null,0);int stride=checked(image.PixelWidth*4);var pixels=new byte[checked(stride*image.PixelHeight)];image.CopyPixels(pixels,stride,0);
        return image.PixelWidth+"x"+image.PixelHeight+":"+Convert.ToHexString(SHA256.HashData(pixels));
    }
}
