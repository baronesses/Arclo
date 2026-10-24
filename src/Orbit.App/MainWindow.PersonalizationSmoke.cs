using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Orbit.App.Controls;
using Orbit.App.History;
using Orbit.App.Personalization;
using Orbit.App.Presets;
using Orbit.Core;
using Orbit.Core.Presets;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private async Task RunPersonalizationSmokeAsync()
    {
        var destination=Path.Combine(AppContext.BaseDirectory,"personalization-smoke");
        var run=Path.Combine(destination,"run-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        var checks=new List<string>();
        void Require(bool condition,string text){if(!condition)throw new InvalidOperationException("Personalization integration: "+text);checks.Add(text);}
        Button Named(string text)=>SystemsDescendants<Button>(_panelContent).Single(b=>b.Content as string==text);
        void Click(string text)=>Named(text).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        async Task FinishAsync(){await Task.WhenAll(_jobTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(60));await FlushHistoryAsync();}
        async Task WritesAsync()=>await Task.WhenAll(_resultToolTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(30));
        async Task ShotAsync(string name){_toast.Visibility=Visibility.Collapsed;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);Capture(Path.Combine(destination,name));}
        try{
            Directory.CreateDirectory(run);File.Delete(Path.Combine(destination,"passed.json"));File.Delete(Path.Combine(destination,"error.txt"));
            Require(IsDiagnosticRun&&_bridge is null&&_jobs.Count==0&&!PersonalizationIoAllowed,"diagnostic starts without native hotkeys, user history or personalization IO");
            var first=Path.Combine(run,"первый.png");var second=Path.Combine(run,"новое выделение.png");WriteResultsImage(first);WriteEditingTextImage(second);
            var sourceHashes=new[]{first,second}.ToDictionary(p=>p,p=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
            _layoutStore=new RadialLayoutStore(Path.Combine(run,"personalization.json"));_favoriteStore=new FavoritePresetStore(Path.Combine(run,"favorites.json"));
            _presetStore=new PresetStore(Path.Combine(run,"presets.json"));_historyStore=new HistoryStore(Path.Combine(run,"history.json"));
            _allowDiagnosticPersonalizationIo=true;_allowDiagnosticProductivityIo=true;_outputDirectory=Path.Combine(run,"output-one");
            Directory.CreateDirectory(_outputDirectory);
            await LoadPersonalizationAsync();Require(await LoadFilesAsync(new[]{first}),"image is loaded normally");SetCompactMode(true);
            ShowRepeatRecipe();Require(!SystemsDescendants<Button>(_panelContent).Any(b=>b.Content as string=="Повторить для выделения"),"repeat is empty before a successful reusable operation");
            ShowRadialLayout(ToolkitKind.Image);
            Require(SystemsDescendants<Button>(_panelContent).Count(b=>b.Content as string=="↑")==6,"layout editor represents all six image sectors");
            var arrows=SystemsDescendants<Button>(_panelContent).Where(b=>b.Content as string=="↑").ToArray();arrows[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await ShotAsync("01-layout-editor.png");Click("Сохранить порядок");
            Require(_radial.Children.OfType<SectorButton>().First().Action==ImageAction.Png&&_radial.ActionCount==6,"saved order changes sectors without changing count or compact geometry");
            Require(Math.Abs(Width-_compactSize)<2&&Math.Abs(Height-_compactSize)<2,"saving order returns to the compact circle");
            var restoredLayout=new RadialLayoutStore(_layoutStore.FilePath).Load();
            Require(restoredLayout.Warning==null&&restoredLayout.Layout.Apply("Image",DefaultContextActions(ToolkitKind.Image,true),a=>a.Action).First().Action==ImageAction.Png,"custom radial order survives a fresh store instance");
            Require(ContextActions(ToolkitKind.Audio,true).Select(a=>a.Action).SequenceEqual(DefaultContextActions(ToolkitKind.Audio,true).Select(a=>a.Action)),"image customization leaves other contexts unchanged");
            ShowPresets();var picker=_panelContent.Children.OfType<ComboBox>().Single();picker.SelectedItem=PresetCatalog.BuiltIns.Single(p=>p.Id=="builtin-webp-82");
            Click("☆ В избранное");await WritesAsync();
            Require(_favoriteIds.SequenceEqual(new[]{"builtin-webp-82"})&&Named("★ Убрать из избранного").IsEnabled,"favorite star persists and reflects selected preset");
            var persisted=await new FavoritePresetStore(_favoriteStore.FilePath).LoadAsync();Require(persisted.Error==null&&persisted.Ids.SequenceEqual(_favoriteIds),"favorite IDs survive a fresh store instance");
            // A held private fixture makes the async write cross a navigation/selection boundary.
            using(var held=new FileStream(_favoriteStore.FilePath,FileMode.Open,FileAccess.Read,FileShare.None)){
                Click("★ Убрать из избранного");ShowPresets();
                _panelContent.Children.OfType<ComboBox>().Single().SelectedItem=PresetCatalog.BuiltIns.Single(p=>p.Id=="builtin-jpeg-85");
            }
            await WritesAsync();
            Require((_panelContent.Children.OfType<ComboBox>().Single().SelectedItem as ProcessingPreset)?.Id=="builtin-jpeg-85"&&Named("☆ В избранное").IsEnabled,"finishing a favorite write preserves a newly selected preset and reenables its button");
            using(var held=new FileStream(_favoriteStore.FilePath,FileMode.Open,FileAccess.Read,FileShare.None)){
                Click("☆ В избранное");ShowFavorites();
            }
            await WritesAsync();
            Require(Named("Убрать из избранного").IsEnabled&&_favoriteIds.SequenceEqual(new[]{"builtin-jpeg-85"}),"favorites opened during a pending save refresh their enabled state on completion");
            await ToggleFavoriteAsync("builtin-jpeg-85",true);await ToggleFavoriteAsync("builtin-webp-82",false);
            ShowFavorites();await ShotAsync("02-favorites.png");Require(Named("Применить").IsEnabled,"compatible favorite can be applied");Click("Применить");await FinishAsync();
            var firstJob=_jobs.Last();Require(firstJob.HistoryStatus==HistoryStatus.Succeeded&&firstJob.Options.Action==ImageAction.WebP&&firstJob.Options.Quality==82,"favorite creates a real WebP with its stored quality");
            Require(_repeatRecipe is {Options.OutputDirectory:null}&&_repeatRecipe.Options.Quality==82,"successful processing captures only reusable parameters");
            ShowMoreTools();Require(Named("★ WebP · качество 82").IsEnabled,"compatible favorite has a direct More menu shortcut");
            Require(await LoadFilesAsync(new[]{second}),"another image becomes the current selection");_outputDirectory=Path.Combine(run,"output-two");Directory.CreateDirectory(_outputDirectory);
            ShowRepeatRecipe();await ShotAsync("03-repeat.png");Click("Повторить для выделения");await FinishAsync();
            var repeated=_jobs.Last();Require(repeated.HistoryStatus==HistoryStatus.Succeeded&&repeated.InputPaths.SequenceEqual(new[]{second})&&repeated.Options.Quality==82,"repeat processes the new selection with the successful parameters");
            Require(Path.GetDirectoryName(repeated.Result!.OutputPath)==_outputDirectory&&repeated.Options.OutputDirectory==_outputDirectory,"repeat rebinds the current output directory");
            var remembered=_repeatRecipe;RememberCompletedRecipe(new JobEntry(first,new ImageJobOptions{Action=ImageAction.Jpg}){Finished=true,HistoryStatus=HistoryStatus.Cancelled});
            RememberCompletedRecipe(new JobEntry(first,new ImageJobOptions{Action=ImageAction.Crop,CropWidth=2,CropHeight=2}){Finished=true,HistoryStatus=HistoryStatus.Succeeded,Result=firstJob.Result});
            Require(ReferenceEquals(remembered,_repeatRecipe),"cancelled and source-bound operations do not replace the reusable recipe");
            var folder=Path.Combine(run,"folder");Directory.CreateDirectory(folder);Require(await LoadFilesAsync(new[]{folder}),"folder selection is loaded");
            ShowRepeatRecipe();Require(!Named("Повторить для выделения").IsEnabled,"repeat blocks an incompatible current selection");
            ShowFavorites();Require(!Named("Применить").IsEnabled,"favorite blocks an incompatible current selection");
            Require(await LoadFilesAsync(new[]{second}),"image selection can be restored");
            var custom=_presetStore.Add("Мой WebP",new ImageJobOptions{Action=ImageAction.WebP,Quality=61});Require(custom.Success,"custom fixture recipe is saved");_customPresets=custom.Presets;
            var customId=custom.Presets.Single().Id;await ToggleFavoriteAsync(customId,false);
            _customPresets=_presetStore.Delete(customId).Presets;ShowFavorites();
            Require(SystemsDescendants<TextBlock>(_panelContent).Any(t=>t.Text=="Пресет удалён или недоступен"),"deleted custom favorite stays removable without applying stale parameters");
            var buttons=SystemsDescendants<Button>(_panelContent).Where(b=>b.Content as string=="Убрать из избранного").ToArray();buttons[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await WritesAsync();
            Require(_favoriteIds.SequenceEqual(new[]{"builtin-webp-82"}),"missing custom favorite can be removed");
            ShowRadialLayout(ToolkitKind.Image);Click("Стандартный порядок для типа");ClosePanel();
            Require(_radial.Children.OfType<SectorButton>().First().Action==ImageAction.Jpg,"reset restores the selected context's default order");
            var videoOrder=new[]{ImageAction.ExtractMp3,ImageAction.VideoMp4,ImageAction.VideoWebm,ImageAction.VideoFrame,ImageAction.VideoCompress,ImageAction.ArchiveZip};
            var videoLayout=_layoutStore.SetOrder("Video",videoOrder);Require(videoLayout.Success,"video order fixture saves");_radialLayout=videoLayout.Layout;
            Require(ContextActions(ToolkitKind.Video,true)[0].Action==ImageAction.ExtractMp3&&ContextActions(ToolkitKind.Video,false)[0].Action==ImageAction.FileInfo&&!ContextActions(ToolkitKind.Video,false).Any(a=>a.Action==ImageAction.ExtractMp3),"silent video keeps the configured slot and never exposes audio extraction");
            File.WriteAllText(_layoutStore.FilePath,"{ broken-layout");File.WriteAllText(_favoriteStore.FilePath,"{ broken-favorites");
            _layoutStore=new RadialLayoutStore(_layoutStore.FilePath);_favoriteStore=new FavoritePresetStore(_favoriteStore.FilePath);await LoadPersonalizationAsync();
            ShowRadialLayout(ToolkitKind.Image);Require(!Named("Сохранить порядок").IsEnabled&&File.ReadAllText(_layoutStore.FilePath)=="{ broken-layout","malformed layout is preserved until explicit recovery");
            Click("Сбросить все раскладки");Require(Named("Сохранить порядок").IsEnabled,"explicit layout recovery restores editing");
            ShowFavorites();Require(Named("Сбросить избранное").IsEnabled&&File.ReadAllText(_favoriteStore.FilePath)=="{ broken-favorites","malformed favorites are preserved until explicit recovery");
            Click("Сбросить избранное");await WritesAsync();Require(!_favoritesBlocked&&_favoriteIds.Count==0,"explicit favorites recovery restores a clean store");
            Require(sourceHashes.All(pair=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pair.Key)))==pair.Value),"all operations leave source fixture bytes unchanged");
            ClosePanel();await ShotAsync("04-compact-default.png");
            File.WriteAllText(Path.Combine(destination,"passed.json"),JsonSerializer.Serialize(new{checks=checks.Count,items=checks,run,nativeClipboardWrites=false,registryWrites=false,userProfileIo=false,physicalInput=false},new JsonSerializerOptions{WriteIndented=true}));
        }catch(Exception ex){App.Log(ex);File.WriteAllText(Path.Combine(destination,"error.txt"),ex.ToString());}
        Quit();
    }
}
