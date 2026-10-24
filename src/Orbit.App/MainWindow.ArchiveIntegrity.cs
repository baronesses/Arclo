using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Orbit.Core;
using Orbit.Core.Checksums;
using Orbit.Core.Documents;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _archiveIntegrityCancellation;
    private readonly ChecksumEngine _checksumEngine=new();
    private Action<string>? _checksumCopyOverride;
    private void StopArchiveIntegrity()=>_archiveIntegrityCancellation?.Cancel();
    private sealed record TarCompressionChoice(string Id,string Label){public override string ToString()=>Label;}
    private bool CheckTarSelection(bool single=false)
    {
        if(_selectionLoading||_files.Count==0||single&&_files.Count!=1||_selectionMetadata.Any(m=>m.Kind!=ToolkitKind.Tar)){
            Toast(single?global::Orbit.Core.Localization.L.Get("Text_D1153D0E726E"):global::Orbit.Core.Localization.L.Get("Text_860EE2F19034"));return false;
        }
        return true;
    }
    private void ShowTarCreate()
    {
        if(_selectionLoading||_files.Count==0){Toast(global::Orbit.Core.Localization.L.Get("Text_29BCAB4127CE"));return;}
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_4BE039315043"),global::Orbit.Core.Localization.L.Format("Text_54F49F23798C",(object?)(_files.Count)));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_E96D818CC9FF"),12));
        var format=new ComboBox{Tag="tar-format",ItemsSource=new[]{"TAR.GZ","TAR"},SelectedIndex=0,Margin=new Thickness(0,2,0,7)};_panelContent.Children.Add(format);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_19DEF701193F"),12));
        var compression=new ComboBox{Tag="tar-compression",ItemsSource=new[]{new TarCompressionChoice("Fastest",global::Orbit.Core.Localization.L.Get("Text_568E623109DD")),new TarCompressionChoice("Optimal",global::Orbit.Core.Localization.L.Get("Text_B7FD555C25B7")),new TarCompressionChoice("SmallestSize",global::Orbit.Core.Localization.L.Get("Text_3A3742417469"))},SelectedIndex=1,Margin=new Thickness(0,2,0,7)};_panelContent.Children.Add(compression);
        format.SelectionChanged+=(_,_)=>compression.IsEnabled=format.SelectedIndex==0;
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_6B57971508EE"),12));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_CFFF44CFD77B"),11));
        int version=_loadVersion;var paths=_files.ToArray();
        var run=Btn(global::Orbit.Core.Localization.L.Get("Text_C1EEF42F73FC"),(_,_)=>{
            if(version!=_loadVersion||_selectionLoading||!paths.SequenceEqual(_files)){Toast(global::Orbit.Core.Localization.L.Get("Text_ED0DB2D45791"));return;}
            QueueEditing(new ImageJobOptions{Action=ImageAction.ArchiveTar,OutputDirectory=_outputDirectory,TarArchiveFormat=(string)format.SelectedItem,
                TarCompression=format.SelectedIndex==0?((TarCompressionChoice)compression.SelectedItem).Id:"Optimal"});
        });GlassAccent(run);_panelContent.Children.Add(run);
    }
    private void ShowTarAction(ImageAction action)
    {
        if(!CheckTarSelection())return;
        if(_compact){QueueEditing(new ImageJobOptions{Action=action,OutputDirectory=_outputDirectory});return;}
        OpenPanel(ActionName(action),global::Orbit.Core.Localization.L.Format("Text_AFA985FF924F",(object?)(_files.Count)));
        _panelContent.Children.Add(Text(action==ImageAction.ExtractTar?global::Orbit.Core.Localization.L.Get("Text_639F602F556D"):global::Orbit.Core.Localization.L.Get("Text_249B9A3B6DE9"),13));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_2E5409055143"),11));
        int version=_loadVersion;var paths=_files.ToArray();
        var run=Btn(action==ImageAction.ExtractTar?global::Orbit.Core.Localization.L.Get("Text_485AE2DC1D37"):global::Orbit.Core.Localization.L.Get("Text_B3507A71CDFF"),(_,_)=>{
            if(version!=_loadVersion||!CheckTarSelection()||!paths.SequenceEqual(_files)){Toast(global::Orbit.Core.Localization.L.Get("Text_4E5758BA6091"));return;}
            QueueEditing(new ImageJobOptions{Action=action,OutputDirectory=_outputDirectory});
        });GlassAccent(run);_panelContent.Children.Add(run);
    }
    private void ShowTarBrowser()
    {
        if(!CheckTarSelection(true))return;
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_7BD2DF7AC2D3"),Path.GetFileName(_files[0]));
        var loading=Text(global::Orbit.Core.Localization.L.Get("Text_242A9E1929B6"),13);_panelContent.Children.Add(loading);
        var cancellation=CancellationTokenSource.CreateLinkedTokenSource(_resultToolsCancellation.Token);_archiveIntegrityCancellation=cancellation;
        _=TrackResultTask(LoadTarBrowserAsync(_files[0],_comparisonVersion,_loadVersion,cancellation,loading));
    }
    private async Task LoadTarBrowserAsync(string path,int panelVersion,int loadVersion,CancellationTokenSource cancellation,TextBlock loading)
    {
        try{
            var listing=await new TarArchiveEngine().InspectAsync(path,cancellation.Token);
            if(_closing||panelVersion!=_comparisonVersion||loadVersion!=_loadVersion||cancellation.IsCancellationRequested)return;
            while(_panelContent.Children.Count>2)_panelContent.Children.RemoveAt(2);
            bool Current()=>!_closing&&!cancellation.IsCancellationRequested&&loadVersion==_loadVersion&&panelVersion==_comparisonVersion&&!_selectionLoading&&_files.Count==1&&_files[0]==path;
            RenderArchiveSelection(path,listing.Format,listing.Entries.Select(e=>new ArchiveBrowserRow(e.RelativeName,e.IsDirectory,e.Bytes)).ToArray(),listing.TotalBytes,listing.SourceFingerprint,false,false,Current);
        }catch(OperationCanceledException){}
        catch(Exception ex){if(!_closing&&panelVersion==_comparisonVersion)loading.Text=global::Orbit.Core.Localization.L.Get("Text_A97D317A9B51")+ex.Message;}
        finally{if(ReferenceEquals(_archiveIntegrityCancellation,cancellation))_archiveIntegrityCancellation=null;cancellation.Dispose();}
    }
    private void ShowFileChecksum()
    {
        if(!_selectionLoading&&_files.Count==1&&_selectionKind!=ToolkitKind.Folder){ShowFileChecksumFor(_files[0]);return;}
        ChooseChecksumFile();
    }
    private void ChooseChecksumFile()
    {
        InvalidatePendingInvocation();
        var dialog=new OpenFileDialog{Title=global::Orbit.Core.Localization.L.Get("Text_C6E5B3054BA4"),Filter=global::Orbit.Core.Localization.L.Get("Text_54E4DB1EDF0E"),Multiselect=false};
        if(WithPopupInteraction(()=>dialog.ShowDialog(this))==true)ShowFileChecksumFor(dialog.FileName);
    }
    private void ShowFileChecksumFor(string path)
    {
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_A143D93B062C"),Path.GetFileName(path));int panelVersion=_comparisonVersion;
        _panelContent.Children.Add(Text(path,11));_panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_1601C0578042"),12));
        var expected=new TextBox{Tag="checksum-expected",MaxLength=128,Margin=new Thickness(0,3,0,7)};_panelContent.Children.Add(expected);
        var status=Text(global::Orbit.Core.Localization.L.Get("Text_AA94E26DDF68"),12);_panelContent.Children.Add(status);
        var result=new StackPanel();_panelContent.Children.Add(result);
        var run=Btn(global::Orbit.Core.Localization.L.Get("Text_02D9AFB448F9"),(_,_)=>{});GlassAccent(run);_panelContent.Children.Add(run);
        var cancel=Btn(global::Orbit.Core.Localization.L.Get("Text_D832FB0B50D4"),(_,_)=>{if(panelVersion==_comparisonVersion)StopArchiveIntegrity();});cancel.IsEnabled=false;_panelContent.Children.Add(cancel);
        _panelContent.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_E7D1E5D45FB2"),(_,_)=>ChooseChecksumFile()));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_1074FE8BFDE7"),11));
        bool Valid()=>expected.Text.Trim() is{Length:64} hash&&hash.All(char.IsAsciiHexDigit);
        void Update(){run.IsEnabled=Valid();}
        expected.TextChanged+=(_,_)=>{if(panelVersion!=_comparisonVersion)return;StopArchiveIntegrity();result.Children.Clear();status.Text=Valid()?global::Orbit.Core.Localization.L.Get("Text_CA999365C507"):global::Orbit.Core.Localization.L.Get("Text_BD346B78BD9E");Update();};Update();
        run.Click+=(_,_)=>{
            if(panelVersion!=_comparisonVersion||!Valid())return;
            StopArchiveIntegrity();var cancellation=CancellationTokenSource.CreateLinkedTokenSource(_resultToolsCancellation.Token);_archiveIntegrityCancellation=cancellation;
            string hash=expected.Text.Trim();run.IsEnabled=false;cancel.IsEnabled=true;result.Children.Clear();status.Text=global::Orbit.Core.Localization.L.Get("Text_F9B613A39041");
            bool Current()=>!_closing&&panelVersion==_comparisonVersion&&ReferenceEquals(_archiveIntegrityCancellation,cancellation);
            var progress=new Progress<double>(value=>{if(Current()&&!cancellation.IsCancellationRequested)status.Text=global::Orbit.Core.Localization.L.Format("Text_3F7A0DB8D3A5",(object?)(Math.Clamp(value,0,1)));});
            _=TrackResultTask(CheckAsync());
            async Task CheckAsync(){try{
                var verified=await _checksumEngine.VerifyFileAsync(path,hash,progress,cancellation.Token);
                if(!Current()||cancellation.IsCancellationRequested)return;
                status.Text=ChecksumStatusText(verified.Status);status.Foreground=Ink(verified.Status==ChecksumStatus.Match?"#A7D8C8":"#EDACB9");
                if(verified.ActualSha256 is{} actual){result.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_AAA317095300"),12));result.Children.Add(new TextBox{Tag="checksum-actual",Text=actual,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,2,0,6)});
                    result.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_38D6D5F8757E"),(_,_)=>CopyChecksumText(actual)));}
                if(!string.IsNullOrEmpty(verified.Message))result.Children.Add(Text(verified.Message,11));
            }catch(OperationCanceledException){if(Current())status.Text=global::Orbit.Core.Localization.L.Get("Text_D9023B53164B");}
            catch(Exception ex){if(Current())status.Text=global::Orbit.Core.Localization.L.Get("Text_DE210110A131")+ex.Message;}
            finally{bool current=Current();if(ReferenceEquals(_archiveIntegrityCancellation,cancellation))_archiveIntegrityCancellation=null;cancellation.Dispose();if(current){cancel.IsEnabled=false;Update();}}}
        };
    }
    private void CopyChecksumText(string value)
    {
        if(_checksumCopyOverride!=null){_checksumCopyOverride(value);return;}
        if(IsDiagnosticRun){Toast(global::Orbit.Core.Localization.L.Get("Text_D3889B8382F1"));return;}
        try{Clipboard.SetText(value);Toast(global::Orbit.Core.Localization.L.Get("Text_B84C9D12129A"));}catch(Exception ex){Toast(global::Orbit.Core.Localization.L.Get("Text_C2636D50597C")+ex.Message);}
    }
    private static string ChecksumStatusText(ChecksumStatus status)=>status switch{
        ChecksumStatus.Match=>global::Orbit.Core.Localization.L.Get("Text_8AFA2241FD34"),ChecksumStatus.Mismatch=>global::Orbit.Core.Localization.L.Get("Text_D68CEFE360D9"),ChecksumStatus.Missing=>global::Orbit.Core.Localization.L.Get("Text_CAEE7CEA3236"),_=>global::Orbit.Core.Localization.L.Get("Text_6D9A1AA55DB3")};
    private void ShowManifestChecksum(string? manifestPath=null,string? rootDirectory=null)
    {
        if(manifestPath==null&&_files.Count==1&&Path.GetExtension(_files[0]) is{} ext&&(ext.Equals(".txt",StringComparison.OrdinalIgnoreCase)||ext.Equals(".sha256",StringComparison.OrdinalIgnoreCase)))manifestPath=_files[0];
        rootDirectory??=manifestPath==null?null:Path.GetDirectoryName(manifestPath);
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_DEF1DFA24DE0"),global::Orbit.Core.Localization.L.Get("Text_D9C6648C0B63"));int panelVersion=_comparisonVersion;
        var status=Text("",12);var run=Btn(global::Orbit.Core.Localization.L.Get("Text_7FE0E62CFDFD"),(_,_)=>{});var results=new StackPanel();bool busy=false;
        var manifestLabel=Text("",11);var rootLabel=Text("",11);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_7C251C00F1DC"),12));_panelContent.Children.Add(manifestLabel);
        var chooseManifest=Btn(global::Orbit.Core.Localization.L.Get("Text_ED8B9687B84A"),(_,_)=>{
            var dialog=new OpenFileDialog{Title=global::Orbit.Core.Localization.L.Get("Text_2B15F7DE3353"),Filter=global::Orbit.Core.Localization.L.Get("Text_97B91525FE72"),Multiselect=false};
            if(WithPopupInteraction(()=>dialog.ShowDialog(this))==true){manifestPath=dialog.FileName;rootDirectory=Path.GetDirectoryName(manifestPath);Update();}
        });_panelContent.Children.Add(chooseManifest);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_F675067C5D4E"),12));_panelContent.Children.Add(rootLabel);
        var chooseRoot=Btn(global::Orbit.Core.Localization.L.Get("Text_9876EB842D0B"),(_,_)=>{
            var dialog=new OpenFolderDialog{Title=global::Orbit.Core.Localization.L.Get("Text_255D19721D33"),Multiselect=false};
            if(WithPopupInteraction(()=>dialog.ShowDialog(this))==true){rootDirectory=dialog.FolderName;Update();}
        });_panelContent.Children.Add(chooseRoot);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_A3571610BA0B"),11));
        _panelContent.Children.Add(status);
        GlassAccent(run);_panelContent.Children.Add(run);
        var cancel=Btn(global::Orbit.Core.Localization.L.Get("Text_D832FB0B50D4"),(_,_)=>{if(panelVersion==_comparisonVersion)StopArchiveIntegrity();});cancel.IsEnabled=false;_panelContent.Children.Add(cancel);
        _panelContent.Children.Add(results);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_55D35D4B0A1F"),11));
        void Update(){manifestLabel.Text=manifestPath??global::Orbit.Core.Localization.L.Get("Text_CDCA4A022E62");rootLabel.Text=rootDirectory??global::Orbit.Core.Localization.L.Get("Text_07A20AB9B819");run.IsEnabled=!busy&&!string.IsNullOrWhiteSpace(manifestPath)&&!string.IsNullOrWhiteSpace(rootDirectory);results.Children.Clear();status.Text="";}
        Update();
        run.Click+=(_,_)=>{
            if(panelVersion!=_comparisonVersion||busy||manifestPath==null||rootDirectory==null)return;
            StopArchiveIntegrity();var cancellation=CancellationTokenSource.CreateLinkedTokenSource(_resultToolsCancellation.Token);_archiveIntegrityCancellation=cancellation;
            string manifest=manifestPath,root=rootDirectory;busy=true;chooseManifest.IsEnabled=chooseRoot.IsEnabled=false;run.IsEnabled=false;cancel.IsEnabled=true;results.Children.Clear();status.Text=global::Orbit.Core.Localization.L.Get("Text_480EE11BB4FC");
            bool Current()=>!_closing&&panelVersion==_comparisonVersion&&ReferenceEquals(_archiveIntegrityCancellation,cancellation);
            var progress=new Progress<double>(value=>{if(Current()&&!cancellation.IsCancellationRequested)status.Text=global::Orbit.Core.Localization.L.Format("Text_3F7A0DB8D3A5",(object?)(Math.Clamp(value,0,1)));});
            _=TrackResultTask(CheckAsync());
            async Task CheckAsync(){try{
                var verified=await _checksumEngine.VerifyManifestAsync(manifest,root,progress,cancellation.Token);
                if(!Current()||cancellation.IsCancellationRequested)return;
                int matches=verified.Entries.Count(e=>e.Status==ChecksumStatus.Match);
                status.Text=global::Orbit.Core.Localization.L.Format("Text_EBE103A0EFBD",(object?)(matches),(object?)(verified.Entries.Count))+(verified.AllMatch?global::Orbit.Core.Localization.L.Get("Text_10148E71037C"):global::Orbit.Core.Localization.L.Get("Text_53C82C503126"));status.Foreground=Ink(verified.AllMatch?"#A7D8C8":"#EDACB9");
                var onlyProblems=new CheckBox{Tag="checksum-problems",Content=global::Orbit.Core.Localization.L.Get("Text_8182348FD422"),IsChecked=!verified.AllMatch,Margin=new Thickness(0,4,0,5)};results.Children.Add(onlyProblems);
                var list=new ListBox{Tag="checksum-results",Height=170,FontSize=11};StyleImageCollectionList(list,true);VirtualizingPanel.SetIsVirtualizing(list,true);VirtualizingPanel.SetVirtualizationMode(list,VirtualizationMode.Recycling);results.Children.Add(list);
                void Refresh()=>list.ItemsSource=verified.Entries.Where(e=>onlyProblems.IsChecked!=true||e.Status!=ChecksumStatus.Match).Select(e=>ChecksumStatusText(e.Status)+" · "+Path.GetRelativePath(root,e.Path)+(string.IsNullOrEmpty(e.Message)?"":" · "+e.Message)).ToArray();
                onlyProblems.Click+=(_,_)=>Refresh();Refresh();
            }catch(OperationCanceledException){if(Current())status.Text=global::Orbit.Core.Localization.L.Get("Text_D9023B53164B");}
            catch(Exception ex){if(Current())status.Text=global::Orbit.Core.Localization.L.Get("Text_FD5271202FBD")+ex.Message;}
            finally{bool current=Current();if(ReferenceEquals(_archiveIntegrityCancellation,cancellation))_archiveIntegrityCancellation=null;cancellation.Dispose();if(current){busy=false;chooseManifest.IsEnabled=chooseRoot.IsEnabled=true;cancel.IsEnabled=false;run.IsEnabled=true;}}}
        };
    }
}
