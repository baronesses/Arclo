using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Orbit.Core;
using Orbit.Core.Renaming;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private readonly RenameEngine _renameEngine=new();
    private RenamePlan? _preparedRenamePlan;
    private RenameReceipt? _lastRenameReceipt;
    private RenameException? _renameRecovery;
    private CancellationTokenSource? _renameCancellation;
    private Task _renameWork=Task.CompletedTask;
    private int _renamePreviewVersion;
    private void ShowRename()
    {
        if(_files.Count==0||_selectionLoading||_selectionMetadata.Any(m=>m.Kind==ToolkitKind.Folder)){Toast(global::Orbit.Core.Localization.L.Get("Text_C72B2E6D6785"));return;}
        if(!_renameWork.IsCompleted){Toast(global::Orbit.Core.Localization.L.Get("Text_A23F34E843C7"));return;}
        _preparedRenamePlan=null;int version=++_renamePreviewVersion;
        var sources=_files.ToArray();
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_194306E20807"),global::Orbit.Core.Localization.L.Format("Text_0B17D5C23C53",(object?)(sources.Length)));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_FDD533E90462"),12));
        TextBox Field(string title,string tag){_panelContent.Children.Add(Text(title,12));var box=new TextBox{Tag=tag,MaxLength=240,Margin=new Thickness(0,0,0,5)};_panelContent.Children.Add(box);return box;}
        var prefix=Field(global::Orbit.Core.Localization.L.Get("Text_60E6F8A4B506"),"rename-prefix");var suffix=Field(global::Orbit.Core.Localization.L.Get("Text_23B9BFD7105F"),"rename-suffix");
        var search=Field(global::Orbit.Core.Localization.L.Get("Text_814B79156A7F"),"rename-search");var replace=Field(global::Orbit.Core.Localization.L.Get("Text_6E4964D15BAC"),"rename-replacement");
        var numbered=Field(global::Orbit.Core.Localization.L.Get("Text_B959FA02AD5A"),"rename-base");
        var first=Field(global::Orbit.Core.Localization.L.Get("Text_C9A33ED9B822"),"rename-start");first.Text="1";
        var digits=Field(global::Orbit.Core.Localization.L.Get("Text_FE0F5C68557B"),"rename-digits");digits.Text="3";
        var preview=Btn(global::Orbit.Core.Localization.L.Get("Text_32060A34F5BE"),(_,_)=>{});preview.Margin=new Thickness(0,9,0,7);_panelContent.Children.Add(preview);
        var summary=Text(global::Orbit.Core.Localization.L.Get("Text_98BC126DAFA7"),12);_panelContent.Children.Add(summary);
        var table=new ListBox{Tag="rename-preview-table",Height=170,FontSize=12,BorderThickness=new Thickness(1),Margin=new Thickness(0,5,0,8)};
        table.SetResourceReference(Control.BackgroundProperty,"GlassFieldFill");table.SetResourceReference(Control.BorderBrushProperty,"GlassEdge");
        var row=new FrameworkElementFactory(typeof(StackPanel));
        var oldName=new FrameworkElementFactory(typeof(TextBlock));oldName.SetBinding(TextBlock.TextProperty,new Binding("OriginalName"));oldName.SetValue(TextBlock.ForegroundProperty,Ink("#B8C7DD"));row.AppendChild(oldName);
        var newName=new FrameworkElementFactory(typeof(TextBlock));newName.SetBinding(TextBlock.TextProperty,new Binding("NewName"));newName.SetValue(TextBlock.ForegroundProperty,Ink("#F5F8FF"));newName.SetValue(FrameworkElement.MarginProperty,new Thickness(0,1,0,7));row.AppendChild(newName);
        table.ItemTemplate=new DataTemplate{VisualTree=row};_panelContent.Children.Add(table);
        var apply=Btn(global::Orbit.Core.Localization.L.Get("Text_194306E20807"),(_,_)=>{});apply.IsEnabled=false;GlassAccent(apply);_panelContent.Children.Add(apply);
        if(_lastRenameReceipt is {IsUndone:false})_panelContent.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_4001A33D51D3"),(_,_)=>StartRenameUndo()));
        foreach(var box in new[]{prefix,suffix,search,replace,numbered,first,digits})box.TextChanged+=(_,_)=>{version=++_renamePreviewVersion;_preparedRenamePlan=null;apply.IsEnabled=false;summary.Text=global::Orbit.Core.Localization.L.Get("Text_C86F7BF26345");};
        preview.Click+=async(_,_)=>{
            if(!_renameWork.IsCompleted)return;
            int request=++_renamePreviewVersion;version=request;preview.IsEnabled=false;apply.IsEnabled=false;summary.Text=global::Orbit.Core.Localization.L.Get("Text_D6E464BE1D66");
            try{
                if(!int.TryParse(first.Text,out int start)||!int.TryParse(digits.Text,out int padding))throw new RenameException(global::Orbit.Core.Localization.L.Get("Text_5E8455026EEE"));
                var plan=await _renameEngine.CreatePlanAsync(sources,new RenameOptions{Prefix=prefix.Text,Suffix=suffix.Text,Search=search.Text,Replacement=replace.Text,NumberedBaseName=string.IsNullOrWhiteSpace(numbered.Text)?null:numbered.Text,StartNumber=start,Digits=padding});
                if(request!=_renamePreviewVersion||_closing)return;
                _preparedRenamePlan=plan;table.ItemsSource=plan.Items.Select(item=>new{OriginalName=Path.GetFileName(item.OriginalPath),NewName="→ "+Path.GetFileName(item.TargetPath)}).ToArray();
                summary.Text=global::Orbit.Core.Localization.L.Format("Text_38B5E54AE1AF",(object?)(plan.Items.Count(item=>item.HasChange)),(object?)(plan.Items.Count));apply.IsEnabled=plan.HasChanges;
                await Dispatcher.InvokeAsync(()=>{UpdateLayout();apply.BringIntoView();},System.Windows.Threading.DispatcherPriority.Loaded);
            }catch(Exception ex){if(request==_renamePreviewVersion){_preparedRenamePlan=null;summary.Text=ex.Message;}}
            finally{preview.IsEnabled=true;}
        };
        apply.Click+=(_,_)=>{if(_preparedRenamePlan!=null&&version==_renamePreviewVersion){apply.IsEnabled=false;StartRenameApply(_preparedRenamePlan);}};
    }
    private void StartRenameApply(RenamePlan plan)
    {
        if(!_renameWork.IsCompleted)return;
        if(_jobs.Any(job=>!job.Finished&&job.InputPaths.Any(path=>plan.Items.Any(item=>string.Equals(item.OriginalPath,path,StringComparison.OrdinalIgnoreCase))))){Toast(global::Orbit.Core.Localization.L.Get("Text_646F34576700"));return;}
        _renameCancellation?.Dispose();_renameCancellation=new CancellationTokenSource();
        int version=_renamePreviewVersion;
        _status.Text=global::Orbit.Core.Localization.L.Get("Text_1BD319271B9A");_radial.CenterHint.Text=global::Orbit.Core.Localization.L.Get("Text_1BD319271B9A");
        _renameWork=ApplyRenameAsync(plan,version,_renameCancellation.Token);
    }
    private async Task ApplyRenameAsync(RenamePlan plan,int version,CancellationToken ct)
    {
        try{
            var receipt=await _renameEngine.ApplyAsync(plan,ct);_lastRenameReceipt=receipt;_renameRecovery=null;
            if(!_closing&&version==_renamePreviewVersion){await LoadFilesAsync(receipt.Items.Select(item=>item.CurrentPath));ShowRenameResult();}
            _status.Text=global::Orbit.Core.Localization.L.Format("Text_4F3CC0659AB6",(object?)(receipt.Items.Count));
        }catch(OperationCanceledException){Toast(global::Orbit.Core.Localization.L.Get("Text_D047C02405FD"));}
        catch(RenameException ex){HandleRenameFailure(ex);}
        catch(Exception ex){App.Log(ex);Toast(ex.Message);}
    }
    private void StartRenameUndo()
    {
        if(!_renameWork.IsCompleted){Toast(global::Orbit.Core.Localization.L.Get("Text_B24668D31DE7"));return;}
        if(_lastRenameReceipt is not {IsUndone:false} receipt){Toast(global::Orbit.Core.Localization.L.Get("Text_2F0826A92527"));return;}
        _renameCancellation?.Dispose();_renameCancellation=new CancellationTokenSource();
        _status.Text=global::Orbit.Core.Localization.L.Get("Text_5B2D216BCB82");_radial.CenterHint.Text=global::Orbit.Core.Localization.L.Get("Text_5B2D216BCB82");
        _renameWork=UndoRenameAsync(receipt,_renamePreviewVersion,_renameCancellation.Token);
    }
    private async Task UndoRenameAsync(RenameReceipt receipt,int version,CancellationToken ct)
    {
        try{
            await _renameEngine.UndoAsync(receipt,ct);_renameRecovery=null;
            if(!_closing&&version==_renamePreviewVersion)await LoadFilesAsync(receipt.Items.Select(item=>item.OriginalPath));
            Toast(global::Orbit.Core.Localization.L.Get("Text_4F34300E802C"));
        }catch(OperationCanceledException){Toast(global::Orbit.Core.Localization.L.Get("Text_17BB918E1DB6"));}
        catch(RenameException ex){HandleRenameFailure(ex);}
        catch(Exception ex){App.Log(ex);Toast(ex.Message);}
    }
    private void ShowRenameResult()
    {
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_39EF37C61DB9"),global::Orbit.Core.Localization.L.Get("Text_83916AC21356"));
        foreach(var item in _lastRenameReceipt!.Items.Take(30))_panelContent.Children.Add(Text(Path.GetFileName(item.OriginalPath)+" → "+Path.GetFileName(item.CurrentPath),12));
        if(_lastRenameReceipt.Items.Count>30)_panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Format("Text_FA12E447440E",(object?)(_lastRenameReceipt.Items.Count)),12));
        _panelContent.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_4001A33D51D3"),(_,_)=>StartRenameUndo()));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_235415483E22"),11));
    }
    private void HandleRenameFailure(RenameException exception)
    {
        App.Log(exception);Toast(exception.Message);
        if(exception.RollbackFailed){_renameRecovery=exception;ClearSelection();ShowRenameRecovery();}
    }
    private void ShowRenameRecovery()
    {
        if(_renameRecovery==null)return;
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_99C94E12B6F7"),_renameRecovery.Message);
        foreach(var item in _renameRecovery.RemainingItems){_panelContent.Children.Add(Text(item.OriginalPath+" → "+item.CurrentPath,12));if(item.Problem!=null)_panelContent.Children.Add(Text(item.Problem,11));}
    }
}
