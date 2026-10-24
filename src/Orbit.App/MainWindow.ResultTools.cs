using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Orbit.App.Controls;
using Orbit.App.History;
using Orbit.App.Platform;
using Orbit.Core;
using Orbit.Core.Comparison;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private enum JobListFilter { All,Active,Succeeded,Failed,Cancelled,Interrupted }
    private JobListFilter _jobListFilter;
    private string _jobSearch="";
    private StackPanel? _jobsCards;
    private ComboBox? _jobFilterPicker;
    private TextBox? _jobSearchBox;
    private readonly ImageComparisonEngine _comparisonEngine=new();
    private CancellationTokenSource? _comparisonCancellation;
    private int _comparisonVersion;
    private readonly CancellationTokenSource _resultToolsCancellation=new();
    private readonly List<Task> _resultToolTasks=new();
    private ResultClipboard? _resultClipboard;
    private ImageCompareView? _comparisonView;

    private void AddJobFilters()
    {
        var controls=new StackPanel{Margin=new Thickness(0,3,0,7)};
        _jobFilterPicker=new ComboBox{ItemsSource=new[]{global::Orbit.Core.Localization.L.Get("Text_215816BF4210"),global::Orbit.Core.Localization.L.Get("Text_59A28F1B11DF"),global::Orbit.Core.Localization.L.Get("Text_4E591061E60B"),global::Orbit.Core.Localization.L.Get("Text_BB095483D8F2"),global::Orbit.Core.Localization.L.Get("Text_ED3A2D2E787B"),global::Orbit.Core.Localization.L.Get("Text_51D053A79E4D")},SelectedIndex=(int)_jobListFilter,Margin=new Thickness(0,3,6,5)};
        _jobSearchBox=new TextBox{Text=_jobSearch,MaxLength=200,Height=36,ToolTip=global::Orbit.Core.Localization.L.Get("Text_FBC617DD8074"),Margin=new Thickness(0,3,0,5),Padding=new Thickness(8,4,8,4)};
        controls.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_429A4506B152"),12));var searchRow=new Grid();searchRow.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(130)});searchRow.ColumnDefinitions.Add(new ColumnDefinition());searchRow.Children.Add(_jobFilterPicker);searchRow.Children.Add(_jobSearchBox);Grid.SetColumn(_jobSearchBox,1);controls.Children.Add(searchRow);
        var reset=Btn(global::Orbit.Core.Localization.L.Get("Text_CD45EC7836B5"),(_,_)=>{_jobListFilter=JobListFilter.All;_jobSearch="";_jobFilterPicker.SelectedIndex=0;_jobSearchBox.Text="";RenderJobs();});
        reset.FontSize=11;reset.Margin=new Thickness(0,0,0,4);controls.Children.Add(reset);
        _panelContent.Children.Add(controls);_jobsCards=new StackPanel();_panelContent.Children.Add(_jobsCards);
        _jobFilterPicker.SelectionChanged+=(_,_)=>{_jobListFilter=(JobListFilter)Math.Max(0,_jobFilterPicker.SelectedIndex);RenderJobs();};
        _jobSearchBox.TextChanged+=(_,_)=>{_jobSearch=_jobSearchBox.Text;RenderJobs();};
    }
    private IEnumerable<JobEntry> FilteredJobs()
    {
        var search=_jobSearch.Trim();
        return _jobs.Where(job=>{
            bool matches=_jobListFilter switch{
                JobListFilter.Active=>!job.Finished,
                JobListFilter.Succeeded=>job.Finished&&job.HistoryStatus==HistoryStatus.Succeeded,
                JobListFilter.Failed=>job.Finished&&job.HistoryStatus==HistoryStatus.Failed,
                JobListFilter.Cancelled=>job.Finished&&job.HistoryStatus==HistoryStatus.Cancelled,
                JobListFilter.Interrupted=>job.Finished&&job.HistoryStatus==HistoryStatus.Interrupted,
                _=>true
            };
            return matches&&(search.Length==0||job.InputPaths.Any(path=>path.Contains(search,StringComparison.OrdinalIgnoreCase))
                ||job.Path.Contains(search,StringComparison.OrdinalIgnoreCase)||job.Result?.OutputPath.Contains(search,StringComparison.OrdinalIgnoreCase)==true
                ||ActionName(job.Options.Action).Contains(search,StringComparison.OrdinalIgnoreCase));
        });
    }

    private void AddResultToolButtons(WrapPanel buttons,JobEntry job)
    {
        if(job.Result==null)return;
        if(job.Download is not null||job.DownloadWorkspaceEdit)buttons.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_59792556FD06"),(_,_)=>ShowDownloadJob(job)));
        var path=job.Result.OutputPath;
        var copy=Btn(global::Orbit.Core.Localization.L.Get("Text_1ECDDF40BFEB"),async(_,_)=>await CopyKnownResultAsync(path,false));copy.ToolTip=global::Orbit.Core.Localization.L.Get("Text_BBF25AF162E9");buttons.Children.Add(copy);
        var text=Btn(global::Orbit.Core.Localization.L.Get("Text_472631F53A00"),async(_,_)=>await CopyKnownResultAsync(path,true));text.ToolTip=global::Orbit.Core.Localization.L.Get("Text_C28D247933A1");buttons.Children.Add(text);
        if(CanCompare(job))buttons.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_83B70468CD60"),async(_,_)=>await ShowComparisonAsync(job)));
    }
    private static bool CanCompare(JobEntry job)=>job.Finished&&job.Result is {Width:>0,Height:>0}&&job.InputPaths.Count==1
        &&job.Options.Action is ImageAction.Jpg or ImageAction.Png or ImageAction.Bmp or ImageAction.Tiff or ImageAction.Gif or ImageAction.Ico or ImageAction.Svg or ImageAction.WebP or ImageAction.Avif or ImageAction.Crop or ImageAction.Resize or ImageAction.Compress or ImageAction.ImageTransform or ImageAction.RemoveImageMetadata or ImageAction.ImageChain or ImageAction.ImageWatermark;

    private Task CopyKnownResultAsync(string path,bool asText)
    {
        if(_closing)return Task.CompletedTask;
        if(IsDiagnosticRun&&(_resultClipboard==null||_resultClipboard.UsesSystemClipboard)){Toast(global::Orbit.Core.Localization.L.Get("Text_D3889B8382F1"));return Task.CompletedTask;}
        _resultClipboard??=new ResultClipboard(Dispatcher);
        return TrackResultTask(CopyResultCoreAsync(path,asText));
    }
    private async Task CopyResultCoreAsync(string path,bool asText)
    {
        _popupInteractionDepth++;
        try{
            var result=asText?await _resultClipboard!.CopyPathTextAsync(new[]{path},_resultToolsCancellation.Token)
                :await _resultClipboard!.CopyFilesAsync(new[]{path},_resultToolsCancellation.Token);
            if(_closing||result.Cancelled)return;
            if(result.Success)Toast(asText?global::Orbit.Core.Localization.L.Get("Text_05A9E4BA85C1"):global::Orbit.Core.Localization.L.Get("Text_F9CA1A64E0AE"));
            else Toast(result.Error??global::Orbit.Core.Localization.L.Get("Text_93477EC02B1D"));
        }catch(OperationCanceledException){}
        catch(Exception ex){if(!_closing)Toast(global::Orbit.Core.Localization.L.Get("Text_14135B84FE8D")+ex.Message);}
        finally{_popupInteractionDepth--;ArmPopupDismissal();}
    }
    private Task TrackResultTask(Task task)
    {
        _resultToolTasks.RemoveAll(item=>item.IsCompleted);_resultToolTasks.Add(task);return task;
    }
    private void StopComparison()
    {
        StopDownloadPresentation();
        _toolSearchBox=null;
        StopToolkitDiagnostics();
        StopWatermarkTools();
        StopSevenZipBrowser();
        StopAudioTagsInspection();
        StopArchiveIntegrity();
        StopTrackInspection();
        StopMediaLab();
        StopZipBrowser();
        ++_comparisonVersion;_comparisonCancellation?.Cancel();_comparisonView=null;StopMediaInspection();
    }
    private Task ShowComparisonAsync(JobEntry job)
    {
        if(!CanCompare(job)){Toast(global::Orbit.Core.Localization.L.Get("Text_FBC8D33E1B2E"));return Task.CompletedTask;}
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_39CA1CAAFD2A"),global::Orbit.Core.Localization.L.Get("Text_F8284C131E4E")+Path.GetFileName(job.Result!.OutputPath));
        var loading=Text(global::Orbit.Core.Localization.L.Get("Text_E8760D87C873"),13);_panelContent.Children.Add(loading);
        var cancellation=CancellationTokenSource.CreateLinkedTokenSource(_resultToolsCancellation.Token);_comparisonCancellation=cancellation;
        int version=++_comparisonVersion;
        return TrackResultTask(LoadComparisonAsync(job,loading,version,cancellation));
    }
    private async Task LoadComparisonAsync(JobEntry job,TextBlock loading,int version,CancellationTokenSource cancellation)
    {
        try{
            var compared=await _comparisonEngine.LoadAsync(job.InputPaths[0],job.Result!.OutputPath,1400,cancellation.Token);
            if(_closing||version!=_comparisonVersion||cancellation.IsCancellationRequested)return;
            var before=ComparisonBitmap(compared.BeforePng);var after=ComparisonBitmap(compared.AfterPng);
            _panelContent.Children.Remove(loading);
            _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Format("Text_167A8688F647",(object?)(compared.Original.Width),(object?)(compared.Original.Height),(object?)(FormatBytes(compared.Original.Bytes))),12));
            _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Format("Text_30447C8C71C9",(object?)(compared.Result.Width),(object?)(compared.Result.Height),(object?)(FormatBytes(compared.Result.Bytes))),12));
            _comparisonView=new ImageCompareView{BeforeSource=before,AfterSource=after,Height=250,Margin=new Thickness(0,4,0,8)};_panelContent.Children.Add(_comparisonView);
            _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_7FE8525C53E2"),11));
            var actions=new WrapPanel();actions.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_A99564C5702F"),async(_,_)=>await OpenKnownResultAsync(job.Result.OutputPath,true)));
            actions.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_1ECDDF40BFEB"),async(_,_)=>await CopyKnownResultAsync(job.Result.OutputPath,false)));
            actions.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_472631F53A00"),async(_,_)=>await CopyKnownResultAsync(job.Result.OutputPath,true)));_panelContent.Children.Add(actions);
        }catch(OperationCanceledException){}
        catch(Exception ex){if(!_closing&&version==_comparisonVersion){loading.Text=global::Orbit.Core.Localization.L.Get("Text_690BB13124EC")+ex.Message;Toast(ex.Message);}}
        finally{if(ReferenceEquals(_comparisonCancellation,cancellation))_comparisonCancellation=null;cancellation.Dispose();}
    }
    private static BitmapSource ComparisonBitmap(byte[] png)
    {
        using var stream=new MemoryStream(png,false);var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;
        bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();return bitmap;
    }
}
