using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Orbit.Core;
using Orbit.Core.Documents;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _sevenZipBrowseCancellation;
    private void StopSevenZipBrowser(){_sevenZipBrowseCancellation?.Cancel();ClearSevenZipPasswords();}
    private sealed record SevenZipCompressionChoice(string Id,string Label){public override string ToString()=>Label;}
    private bool CheckSevenZipSelection(bool single=false)
    {
        if(_selectionLoading||_files.Count==0||single&&_files.Count!=1||_selectionMetadata.Any(m=>m.Kind!=ToolkitKind.SevenZip)){
            Toast(single?global::Orbit.Core.Localization.L.Get("Text_37B71742E543"):global::Orbit.Core.Localization.L.Get("Text_5BA1CF77AEBF"));return false;
        }
        return true;
    }
    private void ShowSevenZipCreate()
    {
        if(_selectionLoading||_files.Count==0){Toast(global::Orbit.Core.Localization.L.Get("Text_29BCAB4127CE"));return;}
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_571F13BF0489"),global::Orbit.Core.Localization.L.Format("Text_54F49F23798C",(object?)(_files.Count)));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_1D3C301D0FFD"),12));
        var compression=new ComboBox{Tag="sevenzip-compression",ItemsSource=new[]{new SevenZipCompressionChoice("Fastest",global::Orbit.Core.Localization.L.Get("Text_568E623109DD")),new SevenZipCompressionChoice("Optimal",global::Orbit.Core.Localization.L.Get("Text_B7FD555C25B7")),new SevenZipCompressionChoice("SmallestSize",global::Orbit.Core.Localization.L.Get("Text_3A3742417469"))},SelectedIndex=1,Margin=new Thickness(0,2,0,8)};_panelContent.Children.Add(compression);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_E36607E9F86B"),12));
        var protect=new CheckBox{Tag="sevenzip-protect",Content=global::Orbit.Core.Localization.L.Get("Text_CF26E803E786"),Margin=new Thickness(0,5,0,7)};_panelContent.Children.Add(protect);
        var passwordFields=new StackPanel{Visibility=Visibility.Collapsed};_panelContent.Children.Add(passwordFields);
        var password=AddSevenZipPasswordField(passwordFields,global::Orbit.Core.Localization.L.Get("Text_CB1A2074B3A0"),"sevenzip-password");
        var confirmation=AddSevenZipPasswordField(passwordFields,global::Orbit.Core.Localization.L.Get("Text_65AF96D11B73"),"sevenzip-password-confirm");
        var headers=new CheckBox{Tag="sevenzip-encrypt-headers",Content=global::Orbit.Core.Localization.L.Get("Text_E25AF9F64D4E"),IsChecked=true,Margin=new Thickness(0,0,0,6)};passwordFields.Children.Add(headers);
        var status=Text("",11,"#EDACB9");status.Tag="sevenzip-password-status";_panelContent.Children.Add(status);
        password.PasswordChanged+=(_,_)=>status.Text="";confirmation.PasswordChanged+=(_,_)=>status.Text="";
        protect.Click+=(_,_)=>{passwordFields.Visibility=protect.IsChecked==true?Visibility.Visible:Visibility.Collapsed;password.Clear();confirmation.Clear();status.Text="";};
        int panelVersion=_comparisonVersion,loadVersion=_loadVersion;var paths=_files.ToArray();
        var run=Btn(global::Orbit.Core.Localization.L.Get("Text_E9E6102439D6"),(_,_)=>{
            if(panelVersion!=_comparisonVersion||loadVersion!=_loadVersion||_selectionLoading||!paths.SequenceEqual(_files)){Toast(global::Orbit.Core.Localization.L.Get("Text_ED0DB2D45791"));return;}
            if(compression.SelectedItem is SevenZipCompressionChoice choice){
                var options=new ImageJobOptions{Action=ImageAction.Archive7z,SevenZipCompression=choice.Id,OutputDirectory=_outputDirectory};
                if(protect.IsChecked!=true){QueueEditing(options);return;}
                try{QueueSevenZipWithPassword(paths,options with{SevenZipPasswordProtected=true,SevenZipEncryptHeaders=headers.IsChecked==true},ReadSevenZipPassword(password,confirmation));}
                catch(Exception ex){password.Clear();confirmation.Clear();status.Text=ex.Message;password.Focus();}
            }
        });GlassAccent(run);_panelContent.Children.Add(run);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_DD3800972466"),11));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_85A089E64996"),11));
    }
    private void ShowSevenZipAction(ImageAction action)
    {
        if(!CheckSevenZipSelection())return;
        if(_selectionMetadata.Any(m=>m.ArchiveLocked)){ShowSevenZipPasswordRequest(_files,new ImageJobOptions{Action=action,SevenZipPasswordProtected=true,OutputDirectory=_outputDirectory},true);return;}
        if(_compact){QueueEditing(new ImageJobOptions{Action=action,OutputDirectory=_outputDirectory});return;}
        OpenPanel(ActionName(action),global::Orbit.Core.Localization.L.Format("Text_AFA985FF924F",(object?)(_files.Count)));
        _panelContent.Children.Add(Text(action==ImageAction.Extract7z?global::Orbit.Core.Localization.L.Get("Text_4C56214A931C"):global::Orbit.Core.Localization.L.Get("Text_4F60C7658352"),12));
        int panelVersion=_comparisonVersion,loadVersion=_loadVersion;var paths=_files.ToArray();
        var run=Btn(action==ImageAction.Extract7z?global::Orbit.Core.Localization.L.Get("Text_27F8FBB2D20C"):global::Orbit.Core.Localization.L.Get("Text_EAC2F8140A5D"),(_,_)=>{
            if(panelVersion==_comparisonVersion&&loadVersion==_loadVersion&&paths.SequenceEqual(_files)&&CheckSevenZipSelection())QueueEditing(new ImageJobOptions{Action=action,OutputDirectory=_outputDirectory});
        });GlassAccent(run);_panelContent.Children.Add(run);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_41C5B626635B"),11));
    }
    private void ShowSevenZipBrowser()=>ShowSevenZipBrowser(false);
    private void ShowSevenZipBrowser(bool requirePassword)
    {
        if(!CheckSevenZipSelection(true))return;
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_67AE104662A0"),Path.GetFileName(_files[0]));
        if(requirePassword||_metadata?.ArchiveLocked==true){ShowSevenZipBrowserUnlock();return;}
        var loading=Text(global::Orbit.Core.Localization.L.Get("Text_242A9E1929B6"),13);_panelContent.Children.Add(loading);
        var cancellation=CancellationTokenSource.CreateLinkedTokenSource(_resultToolsCancellation.Token);_sevenZipBrowseCancellation=cancellation;
        _=TrackResultTask(LoadSevenZipBrowserAsync(_files[0],_comparisonVersion,_loadVersion,cancellation,loading,null));
    }
    private void ShowSevenZipBrowserUnlock()
    {
        var path=_files[0];int panelVersion=_comparisonVersion,loadVersion=_loadVersion;
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_3B5DD84DAD3F"),13));
        var box=AddSevenZipPasswordField(_panelContent,global::Orbit.Core.Localization.L.Get("Text_BBD1C4D16A4C"),"sevenzip-password");
        var status=Text("",12,"#EDACB9");status.Tag="sevenzip-password-status";_panelContent.Children.Add(status);
        var run=Btn(global::Orbit.Core.Localization.L.Get("Text_45146BB05E5E"),(_,_)=>{});GlassAccent(run);_panelContent.Children.Add(run);
        box.PasswordChanged+=(_,_)=>{if(run.IsEnabled)status.Text="";};
        run.Click+=async(_,_)=>{
            bool Current()=>!_closing&&panelVersion==_comparisonVersion&&loadVersion==_loadVersion&&!_selectionLoading&&_files.Count==1&&_files[0]==path;
            if(!Current())return;
            SevenZipPassword password;
            try{password=ReadSevenZipPassword(box);}catch(Exception ex){box.Clear();status.Text=ex.Message;return;}
            box.Clear();run.IsEnabled=false;status.Text=global::Orbit.Core.Localization.L.Get("Text_80365217F199");
            var cancellation=CancellationTokenSource.CreateLinkedTokenSource(_resultToolsCancellation.Token);_sevenZipBrowseCancellation=cancellation;
            try{await TrackResultTask(LoadSevenZipBrowserAsync(path,panelVersion,loadVersion,cancellation,status,password));}
            finally{if(Current()){run.IsEnabled=true;box.Focus();}}
        };
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_9B165E5B011A"),11));
        box.Focus();
    }
    private async Task LoadSevenZipBrowserAsync(string path,int panelVersion,int loadVersion,CancellationTokenSource cancellation,TextBlock loading,SevenZipPassword? password)
    {
        bool Current()=>!_closing&&panelVersion==_comparisonVersion&&loadVersion==_loadVersion&&!cancellation.IsCancellationRequested&&_files.Count==1&&_files[0]==path;
        bool retained=false;
        try{
            var engine=new SevenZipArchiveEngine();var listing=password is null?await engine.InspectAsync(path,cancellation.Token):await engine.InspectAsync(path,password,cancellation.Token);
            if(!Current())return;
            _sevenZipBrowsePassword=password;retained=password is not null;
            while(_panelContent.Children.Count>2)_panelContent.Children.RemoveAt(2);
            RenderArchiveSelection(path,"7z",listing.Entries.Select(e=>new ArchiveBrowserRow(e.RelativeName,e.IsDirectory,e.Bytes)).ToArray(),listing.TotalBytes,listing.SourceFingerprint,true,password is not null,Current);
        }catch(OperationCanceledException){}
        catch(SevenZipPasswordRequiredException){if(Current())ShowSevenZipBrowser(true);}
        catch(Exception ex){if(Current())loading.Text=global::Orbit.Core.Localization.L.Get("Text_861A2FA0F2AD")+ex.Message;}
        finally{if(!retained)password?.Dispose();if(ReferenceEquals(_sevenZipBrowseCancellation,cancellation))_sevenZipBrowseCancellation=null;cancellation.Dispose();}
    }
}
