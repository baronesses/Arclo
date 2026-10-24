using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Orbit.App.Controls;
using Orbit.App.Platform;
using Orbit.Core;
using Orbit.Core.Downloads;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private readonly DownloadEngine _downloadEngine=new();
    private readonly ActiveBrowserUrlReader _browserReader=new();
    private bool _browserContext;
    private string? _browserUrl;
    private Func<IntPtr,bool>? _browserWindowOverride;
    private Func<IntPtr,Task<BrowserUrlResult>>? _browserUrlOverride;
    private Func<DownloadRequest,IProgress<DownloadProgress>,CancellationToken,Task<DownloadResult>>? _downloadOverride;

    private bool IsBrowserInvocation(IntPtr handle)=>IsDiagnosticRun?_browserWindowOverride?.Invoke(handle)==true:ActiveBrowserUrlReader.IsSupportedBrowserWindow(handle);
    private async Task<BrowserUrlResult> ReadBrowserUrlAsync(IntPtr handle)
    {
        try{return IsDiagnosticRun&&_browserUrlOverride is{} reader?await reader(handle):await _browserReader.ReadAsync(handle);}
        catch(Exception){return new BrowserUrlResult(null,global::Orbit.Core.Localization.L.Get("Text_CE11CCD4340B"));}
    }

    private void PublishBrowserContext(BrowserUrlResult result)
    {
        _selectionLoading=false;_browserContext=true;_browserUrl=result.Url;
        _radial.SetActions(new[]{
            new RadialAction(ImageAction.DownloadAudio,global::Orbit.Core.Localization.L.Get("Text_419217EA9537"),global::Orbit.Core.Localization.L.Get("Text_8AF20D86F1DB")),
            new RadialAction(ImageAction.DownloadVideo,global::Orbit.Core.Localization.L.Get("Text_3D47D65B63BA"),global::Orbit.Core.Localization.L.Get("Text_E2CD2A0F3660")),
            new RadialAction(ImageAction.MoreTools,global::Orbit.Core.Localization.L.Get("Text_4E90074F740B"),global::Orbit.Core.Localization.L.Get("Text_AD2BDA03F10A"))});
        _radial.SetAvailable(true);
        _radial.FileName.Text=result.Url is{} url?new Uri(url).Host:global::Orbit.Core.Localization.L.Get("Text_088E931CE654");
        _radial.CenterHint.Text=result.Url is null?global::Orbit.Core.Localization.L.Get("Text_29E34E7169E6"):global::Orbit.Core.Localization.L.Get("Text_3B7DBF1A8593");
        _radial.Metadata.Text=global::Orbit.Core.Localization.L.Get("Text_8FF93B185CD8");
        _radial.CenterCard.ToolTip=result.Url??global::Orbit.Core.Localization.L.Get("Text_6F3B9CE89ED9");
        _status.Text=result.Url is null?global::Orbit.Core.Localization.L.Get("Text_1F6686F1272D"):global::Orbit.Core.Localization.L.Get("Text_BACB30E4DD99")+new Uri(result.Url).Host;
        if(result.Url is null)Toast(result.Error??global::Orbit.Core.Localization.L.Get("Text_A5722E762E02"));
    }

    private void StartBrowserDownload(DownloadMode mode)
    {
        if(_selectionLoading||_closing)return;
        if(_browserUrl is null){ShowDownloadPanel(mode);return;}
        try{EnqueueDownload(BuildDownloadRequest(_browserUrl,mode));}
        catch(Exception ex){Toast(ex.Message);}
    }

    private void ShowDownloadPanel(DownloadMode mode=DownloadMode.AudioMp3,string? url=null)
    {
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_1CAB33D5166B"),global::Orbit.Core.Localization.L.Get("Text_0C19FCC8E1BC"));
        int version=_comparisonVersion;
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_A51487B3ADB5"),13));
        var address=new TextBox{Text=url??_browserUrl??"",Tag="DownloadUrl",MaxLength=8192,MinHeight=38,Padding=new Thickness(8,7,8,7),Margin=new Thickness(0,2,0,8)};
        _panelContent.Children.Add(address);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_853401A15656"),12));
        var format=new ComboBox{Tag="DownloadMode",ItemsSource=new[]{global::Orbit.Core.Localization.L.Get("Text_D5B92BB0EC9C"),global::Orbit.Core.Localization.L.Get("Text_49758AF81C1A")},SelectedIndex=mode==DownloadMode.AudioMp3?0:1,Margin=new Thickness(0,6,0,8)};
        _panelContent.Children.Add(format);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_A77D5C4ECCAA"),13));
        var folder=Text(DownloadDirectory(),12);_panelContent.Children.Add(folder);
        _panelContent.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_582804FBABC5"),(_,_)=>{
            if(IsDiagnosticRun||_closing||version!=_comparisonVersion)return;
            var dialog=new OpenFolderDialog{Title=global::Orbit.Core.Localization.L.Get("Text_25183FC7A623"),InitialDirectory=DownloadDirectory()};
            if(WithPopupInteraction(()=>dialog.ShowDialog(this))==true&&version==_comparisonVersion){_outputDirectory=dialog.FolderName;folder.Text=dialog.FolderName;SavePreferences();}
        }));
        var error=Text("",12,"#EDACB9");error.Tag="DownloadError";error.Visibility=Visibility.Collapsed;_panelContent.Children.Add(error);
        var run=Btn(global::Orbit.Core.Localization.L.Get("Text_889F19BDED8E"),(_,_)=>{
            if(_closing||version!=_comparisonVersion)return;
            try{
                var request=BuildDownloadRequest(address.Text,format.SelectedIndex==0?DownloadMode.AudioMp3:DownloadMode.VideoMp4);
                EnqueueDownload(request);
            }catch(Exception ex){error.Text=ex.Message;error.Visibility=Visibility.Visible;}
        });run.Tag="DownloadStart";GlassAccent(run);_panelContent.Children.Add(run);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_155C0C927C1C"),12));
    }

    private DownloadRequest BuildDownloadRequest(string text,DownloadMode mode)
    {
        text=text.Trim();
        if(text.Length is <1 or >8192||text.Any(char.IsControl)||!Uri.TryCreate(text,UriKind.Absolute,out var uri)
            ||uri.Scheme is not("http" or "https")||string.IsNullOrEmpty(uri.Host)||uri.UserInfo.Length!=0)
            throw new InvalidOperationException(global::Orbit.Core.Localization.L.Get("Text_FC829F3D0B30"));
        if(!Enum.IsDefined(mode))throw new InvalidOperationException(global::Orbit.Core.Localization.L.Get("Text_79D50C6E72C0"));
        return DownloadEngine.Normalize(new DownloadRequest{Url=uri.AbsoluteUri,Mode=mode,OutputDirectory=Path.GetFullPath(DownloadDirectory())});
    }

    private string DownloadDirectory()
    {
        if(_outputDirectory is not null)return _outputDirectory;
        if(IsDiagnosticRun)return Path.Combine(Path.GetTempPath(),"Downloads");
        var id=new Guid("374DE290-123F-4565-9164-39C4925E467B");
        IntPtr path=IntPtr.Zero;
        try{if(SHGetKnownFolderPath(ref id,0,IntPtr.Zero,out path)==0&&Marshal.PtrToStringUni(path) is{} value)return value;}
        catch(Exception ex)when(ex is ExternalException or DllNotFoundException){ }
        finally{if(path!=IntPtr.Zero)Marshal.FreeCoTaskMem(path);}
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads");
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid folder,uint flags,IntPtr token,out IntPtr path);

    private static string JobCaption(JobEntry job)=>job.Download is{} download?new Uri(download.Url).Host+" · "+(download.Mode==DownloadMode.AudioMp3?"MP3":"MP4"):Path.GetFileName(job.Path);
    private void EnqueueDownload(DownloadRequest request)
    {
        if(_closing)return;
        var options=new ImageJobOptions{Action=request.Mode==DownloadMode.AudioMp3?ImageAction.DownloadAudio:ImageAction.DownloadVideo,OutputDirectory=request.OutputDirectory};
        var job=new JobEntry(request.Url,options){Download=request,InputPaths=Array.Empty<string>(),PopupSession=_compact?_popupSession:null,State=_queueRunControl.IsPaused?global::Orbit.Core.Localization.L.Get("Text_AA8D8A7478F9"):global::Orbit.Core.Localization.L.Get("Text_B22A2454BD69")};
        _jobs.Add(job);ShowRadialJob(job);_jobTasks.RemoveAll(task=>task.IsCompleted);ScheduleHistorySave();UpdateJobsButton();_jobTasks.Add(RunJobAsync(job));RefreshJobs();
    }

    private async Task<ImageJobResult> ExecuteDownloadAsync(JobEntry job)
    {
        var progress=new Progress<DownloadProgress>(value=>{
            if(job.Finished||job.Result is not null||job.HistoryStatus!=History.HistoryStatus.Running||job.Cancellation.IsCancellationRequested)return;
            job.Progress=value.Fraction is{} fraction?Math.Clamp(fraction,0,1):null;job.State=value.Stage;
            job.DownloadSpeed=value.SpeedBytesPerSecond;job.DownloadEta=value.EtaSeconds;
            RefreshJobs();if(IsCurrentPopupJob(job))_radial.CenterHint.Text=value.Fraction is{} p?global::Orbit.Core.Localization.L.Format("Text_859561F3CA87",(object?)(p)):value.Stage;
        });
        var result=IsDiagnosticRun&&_downloadOverride is{} execute?await execute(job.Download!,progress,job.Cancellation.Token)
            :await _downloadEngine.DownloadAsync(job.Download!,progress,job.Cancellation.Token);
        return new ImageJobResult(result.OutputPath,result.Bytes,0,0,result.Format,0);
    }

    private async Task CopyDownloadedFileAsync(JobEntry job)
    {
        try{
            if(IsDiagnosticRun&&(_resultClipboard is null||_resultClipboard.UsesSystemClipboard)){job.Detail+=global::Orbit.Core.Localization.L.Get("Text_2CC4081424CC");return;}
            _resultClipboard??=new ResultClipboard(Dispatcher);
            // A completed file stays successful if the clipboard is busy or cancellation arrives after publication.
            var copied=await _resultClipboard.CopyFilesAsync(new[]{job.Result!.OutputPath},_resultToolsCancellation.Token);
            job.AutomaticCopySucceeded=copied.Success;
            job.Detail+=copied.Success?global::Orbit.Core.Localization.L.Get("Text_9993E17FB13E"):global::Orbit.Core.Localization.L.Get("Text_674AD895216C");
        }catch(Exception ex){job.AutomaticCopySucceeded=false;job.Detail+=global::Orbit.Core.Localization.L.Get("Text_5CA99659BE46");if(!_closing)App.Log(ex);}
    }
}
