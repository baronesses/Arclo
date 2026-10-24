using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Orbit.Core;
using Orbit.Core.Documents;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _zipBrowserCancellation;
    private bool CheckPdfSelection(bool single)
    {
        if(_selectionLoading||_files.Count==0||_selectionMetadata.Any(m=>m.Kind!=ToolkitKind.Pdf)||single&&_files.Count!=1){
            Toast(single?global::Orbit.Core.Localization.L.Get("Text_5FBE323A65A7"):global::Orbit.Core.Localization.L.Get("Text_F27BD2BBCC0E"));return false;
        }
        if(_selectionMetadata.Any(m=>m.Pages is <1 or >1000||m.Bytes>512L*1024*1024)){Toast(global::Orbit.Core.Localization.L.Get("Text_78E96A855CA1"));return false;}
        return true;
    }
    private void ShowPdfRotation()
    {
        if(!CheckPdfSelection(true))return;
        var path=_files[0];int version=_loadVersion;int pageCount=_metadata!.Pages;
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_18D18BF01B0D"),global::Orbit.Core.Localization.L.Format("Text_AD92255487F5",(object?)(Path.GetFileName(path)),(object?)(pageCount)));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_682E30802C1A"),13));
        var angle=new ComboBox{ItemsSource=new[]{90,180,270},SelectedItem=90,Margin=new Thickness(0,3,0,8)};_panelContent.Children.Add(angle);
        var all=new CheckBox{Content=global::Orbit.Core.Localization.L.Get("Text_D6B1C7961A44"),IsChecked=true};_panelContent.Children.Add(all);
        var pages=new TextBox{Text="1",MaxLength=8000,IsEnabled=false,Margin=new Thickness(0,5,0,6)};_panelContent.Children.Add(pages);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_94B2B28BFA40"),12));
        var message=Text("",12);_panelContent.Children.Add(message);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_C79C21452252"),12));
        var run=Btn(global::Orbit.Core.Localization.L.Get("Text_6C276CC4261F"),(_,_)=>{
            if(version!=_loadVersion||!CheckPdfSelection(true)||_files[0]!=path){Toast(global::Orbit.Core.Localization.L.Get("Text_4E5758BA6091"));return;}
            if(!Read(out var range))return;
            QueueEditing(new ImageJobOptions{Action=ImageAction.PdfRotate,OutputDirectory=_outputDirectory,
                PdfRotationDegrees=(int)angle.SelectedItem,PdfPageSelection=range});
        });GlassAccent(run);_panelContent.Children.Add(run);
        bool Read(out string range){
            range=all.IsChecked==true?$"1-{pageCount}":pages.Text;
            try{var selected=PdfAssemblyEngine.ParsePageSelection(range,pageCount);message.Text=global::Orbit.Core.Localization.L.Format("Text_E8ECE28B31FB",(object?)(selected.Distinct().Count()));message.Foreground=Ink("#A7D8C8");return true;}
            catch(Exception ex){message.Text=ex.Message;message.Foreground=Ink("#EDACB9");return false;}
        }
        void Update(){pages.IsEnabled=all.IsChecked!=true;run.IsEnabled=Read(out _);}
        pages.TextChanged+=(_,_)=>Update();all.Click+=(_,_)=>Update();Update();
    }
    private void ShowPdfSplit()
    {
        if(!CheckPdfSelection(false))return;
        int version=_loadVersion;var metadata=_selectionMetadata.ToArray();
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_C77DC8EC63A1"),_files.Count==1?global::Orbit.Core.Localization.L.Format("Text_AD92255487F5",(object?)(Path.GetFileName(_files[0])),(object?)(_metadata!.Pages)):$"PDF: {_files.Count}");
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_F0DE7BB6D23F"),13));
        var size=new TextBox{Text="1",MaxLength=4,Margin=new Thickness(0,4,0,7)};_panelContent.Children.Add(size);
        var estimate=Text("",12);_panelContent.Children.Add(estimate);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_7AAFFFF1AC46"),12));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_4BA2DC264768"),12));
        var run=Btn(global::Orbit.Core.Localization.L.Get("Text_67542EBFC185"),(_,_)=>{
            if(version!=_loadVersion||!CheckPdfSelection(false)){Toast(global::Orbit.Core.Localization.L.Get("Text_4E5758BA6091"));return;}
            if(!Read(out var count))return;
            QueueEditing(new ImageJobOptions{Action=ImageAction.PdfSplit,OutputDirectory=_outputDirectory,PdfSplitPagesPerFile=count});
        });GlassAccent(run);_panelContent.Children.Add(run);
        bool Read(out int count){
            bool valid=int.TryParse(size.Text,out count)&&count is >=1 and <=1000;
            int group=count;
            estimate.Text=valid?global::Orbit.Core.Localization.L.Format("Text_DFD737EA9019",(object?)(metadata.Sum(m=>(m.Pages+group-1)/group)),(object?)(metadata.Length)):global::Orbit.Core.Localization.L.Get("Text_303F0E684765");
            estimate.Foreground=Ink(valid?"#A7D8C8":"#EDACB9");return valid;
        }
        void Update()=>run.IsEnabled=Read(out _);size.TextChanged+=(_,_)=>Update();Update();
    }
    private void StopZipBrowser()=>_zipBrowserCancellation?.Cancel();
    private void ShowZipBrowser()
    {
        if(_selectionLoading||_files.Count!=1||_selectionKind!=ToolkitKind.Zip){Toast(global::Orbit.Core.Localization.L.Get("Text_510ABD7A1A47"));return;}
        var path=_files[0];OpenPanel(global::Orbit.Core.Localization.L.Get("Text_263794DF418C"),Path.GetFileName(path));
        var loading=Text(global::Orbit.Core.Localization.L.Get("Text_242A9E1929B6"),13);_panelContent.Children.Add(loading);
        var cancellation=CancellationTokenSource.CreateLinkedTokenSource(_resultToolsCancellation.Token);_zipBrowserCancellation=cancellation;
        int panelVersion=_comparisonVersion;int selectionVersion=_loadVersion;
        _=TrackResultTask(LoadZipBrowserAsync(path,panelVersion,selectionVersion,cancellation,loading));
    }
    private async Task LoadZipBrowserAsync(string path,int panelVersion,int selectionVersion,CancellationTokenSource cancellation,TextBlock loading)
    {
        try{
            var listing=await new ZipBrowserEngine().InspectAsync(path,cancellation.Token);
            if(_closing||cancellation.IsCancellationRequested||panelVersion!=_comparisonVersion||selectionVersion!=_loadVersion)return;
            while(_panelContent.Children.Count>2)_panelContent.Children.RemoveAt(2);
            RenderZipBrowser(listing,selectionVersion);
        }catch(OperationCanceledException){}
        catch(Exception ex){
            if(!_closing&&panelVersion==_comparisonVersion){loading.Text=global::Orbit.Core.Localization.L.Get("Text_36698A96EF21")+ex.Message;_panelContent.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_40F9BA844655"),(_,_)=>ShowZipBrowser()));Toast(ex.Message);}
        }finally{if(ReferenceEquals(_zipBrowserCancellation,cancellation))_zipBrowserCancellation=null;cancellation.Dispose();}
    }
    private sealed record ZipBrowserRow(ZipBrowserEntry Entry)
    {
        public string Label=>(Entry.IsDirectory?"▸ ":"  ")+Entry.RelativeName+(Entry.IsDirectory?"":"  · "+FormatBytes(Entry.UncompressedBytes));
    }
    private void RenderZipBrowser(ZipBrowserListing listing,int selectionVersion)
    {
        var rows=listing.Entries.Select(e=>new ZipBrowserRow(e)).ToArray();
        var chosen=new HashSet<string>(StringComparer.Ordinal);bool refreshing=false;
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Format("Text_DA5734FF87BA",(object?)(rows.Length),(object?)(FormatBytes(listing.TotalBytes))),12));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_FAC32113CA4E"),12));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_88885A53A066"),12));
        var search=new TextBox{MaxLength=200,Height=36,FontSize=12,Padding=new Thickness(9,5,9,5),VerticalContentAlignment=VerticalAlignment.Center,
            HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled,
            Margin=new Thickness(0,3,0,5),ToolTip=global::Orbit.Core.Localization.L.Get("Text_D9A4B02381A4")};_panelContent.Children.Add(search);
        var list=new ListBox{SelectionMode=SelectionMode.Multiple,Height=160,FontSize=12,DisplayMemberPath="Label",Margin=new Thickness(0,3,0,5)};
        StyleImageCollectionList(list,true);VirtualizingPanel.SetIsVirtualizing(list,true);VirtualizingPanel.SetVirtualizationMode(list,VirtualizationMode.Recycling);
        var rowStyle=new Style(typeof(ListBoxItem),list.ItemContainerStyle);rowStyle.Setters.Add(new Setter(ToolTipService.ToolTipProperty,new Binding("Label")));
        list.ItemContainerStyle=rowStyle;_panelContent.Children.Add(list);
        var status=Text("",12);_panelContent.Children.Add(status);
        var select=new Button{Content=global::Orbit.Core.Localization.L.Get("Text_3838B052AD6B"),Margin=new Thickness(3,0,3,0)};
        var clear=new Button{Content=global::Orbit.Core.Localization.L.Get("Text_8BCD241DDF5B"),Margin=new Thickness(3,0,3,0)};
        select.FontSize=clear.FontSize=12;select.Padding=clear.Padding=new Thickness(9,6,9,6);
        var controls=new WrapPanel();controls.Children.Add(select);controls.Children.Add(clear);_panelContent.Children.Add(controls);
        var run=Btn(global::Orbit.Core.Localization.L.Get("Text_494EEF0B4099"),(_,_)=>{
            if(_closing||selectionVersion!=_loadVersion||_selectionLoading||_files.Count!=1||_files[0]!=listing.SourcePath){Toast(global::Orbit.Core.Localization.L.Get("Text_B7D8DD383E71"));return;}
            var names=rows.Where(r=>chosen.Contains(r.Entry.RelativeName)).Select(r=>r.Entry.RelativeName).ToArray();
            if(!Valid(names))return;
            QueueEditing(new ImageJobOptions{Action=ImageAction.ZipExtractSelected,OutputDirectory=_outputDirectory,
                ZipSelectedEntries=Array.AsReadOnly(names),ZipSourceSha256=listing.SourceFingerprint});
        });GlassAccent(run);_panelContent.Children.Add(run);
        select.Click+=(_,_)=>{foreach(var row in Visible())chosen.Add(row.Entry.RelativeName);Refresh();};
        clear.Click+=(_,_)=>{chosen.Clear();Refresh();};
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_F24F3F879832"),11));
        ZipBrowserRow[] Visible()=>rows.Where(r=>r.Entry.RelativeName.Contains(search.Text.Trim(),StringComparison.OrdinalIgnoreCase)).ToArray();
        static bool Valid(IEnumerable<string> names){var array=names.ToArray();return array.Length is >=1 and <=1000&&array.Sum(n=>(long)n.Length)<=131072;}
        void Update(){
            var visible=Visible();var all=chosen.Concat(visible.Select(r=>r.Entry.RelativeName)).Distinct(StringComparer.Ordinal).ToArray();
            select.IsEnabled=visible.Length>0&&Valid(all);clear.IsEnabled=chosen.Count>0;run.IsEnabled=Valid(chosen);
            status.Text=global::Orbit.Core.Localization.L.Format("Text_DF3454F86F8F",(object?)(visible.Length),(object?)(chosen.Count));status.Foreground=Ink(run.IsEnabled?"#A7D8C8":"#BAC6DE");
        }
        void Refresh(){
            refreshing=true;try{var visible=Visible();list.ItemsSource=visible;foreach(var row in visible)if(chosen.Contains(row.Entry.RelativeName))list.SelectedItems.Add(row);}finally{refreshing=false;}
            Update();
        }
        list.SelectionChanged+=(_,e)=>{
            if(refreshing)return;
            foreach(ZipBrowserRow row in e.RemovedItems)chosen.Remove(row.Entry.RelativeName);
            foreach(ZipBrowserRow row in e.AddedItems)chosen.Add(row.Entry.RelativeName);
            if(chosen.Count>1000||chosen.Sum(n=>(long)n.Length)>131072){foreach(ZipBrowserRow row in e.AddedItems)chosen.Remove(row.Entry.RelativeName);Toast(global::Orbit.Core.Localization.L.Get("Text_2C5961B10B6C"));Refresh();}
            else Update();
        };
        search.TextChanged+=(_,_)=>Refresh();Refresh();
    }
}
