using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Orbit.App.History;
using Orbit.App.Platform;
using Orbit.App.Presets;
using Orbit.Core;
using Orbit.Core.Presets;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private HistoryStore _historyStore=new();
    private PresetStore _presetStore=new();
    private IReadOnlyList<ProcessingPreset> _customPresets=Array.Empty<ProcessingPreset>();
    private bool _keepHistory=true;
    private bool _allowDiagnosticProductivityIo;
    private bool _historyWriterActive;
    private bool _historyClearing;
    private bool _historyBlocked;
    private bool _historyLoading;
    private TaskCompletionSource? _historyLoadCompletion;
    private Func<Task>? _historyLoadBarrier;
    private Func<Task>? _historyRetryValidationBarrier;
    private string? _historyWarning;
    private string? _presetWarning;
    private IReadOnlyList<HistoryEntry>? _pendingHistory;
    private Task _historyWriteTask=Task.CompletedTask;
    private TaskCompletionSource? _historyClearCompletion;
    private bool _recentVisible;
    private readonly HashSet<string> _missingResults=new(StringComparer.OrdinalIgnoreCase);
    private Func<string,bool,Task>? _resultOpenOverride;
    private bool ProductivityIoAllowed=>!IsDiagnosticRun||_allowDiagnosticProductivityIo;

    private async Task LoadProductivityAsync()
    {
        if(!ProductivityIoAllowed)return;
        if(_historyLoading){if(_historyLoadCompletion!=null)await _historyLoadCompletion.Task;return;}
        _historyLoading=true;_historyLoadCompletion=new(TaskCreationOptions.RunContinuationsAsynchronously);
        try{
            var presets=_presetStore.Load();_customPresets=presets.Presets;_presetWarning=presets.Warning;
            if(!_keepHistory)return;
            if(IsDiagnosticRun&&_historyLoadBarrier!=null)await _historyLoadBarrier();
            var history=await _historyStore.LoadAsync();
            _historyWarning=history.Error;_historyBlocked=history.Error!=null;
            if(!_keepHistory)return;
            var existing=_jobs.Select(j=>j.Id).ToHashSet();
            foreach(var entry in history.Entries.OrderBy(e=>e.CreatedUtc)){
                if(!existing.Add(entry.Id))continue;
                var state=entry.Status switch{
                    HistoryStatus.Succeeded=>global::Orbit.Core.Localization.L.Get("Text_EF05D57959CF"),HistoryStatus.Failed=>global::Orbit.Core.Localization.L.Get("Text_BF22DFEEBA8B"),HistoryStatus.Cancelled=>global::Orbit.Core.Localization.L.Get("Text_23A2A9BFCF74"),_=>global::Orbit.Core.Localization.L.Get("Text_B3CE53956234")
                };
                _jobs.Add(new JobEntry(entry.Download?.Url??entry.SourcePaths[0],entry.Options){Id=entry.Id,CreatedUtc=entry.CreatedUtc,FinishedUtc=entry.FinishedUtc,Download=entry.Download,
                    InputPaths=entry.SourcePaths.ToArray(),HistoryStatus=entry.Status,Result=entry.Result,Finished=true,State=state,
                    Detail=entry.Result!=null?ResultDetail(entry.Result):entry.ErrorCaption??global::Orbit.Core.Localization.L.Get("Text_8E4D9BC3F14B")});
            }
            PruneHistoryEntries();UpdateJobsButton();RefreshJobs();
        }catch(Exception ex){_historyBlocked=true;_historyWarning=global::Orbit.Core.Localization.L.Get("Text_5465FFC917BB");App.Log(ex);}
        finally{
            _historyLoading=false;_historyLoadCompletion.TrySetResult();
            if(_jobs.Count>0)ScheduleHistorySave();
        }
    }

    private static string ResultDetail(ImageJobResult result)
    {
        var detail=FormatBytes(result.Bytes)+" · "+(result.Width>0?$"{result.Width} × {result.Height}":result.Format);
        if(result.SourceBytes>0){var delta=100d*(1d-(double)result.Bytes/result.SourceBytes);detail+=" · "+(delta>=0?$"−{delta:F0}%":$"+{-delta:F0}%");}
        return detail;
    }
    private void UpdateJobsButton(){_jobsButton.Content=global::Orbit.Core.Localization.L.Format("Text_3A3A00DF960A",(object?)(_jobs.Count))+(_queueRunControl.IsPaused?" Ⅱ":"");UpdateQueueControls();}
    private void PruneHistoryEntries()
    {
        var keep=_jobs.Where(j=>j.Finished).OrderByDescending(j=>j.CreatedUtc).Take(100).Select(j=>j.Id).ToHashSet();
        _jobs.RemoveAll(j=>j.Finished&&!keep.Contains(j.Id));
    }
    private IReadOnlyList<HistoryEntry> HistorySnapshot()=>_jobs.OrderByDescending(j=>j.CreatedUtc).Take(100).Select(j=>new HistoryEntry{
        Id=j.Id,CreatedUtc=j.CreatedUtc,FinishedUtc=j.FinishedUtc,SourcePaths=j.InputPaths.ToArray(),Options=j.Options,
        Status=j.HistoryStatus,Result=j.Result,ErrorCaption=j.Result==null?j.Detail:null,Download=j.Download
    }).ToArray();
    private void ScheduleHistorySave()
    {
        if(!ProductivityIoAllowed||!_keepHistory||_historyBlocked||_historyClearing||_historyLoading)return;
        _pendingHistory=HistorySnapshot();
        if(!_historyWriterActive)_historyWriteTask=DrainHistoryWritesAsync();
    }
    private async Task DrainHistoryWritesAsync()
    {
        _historyWriterActive=true;
        try{
            while(_pendingHistory is { } snapshot){
                _pendingHistory=null;
                var error=await _historyStore.SaveAsync(snapshot);
                if(error!=null){_historyWarning=error;_historyBlocked=true;_pendingHistory=null;Toast(error);break;}
            }
        }catch(Exception ex){_historyWarning=global::Orbit.Core.Localization.L.Get("Text_D52DD23C98E7")+ex.Message;_historyBlocked=true;Toast(_historyWarning);App.Log(ex);}
        finally{_historyWriterActive=false;}
    }
    private async Task FlushHistoryAsync()
    {
        if(_historyLoadCompletion!=null)await _historyLoadCompletion.Task;
        if(_historyClearCompletion!=null)await _historyClearCompletion.Task;
        ScheduleHistorySave();await _historyWriteTask;
    }
    private async Task ClearCompletedHistoryAsync(IReadOnlySet<Guid>? selectedIds=null)
    {
        if(_historyLoadCompletion!=null)await _historyLoadCompletion.Task;
        if(_historyClearing)return;
        var clearedIds=_jobs.Where(j=>j.Finished&&(selectedIds==null||selectedIds.Contains(j.Id))).Select(j=>j.Id).ToHashSet();
        _historyClearing=true;_historyClearCompletion=new(TaskCreationOptions.RunContinuationsAsynchronously);_pendingHistory=null;
        try{
            await _historyWriteTask;
            _jobs.RemoveAll(j=>clearedIds.Contains(j.Id));UpdateJobsButton();
            if(ProductivityIoAllowed){
                var error=await _historyStore.SaveAsync(HistorySnapshot());_historyWarning=error;_historyBlocked=error!=null;
                if(error!=null)Toast(error);
            }
            _jobs.RemoveAll(j=>clearedIds.Contains(j.Id));UpdateJobsButton();
            if(_recentVisible)RenderRecentResults();else RefreshJobs();
        }finally{_historyClearing=false;_historyClearCompletion.TrySetResult();}
        ScheduleHistorySave();await _historyWriteTask;
    }
    private void AddHistorySettings()
    {
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_2350B6189437"),15,"#E9F2FF"));
        var enabled=new CheckBox{Content=global::Orbit.Core.Localization.L.Get("Text_20AAB3CECAE4"),IsChecked=_keepHistory,Margin=new Thickness(0,3,0,7)};
        enabled.Click+=async(_,_)=>{
            if(_historyClearing){enabled.IsChecked=_keepHistory;return;}
            _keepHistory=enabled.IsChecked==true;SavePreferences();
            if(_keepHistory){ScheduleHistorySave();await _historyWriteTask;}
            else{
                enabled.IsEnabled=false;_pendingHistory=null;_historyClearing=true;_historyClearCompletion=new(TaskCreationOptions.RunContinuationsAsynchronously);
                try{if(_historyLoadCompletion!=null)await _historyLoadCompletion.Task;await _historyWriteTask;if(ProductivityIoAllowed){var error=await _historyStore.ClearAsync();_historyWarning=error;_historyBlocked=error!=null;if(error!=null)Toast(error);}}
                finally{_historyClearing=false;enabled.IsEnabled=true;_historyClearCompletion.TrySetResult();}
            }
        };
        _panelContent.Children.Add(enabled);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_9379E3365D45"),12));
        _panelContent.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_83ABC7EFB374"),async(_,_)=>await ClearCompletedHistoryAsync()));
    }

    private async Task RetryHistoryJobAsync(JobEntry job)
    {
        if(job.Download is{} download){ShowDownloadPanel(download.Mode,download.Url);return;}
        int panelVersion=_comparisonVersion,loadVersion=_loadVersion;
        bool Current()=>!_closing&&panelVersion==_comparisonVersion&&loadVersion==_loadVersion;
        _popupInteractionDepth++;
        try{
            if(IsDiagnosticRun&&_historyRetryValidationBarrier is not null)await _historyRetryValidationBarrier();
            if(!Current())return;
            var selection=await Task.Run(()=>SelectedPathPolicy.NormalizeExisting(job.InputPaths)).WaitAsync(TimeSpan.FromSeconds(5));
            if(!Current())return;
            if(!selection.Success||selection.RejectedCount>0){Toast(global::Orbit.Core.Localization.L.Get("Text_13811DAE48E1"));return;}
            if(job.Options is{Action:ImageAction.ImageWatermark,WatermarkMode:"Logo"}){
                var load=LoadFilesAsync(selection.Paths,true);int requestedVersion=_loadVersion;
                if(!await load||_closing||requestedVersion!=_loadVersion||!selection.Paths.SequenceEqual(_files))return;
                ShowWatermarkEditor(job.Options with{WatermarkLogoPath=null,WatermarkLogoSha256=null,OutputDirectory=_outputDirectory});
                Toast(global::Orbit.Core.Localization.L.Get("Text_844B36B9A40E"));return;
            }
            if(job.Options.Action is ImageAction.SevenZipExtractSelected or ImageAction.TarExtractSelected){
                if(selection.Paths.Count!=1){Toast(global::Orbit.Core.Localization.L.Get("Text_556F533BC350"));return;}
                var load=LoadFilesAsync(selection.Paths,true);int requestedVersion=_loadVersion;
                if(!await load||_closing||requestedVersion!=_loadVersion||!selection.Paths.SequenceEqual(_files))return;
                if(job.Options.Action==ImageAction.SevenZipExtractSelected)ShowSevenZipBrowser();else ShowTarBrowser();
                Toast(global::Orbit.Core.Localization.L.Get("Text_F20B5F7BA9AA"));return;
            }
            if(job.Options.OutputDirectory is { } directory){
                bool exists=await Task.Run(()=>Directory.Exists(directory)).WaitAsync(TimeSpan.FromSeconds(5));
                if(!Current())return;
                if(!exists){Toast(global::Orbit.Core.Localization.L.Get("Text_74C7BA2BC4D6"));return;}
            }
            if(job.Options.SevenZipPasswordProtected){ShowSevenZipPasswordRetry(selection.Paths,job.Options);return;}
            Enqueue(selection.Paths[0],job.Options,selection.Paths);RefreshJobs();
        }catch(Exception ex){if(Current())Toast(global::Orbit.Core.Localization.L.Get("Text_F4B3C1B01D9B")+ex.Message);}
        finally{_popupInteractionDepth--;ArmPopupDismissal();}
    }

    private void ShowPresets()=>ShowPresetPicker(null);
    private void ShowPresetPicker(string? preferredId)
    {
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_467ACA1D8E12"),_files.Count==0?global::Orbit.Core.Localization.L.Get("Text_220C0B807F4F"):global::Orbit.Core.Localization.L.Format("Text_54F49F23798C",(object?)(_files.Count)));
        if(_presetWarning!=null){
            _panelContent.Children.Add(Text(_presetWarning,12,"#EDACB9"));
            _panelContent.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_9D529198772A"),(_,_)=>{if(!ProductivityIoAllowed)return;var result=_presetStore.Reset();if(!result.Success){Toast(result.Error!);return;}_customPresets=result.Presets;_presetWarning=null;ShowPresets();}));
        }
        var presets=PresetCatalog.BuiltIns.Concat(_customPresets).ToArray();
        var picker=new ComboBox{ItemsSource=presets,DisplayMemberPath="Name",Margin=new Thickness(0,8,0,10)};_panelContent.Children.Add(picker);
        var description=Text("",13);_panelContent.Children.Add(description);
        var destination=Text(_outputDirectory==null?global::Orbit.Core.Localization.L.Get("Text_D1362DCB8037"):global::Orbit.Core.Localization.L.Get("Text_B99DF19B7B50")+_outputDirectory,12);_panelContent.Children.Add(destination);
        var compatibility=Text("",12);_panelContent.Children.Add(compatibility);
        var run=new Button{Content=global::Orbit.Core.Localization.L.Get("Text_E21AC53CCAC2"),Margin=new Thickness(0,8,0,8)};GlassAccent(run);_panelContent.Children.Add(run);
        var rename=Btn(global::Orbit.Core.Localization.L.Get("Text_C40F6F77ADAB"),(_,_)=>{if(picker.SelectedItem is ProcessingPreset preset)ShowRenamePreset(preset);});
        var delete=Btn(global::Orbit.Core.Localization.L.Get("Text_02B27717F224"),(_,_)=>{
            if(picker.SelectedItem is not ProcessingPreset preset||preset.IsBuiltIn||!ProductivityIoAllowed)return;
            var result=_presetStore.Delete(preset.Id);if(!result.Success){Toast(result.Error!);return;}_customPresets=result.Presets;ShowPresets();
        });
        var actions=new WrapPanel();actions.Children.Add(rename);actions.Children.Add(delete);_panelContent.Children.Add(actions);
        AddFavoriteButton(actions,()=>picker.SelectedItem as ProcessingPreset);
        void Update(){
            if(picker.SelectedItem is not ProcessingPreset preset)return;
            description.Text=PresetCatalog.DescribeOptions(preset.Options);
            bool allowed=PresetCatalog.CanApply(preset,_selectionMetadata,out var reason)&&!_selectionLoading;
            if(preset.Options.Action==ImageAction.PdfText&&!_engine.PdfTextAvailable){allowed=false;reason=global::Orbit.Core.Localization.L.Get("Text_3C7367B7C409");}
            run.IsEnabled=allowed;compatibility.Text=allowed?global::Orbit.Core.Localization.L.Get("Text_FA4C69F978C5"):reason??global::Orbit.Core.Localization.L.Get("Text_0C44195BEC05");
            if(allowed&&preset.Options.Action is ImageAction.ImagesToPdf or ImageAction.ContactSheet)compatibility.Text=global::Orbit.Core.Localization.L.Get("Text_F64B9EF54EF0");
            compatibility.Foreground=Ink(allowed?"#A7D8C8":"#EDACB9");rename.Visibility=delete.Visibility=preset.IsBuiltIn?Visibility.Collapsed:Visibility.Visible;
        }
        picker.SelectionChanged+=(_,_)=>Update();
        picker.SelectedItem=presets.FirstOrDefault(p=>p.Id==preferredId)??presets.FirstOrDefault(p=>PresetCatalog.CanApply(p,_selectionMetadata))??presets[0];Update();
        run.Click+=(_,_)=>{
            if(picker.SelectedItem is not ProcessingPreset preset||!PresetCatalog.CanApply(preset,_selectionMetadata)||_selectionLoading)return;
            ApplyCurrentRecipe(preset);
        };
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_258BA300B02F"),12));
        _panelContent.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_0FB52CCFB842"),(_,_)=>ChooseFiles()));
    }
    private void ShowSavePreset(JobEntry job)
    {
        if(!PresetCatalog.CanSave(job.Options)){Toast(global::Orbit.Core.Localization.L.Get("Text_B861C46A8F66"));return;}
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_BAFD153F2688"),ActionName(job.Options.Action));
        _panelContent.Children.Add(Text(PresetCatalog.DescribeOptions(job.Options),13));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_0918B4BA9268")));
        var name=new TextBox{Text=ActionName(job.Options.Action),MaxLength=64,Margin=new Thickness(0,0,0,10)};_panelContent.Children.Add(name);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_6F14138D9EE0"),12));
        var save=Btn(global::Orbit.Core.Localization.L.Get("Text_BAFD153F2688"),(_,_)=>{
            if(!ProductivityIoAllowed){Toast(global::Orbit.Core.Localization.L.Get("Text_5C1FD0E9C8D2"));return;}
            var result=_presetStore.Add(name.Text,job.Options);if(!result.Success){Toast(result.Error!);return;}_customPresets=result.Presets;_presetWarning=null;ShowPresets();
        });GlassAccent(save);_panelContent.Children.Add(save);
    }
    private void ShowRenamePreset(ProcessingPreset preset)
    {
        if(preset.IsBuiltIn)return;
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_DC9756B3DEA7"),preset.Name);
        var name=new TextBox{Text=preset.Name,MaxLength=64,Margin=new Thickness(0,8,0,10)};_panelContent.Children.Add(name);
        _panelContent.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_620BC932F669"),(_,_)=>{
            if(!ProductivityIoAllowed)return;var result=_presetStore.Update(preset.Id,name.Text,preset.Options);
            if(!result.Success){Toast(result.Error!);return;}_customPresets=result.Presets;ShowPresets();
        }));
    }

    private void ShowRecentResults()
    {
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_A0705C8C4AC6"),global::Orbit.Core.Localization.L.Get("Text_FFF649BE228B"));_recentVisible=true;RenderRecentResults();
    }
    private void RenderRecentResults()
    {
        while(_panelContent.Children.Count>2)_panelContent.Children.RemoveAt(2);
        var results=_jobs.Where(j=>j.Finished&&j.Result!=null).OrderByDescending(j=>j.FinishedUtc??j.CreatedUtc).Take(20).ToArray();
        if(results.Length==0){_panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_D7EBC2B7DCD6"),13));return;}
        foreach(var job in results){
            var path=job.Result!.OutputPath;var stack=new StackPanel();
            stack.Children.Add(Text(Path.GetFileName(path),14,"#E2EAF6"));
            var location=Text(path,11,"#9CAAC1");location.ToolTip=path;stack.Children.Add(location);
            stack.Children.Add(Text(ActionName(job.Options.Action)+" · "+ResultDetail(job.Result),12));
            if(_missingResults.Contains(path))stack.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_EEC868A565A6"),12,"#EDACB9"));
            var buttons=new WrapPanel();buttons.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_EA1F40824607"),async(_,_)=>await OpenKnownResultAsync(path,false)));
            buttons.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_A99564C5702F"),async(_,_)=>await OpenKnownResultAsync(path,true)));AddResultToolButtons(buttons,job);stack.Children.Add(buttons);
            var card=new Border{Child=stack,Padding=new Thickness(13,7,13,11),Margin=new Thickness(0,6,0,4),CornerRadius=new CornerRadius(8),BorderThickness=new Thickness(1)};
            card.SetResourceReference(Border.BackgroundProperty,"GlassControlFill");card.SetResourceReference(Border.BorderBrushProperty,"GlassEdge");_panelContent.Children.Add(card);
        }
    }
    private async Task OpenKnownResultAsync(string path,bool reveal)
    {
        _popupInteractionDepth++;
        try{
            var checkedPath=await Task.Run(()=>{
                bool valid=SelectedPathPolicy.TryNormalizeExisting(path,out var normalized,out _);
                return (Valid:valid,Path:normalized,Directory:valid&&Directory.Exists(normalized));
            }).WaitAsync(TimeSpan.FromSeconds(5));
            if(!checkedPath.Valid){_missingResults.Add(path);if(_recentVisible)RenderRecentResults();Toast(global::Orbit.Core.Localization.L.Get("Text_EEC868A565A6"));return;}
            if(!reveal&&!checkedPath.Directory&&!SafeResultExtensions.Contains(Path.GetExtension(checkedPath.Path))){Toast(global::Orbit.Core.Localization.L.Get("Text_F9737CE66BA1"));return;}
            _missingResults.Remove(path);
            if(_resultOpenOverride!=null){await _resultOpenOverride(checkedPath.Path,reveal);return;}
            if(reveal||checkedPath.Directory){
                var explorer=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe")){UseShellExecute=false};
                if(reveal)explorer.ArgumentList.Add("/select,");explorer.ArgumentList.Add(checkedPath.Path);Process.Start(explorer);
            }else Process.Start(new ProcessStartInfo(checkedPath.Path){UseShellExecute=true});
        }catch(Exception ex){Toast(global::Orbit.Core.Localization.L.Get("Text_826109A90B1B")+ex.Message);}
        finally{_popupInteractionDepth--;ArmPopupDismissal();}
    }
    private static readonly HashSet<string> SafeResultExtensions=new(StringComparer.OrdinalIgnoreCase){".png",".jpg",".jpeg",".webp",".avif",".bmp",".tif",".tiff",".gif",".pdf",".txt",".zip",".7z",".tar",".tgz",".gz",".mp4",".webm",".mp3",".wav",".flac",".m4a",".opus",".ogg"};
}
