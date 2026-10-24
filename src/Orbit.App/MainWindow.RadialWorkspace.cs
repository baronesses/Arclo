using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Orbit.App.Controls;
using Orbit.App.History;
using Orbit.App.Platform;
using Orbit.Core;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private string? _radialWorkspacePath;
    private ToolkitMetadata? _radialWorkspaceMetadata;
    private JobEntry? _radialWorkspaceJob;
    private JobEntry? _radialTerminalRendered;
    private bool _radialWorkspaceVisible, _radialConversionMenu;
    private int _radialWorkspaceVersion;
    private string? _radialPublishedPath;
    private CancellationTokenSource? _radialWorkspaceCancellation;
    private Task _radialWorkspaceTask=Task.CompletedTask;
    private Func<string,Task<string?>>? _radialSaveAsPathOverride;

    private void ResetRadialWorkspace()
    {
        SuspendRadialWorkspace();_radialWorkspacePath=null;_radialWorkspaceMetadata=null;
        _radialWorkspaceJob=null;_radialTerminalRendered=null;_radialPublishedPath=null;_radialConversionMenu=false;
    }
    private void SuspendRadialWorkspace()
    {
        ++_radialWorkspaceVersion;_radialWorkspaceVisible=false;
        _radialWorkspaceCancellation?.Cancel();_radialWorkspaceCancellation=null;_radial.SetProgress(false);
        if(_radialWorkspaceMetadata is null)_radialPublishedPath=null;
    }
    private bool RadialWorkspaceCurrent(int version)=>!_closing&&version==_radialWorkspaceVersion
        &&_radialWorkspaceVisible&&IsVisible&&WindowState!=WindowState.Minimized&&_panel.Visibility!=Visibility.Visible;

    private async Task PasteIntoRadialAsync()
    {
        if(_closing||IsDiagnosticRun)return; // Diagnostics inject a payload; they never read the user's clipboard.
        await ApplyClipboardPayloadAsync(new RadialClipboardReader().Read());
    }
    private async Task ApplyClipboardPayloadAsync(RadialClipboardPayload payload)
    {
        if(_closing)return;
        if(payload.Error is{} error){Toast(error);return;}
        if(payload.Paths.Count>0)
        {
            InvalidatePendingInvocation();++_popupSession;
            var loading=LoadFilesAsync(payload.Paths,true);int load=_loadVersion,invocation=_invocationVersion;
            if(await loading&&!_closing&&load==_loadVersion&&invocation==_invocationVersion&&_files.Count==1&&_metadata is{Kind:not ToolkitKind.Folder} metadata)
            {
                _radialWorkspacePath=_files[0];_radialWorkspaceMetadata=metadata;
                _radialWorkspaceVisible=true;_radialConversionMenu=false;RenderRadialFile();
            }
            return;
        }
        if(payload.Url is{} url)
        {
            InvalidatePendingInvocation();++_popupSession;ClearSelection();
            PublishBrowserContext(new BrowserUrlResult(url,null));return;
        }
        Toast(global::Orbit.Core.Localization.L.Get("Text_3BF6D171F280"));
    }
    private static bool IsClipboardTextTarget()=>Keyboard.FocusedElement is TextBoxBase
        ||Keyboard.FocusedElement is ComboBox{IsEditable:true};

    private void RadialCenterSelected()
    {
        if(_radialWorkspaceVisible)
        {
            if(_radialConversionMenu){_radialConversionMenu=false;RenderRadialFile();return;}
            if(_radialWorkspaceJob is{Finished:false})return;
            _radial.CenterHint.Text=global::Orbit.Core.Localization.L.Get("Text_2910977DE8CA");return;
        }
        if(_compact&&_lastPopupOutput is{} output)OpenResult(output,true);
        else if(_browserContext)ShowDownloadPanel();else ChooseFiles();
    }
    private void ShowRadialJob(JobEntry job)
    {
        if(_closing)return;
        // Closing a card releases its player. The existing file and preview stay in the circle.
        ClosePanel();SuspendRadialWorkspace();
        _radialWorkspaceJob=job;_radialTerminalRendered=null;_radialPublishedPath=null;_radialWorkspaceVisible=true;_radialConversionMenu=false;
        if(_radialWorkspacePath is null&&job.Download is null&&job.InputPaths.Count==1)
        {
            _radialWorkspacePath=job.InputPaths[0];
            if(_metadata?.Path==_radialWorkspacePath)_radialWorkspaceMetadata=_metadata;
        }
        if(_radialWorkspacePath is null)
        {
            _radial.FileName.Text=job.Download is{} request?new Uri(request.Url).Host:Path.GetFileName(job.Path);
            _radial.Preview.Source=_emptyPreview;
        }
        RenderRadialFile();RefreshRadialWorkspace();
    }
    private void TryRestoreRadialWorkspace()
    {
        if(_closing||(_radialWorkspacePath is null&&_radialWorkspaceJob is null)||!IsVisible||WindowState==WindowState.Minimized)return;
        _radialWorkspaceVisible=true;_radialConversionMenu=false;RenderRadialFile();RefreshRadialWorkspace();
    }
    private IReadOnlyList<RadialAction> RadialFormats()
    {
        static RadialAction A(ImageAction action,string label)=>new(action,label,global::Orbit.Core.Localization.L.Get("Text_BFD5140E5FCC")+label);
        return _radialWorkspaceMetadata?.Kind switch
        {
            ToolkitKind.Image=>new[]{A(ImageAction.Jpg,"JPG"),A(ImageAction.Png,"PNG"),new RadialAction(ImageAction.Svg,"SVG",global::Orbit.Core.Localization.L.Get("Text_25C0041FCBE3"))},
            ToolkitKind.Audio=>new[]{A(ImageAction.AudioMp3,"MP3"),A(ImageAction.AudioWav,"WAV"),A(ImageAction.AudioFlac,"FLAC")},
            ToolkitKind.Video=>new[]{A(ImageAction.VideoMp4,"MP4"),A(ImageAction.VideoWebm,"WebM"),
                _radialWorkspaceMetadata.HasAudio?A(ImageAction.ExtractMp3,"MP3"):A(ImageAction.VideoFrame,"PNG")},
            ToolkitKind.Pdf=>new[]{A(ImageAction.PdfPng,"PNG"),A(ImageAction.PdfJpg,"JPG"),A(ImageAction.PdfText,"TXT")},
            _=>Array.Empty<RadialAction>()
        };
    }
    private void RenderRadialFile()
    {
        if(!_radialWorkspaceVisible)return;
        bool busy=_radialWorkspaceJob is{Finished:false};
        static RadialAction C(RadialCommand command,string label,string help)=>new(ImageAction.MoreTools,label,help){Command=command};
        if(_radialConversionMenu&&!busy)
        {
            var formats=RadialFormats();if(formats.Count>=3)_radial.SetActions(formats.Concat(new[]{
                C(RadialCommand.Convert,global::Orbit.Core.Localization.L.Get("Text_1A9FB1F3CF8E"),global::Orbit.Core.Localization.L.Get("Text_33733D7C4FAF")),
                C(RadialCommand.More,global::Orbit.Core.Localization.L.Get("Text_2071FB990504"),global::Orbit.Core.Localization.L.Get("Text_5780676F9A0C"))}).ToArray());
            else _radialConversionMenu=false;
        }
        if(!_radialConversionMenu)
            _radial.SetActions(new[]{
                C(RadialCommand.Convert,Orbit.Core.Localization.L.Get("Command.Convert"),global::Orbit.Core.Localization.L.Get("Text_734514BE4DE4")),
                C(RadialCommand.SaveAs,Orbit.Core.Localization.L.Get("Command.SaveAs"),global::Orbit.Core.Localization.L.Get("Text_20C3501AE17D")),
                C(RadialCommand.Copy,Orbit.Core.Localization.L.Get("Command.Copy"),global::Orbit.Core.Localization.L.Get("Text_5BBCC5B7EB88")),
                busy?C(RadialCommand.Cancel,global::Orbit.Core.Localization.L.Get("Text_8FBE9B75CBDF"),global::Orbit.Core.Localization.L.Get("Text_4CC4E4FB6B37"))
                    :_radialWorkspacePath is null&&_radialWorkspaceJob is{Finished:true,Result:null}
                        ?C(RadialCommand.Retry,global::Orbit.Core.Localization.L.Get("Text_5189135A6110"),global::Orbit.Core.Localization.L.Get("Text_F3B9891A36FE"))
                        :C(RadialCommand.Trim,Orbit.Core.Localization.L.Get("Command.Trim"),global::Orbit.Core.Localization.L.Get("Text_3FB6271137EA")),
                C(RadialCommand.More,global::Orbit.Core.Localization.L.Get("Text_2071FB990504"),global::Orbit.Core.Localization.L.Get("Text_E46E3468BAEC"))});
        _radial.SetAvailable(true);_keyboardIndex=-1;
        foreach(var sector in _radial.Children.OfType<SectorButton>())
            sector.IsEnabled=sector.CommandKind switch
            {
                RadialCommand.Convert=>!busy&&RadialFormats().Count>=3,
                RadialCommand.Copy or RadialCommand.SaveAs=>!busy&&_radialWorkspacePath is not null,
                RadialCommand.Trim=>!busy&&_radialWorkspaceMetadata?.Kind is ToolkitKind.Image or ToolkitKind.Audio or ToolkitKind.Video,
                _=>true
            };
        if(_radialWorkspacePath is{} path)
        {
            _radial.FileName.Text=Path.GetFileName(path);_radial.CenterCard.ToolTip=path;
        }
        _radial.CenterHint.Text=_radialConversionMenu?global::Orbit.Core.Localization.L.Get("Text_7847923E4494"):global::Orbit.Core.Localization.L.Get("Text_73BD5DBB84F5");
        _radial.Metadata.Text=_radialWorkspaceMetadata is{} metadata?ShortMetadata(metadata):global::Orbit.Core.Localization.L.Get("Text_AE67094F22ED");
        _radial.SetProgress(busy,_radialWorkspaceJob?.Progress,global::Orbit.Core.Localization.L.Get("Text_2812E78A3C4A"));
    }
    private void RefreshRadialWorkspace()
    {
        if(!_radialWorkspaceVisible||!IsVisible||WindowState==WindowState.Minimized||_panel.Visibility==Visibility.Visible)return;
        var job=_radialWorkspaceJob;if(job is null)return;
        if(!job.Finished)
        {
            _radial.SetProgress(true,job.Progress,job.State);
            var percent=job.Progress is{} p?$"{p:P0}":job.HistoryStatus==HistoryStatus.Queued?global::Orbit.Core.Localization.L.Get("Text_B22A2454BD69"):"…";
            string eta=job.DownloadEta is>=0 and{} seconds&&double.IsFinite(seconds)?" · "+TimeSpan.FromSeconds(Math.Min(seconds,86400)).ToString(seconds>=3600?@"h\:mm\:ss":@"m\:ss"):"";
            _radial.CenterHint.Text=percent+eta;_radial.Metadata.Text=job.State;return;
        }
        _radial.SetProgress(false);
        if(job.HistoryStatus==HistoryStatus.Succeeded&&job.Result is{} result)
        {
            if(_radialPublishedPath==result.OutputPath)return;
            _radialPublishedPath=result.OutputPath;
            _radialWorkspacePath=result.OutputPath;_radialWorkspaceMetadata=null;_radialConversionMenu=false;
            _lastPopupOutput=null;RenderRadialFile();_radial.CenterHint.Text=global::Orbit.Core.Localization.L.Get("Text_EF05D57959CF");
            int version=++_radialWorkspaceVersion;
            var cancellation=CancellationTokenSource.CreateLinkedTokenSource(_resultToolsCancellation.Token);
            _radialWorkspaceCancellation=cancellation;
            _radialWorkspaceTask=TrackResultTask(LoadRadialResultAsync(job,version,cancellation));
        }
        else if(job.HistoryStatus!=HistoryStatus.Succeeded)
        {
            if(!ReferenceEquals(_radialTerminalRendered,job)){RenderRadialFile();_radialTerminalRendered=job;}
            _radial.CenterHint.Text=job.State;_radial.Metadata.Text=global::Orbit.Core.Localization.L.Get("Text_74F861AF3A5B");
            _radial.CenterCard.ToolTip=job.Detail;
        }
    }
    private bool IsRadialJobViewCurrent(JobEntry job)=>_radialWorkspaceVisible&&ReferenceEquals(job,_radialWorkspaceJob)
        &&IsVisible&&WindowState!=WindowState.Minimized&&_panel.Visibility!=Visibility.Visible;
    private async Task LoadRadialResultAsync(JobEntry job,int version,CancellationTokenSource cancellation)
    {
        try
        {
            var path=job.Result!.OutputPath;
            var metadata=await InspectSelectionAsync(path,cancellation.Token);
            if(!string.Equals(Path.GetFullPath(metadata.Path),Path.GetFullPath(path),StringComparison.OrdinalIgnoreCase)
                ||metadata.Kind is ToolkitKind.Audio or ToolkitKind.Video&&(!double.IsFinite(metadata.DurationSeconds)||metadata.DurationSeconds is<=0 or>86400))
                throw new InvalidOperationException(global::Orbit.Core.Localization.L.Get("Text_B49C75996A0D"));
            var preview=await Task.Run(async()=>
            {
                var png=await _engine.CreatePreviewAsync(path,480,cancellation.Token);
                if(png is null)return null;
                var image=new BitmapImage();using var stream=new MemoryStream(png);
                image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.StreamSource=stream;image.EndInit();image.Freeze();return image;
            },cancellation.Token);
            if(cancellation.IsCancellationRequested||!RadialWorkspaceCurrent(version)||!ReferenceEquals(job,_radialWorkspaceJob))return;
            _radialWorkspaceMetadata=metadata;_radial.Preview.Source=preview??_emptyPreview;RenderRadialFile();
            if(job.AutomaticCopySucceeded==false)_radial.CenterHint.Text=global::Orbit.Core.Localization.L.Get("Text_620B070258FE");
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(RadialWorkspaceCurrent(version)){_radial.CenterHint.Text=global::Orbit.Core.Localization.L.Get("Text_43985E59A3FF");_radial.Metadata.Text="Copy · Save as";_radial.CenterCard.ToolTip=ex.Message;}}
        finally{if(ReferenceEquals(_radialWorkspaceCancellation,cancellation))_radialWorkspaceCancellation=null;cancellation.Dispose();}
    }
    private bool TryRadialFormat(ImageAction action)
    {
        if(!_radialWorkspaceVisible||!_radialConversionMenu)return false;
        if(_radialWorkspacePath is not{} path||!RadialFormats().Any(x=>x.Action==action))return true;
        _radialConversionMenu=false;
        EnqueueDownloadEdit(path,new ImageJobOptions{Action=action,Quality=action==ImageAction.Avif?65:85,OutputDirectory=_outputDirectory??Path.GetDirectoryName(path)});
        return true;
    }
    private async Task RunRadialCommandAsync(RadialCommand command)
    {
        if(!_radialWorkspaceVisible||_closing||_panel.Visibility==Visibility.Visible)return;
        if(command==RadialCommand.Cancel)
        {
            if(_radialWorkspaceJob is{Finished:false,Result:null} running){running.Cancellation.Cancel();running.State=global::Orbit.Core.Localization.L.Get("Text_CCF5E8C11886");RefreshJobs();}return;
        }
        if(command==RadialCommand.Retry)
        {
            if(_radialWorkspaceJob is{Finished:true,Download:{} request})EnqueueDownload(BuildDownloadRequest(request.Url,request.Mode));return;
        }
        if(command==RadialCommand.More)
        {
            if(_radialWorkspacePath is{} known)
            {
                if(await LoadRadialSelectionAsync(known))ShowMoreTools();
            }
            else ShowJobs();return;
        }
        if(_radialWorkspaceJob is{Finished:false}||_radialWorkspacePath is not{} path)return;
        if(command==RadialCommand.Convert)
        {
            if(RadialFormats().Count<3){_radial.CenterHint.Text=global::Orbit.Core.Localization.L.Get("Text_13BB0FCC8A54");return;}
            _radialConversionMenu=!_radialConversionMenu;RenderRadialFile();return;
        }
        if(command==RadialCommand.Copy)
        {
            int version=_radialWorkspaceVersion;
            if(IsDiagnosticRun&&(_resultClipboard is null||_resultClipboard.UsesSystemClipboard))return;
            _resultClipboard??=new ResultClipboard(Dispatcher);
            var result=await _resultClipboard.CopyFilesAsync(new[]{path},_resultToolsCancellation.Token);
            if(RadialWorkspaceCurrent(version)){_radial.CenterHint.Text=result.Success?global::Orbit.Core.Localization.L.Get("Text_E4A641507A65"):global::Orbit.Core.Localization.L.Get("Text_38147870F8A0");if(!result.Success)_radial.CenterCard.ToolTip=result.Error;}
            return;
        }
        if(command==RadialCommand.SaveAs){await SaveRadialFileAsAsync(path);return;}
        if(command==RadialCommand.Trim)
        {
            var metadata=_radialWorkspaceMetadata;if(metadata is null)return;
            if(metadata.Kind is ToolkitKind.Audio or ToolkitKind.Video)
            {
                var job=_radialWorkspaceJob is{Result:{} result} owner&&result.OutputPath==path?owner:
                    new JobEntry(path,new ImageJobOptions{Action=metadata.Kind==ToolkitKind.Audio?ImageAction.AudioTrim:ImageAction.VideoTrim})
                    {Result=new ImageJobResult(path,metadata.Bytes,metadata.Width,metadata.Height,metadata.Format,0),Finished=true,HistoryStatus=HistoryStatus.Succeeded,InputPaths=new[]{path}};
                BuildDownloadedMediaEditor(job,metadata);
            }
            else if(metadata.Kind==ToolkitKind.Image)
            {
                if(await LoadRadialSelectionAsync(path))DispatchAction(ImageAction.Crop);
            }
            else _radial.CenterHint.Text=global::Orbit.Core.Localization.L.Get("Text_5DCF9FC02D5E");
        }
    }
    private async Task<bool> LoadRadialSelectionAsync(string path)
    {
        var job=_radialWorkspaceJob;var published=_radialPublishedPath;
        var loading=LoadFilesAsync(new[]{path},true);int load=_loadVersion;
        if(!await loading||_closing||load!=_loadVersion||_files.Count!=1||!string.Equals(_files[0],path,StringComparison.OrdinalIgnoreCase))return false;
        _radialWorkspacePath=path;_radialWorkspaceMetadata=_metadata;_radialWorkspaceJob=job;_radialPublishedPath=published;
        return true;
    }
    private async Task SaveRadialFileAsAsync(string path)
    {
        int version=_radialWorkspaceVersion;string? destination;
        ++_popupInteractionDepth;
        try
        {
            if(IsDiagnosticRun)destination=_radialSaveAsPathOverride is{} choose?await choose(path):null;
            else
            {
                var extension=Path.GetExtension(path);
                var dialog=new SaveFileDialog{Title=global::Orbit.Core.Localization.L.Get("Text_6F6B1EA32FCB"),FileName=Path.GetFileNameWithoutExtension(path)+"_copy"+extension,
                    InitialDirectory=Path.GetDirectoryName(path),DefaultExt=extension,AddExtension=true,OverwritePrompt=false,
                    Filter=global::Orbit.Core.Localization.L.Get("Text_F361B706E263")+extension+")|*"+extension};
                destination=dialog.ShowDialog(this)==true?dialog.FileName:null;
            }
            if(destination is null)return;
            var saved=await SafeFileSaveAs.SaveAsync(path,destination,_resultToolsCancellation.Token);
            if(RadialWorkspaceCurrent(version))_radial.CenterHint.Text=global::Orbit.Core.Localization.L.Get("Text_E08F2709E3DA");
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(RadialWorkspaceCurrent(version)){_radial.CenterHint.Text=global::Orbit.Core.Localization.L.Get("Text_6D1E3F7C5F12");_radial.CenterCard.ToolTip=ex.Message;Toast(ex.Message);}}
        finally{--_popupInteractionDepth;ArmPopupDismissal();}
    }
}
