using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Orbit.Core;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private sealed record ArchiveBrowserRow(string Name,bool Directory,long Bytes)
    {
        public string Label=>(Directory?"▸ ":"  ")+Name+(Directory?"":" · "+FormatBytes(Bytes));
    }
    private void RenderArchiveSelection(string path,string format,IReadOnlyList<ArchiveBrowserRow> rows,long totalBytes,string fingerprint,bool sevenZip,bool passwordProtected,Func<bool> current)
    {
        var prefix=sevenZip?"sevenzip":"tar";var chosen=new HashSet<string>(StringComparer.Ordinal);bool refreshing=false;
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Format("Text_3A2ECF62BB51",(object?)(format),(object?)(rows.Count),(object?)(FormatBytes(totalBytes)))+(passwordProtected?global::Orbit.Core.Localization.L.Get("Text_E7917CA10C69"):""),12));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_B1A1705463CA"),11));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_D9A4B02381A4"),12));
        var search=new TextBox{Tag=prefix+"-search",MaxLength=200,Height=36,FontSize=12,Padding=new Thickness(9,5,9,5),VerticalContentAlignment=VerticalAlignment.Center,Margin=new Thickness(0,2,0,5)};_panelContent.Children.Add(search);
        var list=new ListBox{Tag=prefix+"-entries",SelectionMode=SelectionMode.Multiple,Height=110,FontSize=12,DisplayMemberPath="Label",Margin=new Thickness(0,3,0,5)};StyleImageCollectionList(list,true);
        VirtualizingPanel.SetIsVirtualizing(list,true);VirtualizingPanel.SetVirtualizationMode(list,VirtualizationMode.Recycling);
        var rowStyle=new Style(typeof(ListBoxItem),list.ItemContainerStyle);rowStyle.Setters.Add(new Setter(ToolTipService.ToolTipProperty,new Binding("Label")));list.ItemContainerStyle=rowStyle;_panelContent.Children.Add(list);
        var status=Text("",11);status.Tag=prefix+"-count";_panelContent.Children.Add(status);
        var controls=new WrapPanel();var select=Btn(global::Orbit.Core.Localization.L.Get("Text_3838B052AD6B"),(_,_)=>{});var clear=Btn(global::Orbit.Core.Localization.L.Get("Text_8BCD241DDF5B"),(_,_)=>{});
        foreach(var button in new[]{select,clear}){button.FontSize=11;button.Padding=new Thickness(9,5,9,5);controls.Children.Add(button);}_panelContent.Children.Add(controls);
        var run=Btn(global::Orbit.Core.Localization.L.Get("Text_494EEF0B4099"),(_,_)=>{
            if(!current()){Toast(global::Orbit.Core.Localization.L.Get("Text_EF1BD6BF7AAC"));return;}
            var names=rows.Where(row=>chosen.Contains(row.Name)).Select(row=>row.Name).ToArray();if(!Valid(names))return;
            Queue(new ImageJobOptions{Action=sevenZip?ImageAction.SevenZipExtractSelected:ImageAction.TarExtractSelected,ArchiveSelectedEntries=Array.AsReadOnly(names),ArchiveSourceSha256=fingerprint,ArchiveSelectedCount=names.Length,OutputDirectory=_outputDirectory});
        });run.Tag=prefix+"-extract-selected";GlassAccent(run);_panelContent.Children.Add(run);
        _panelContent.Children.Add(Btn(sevenZip?global::Orbit.Core.Localization.L.Get("Text_9C45F4562EA0"):global::Orbit.Core.Localization.L.Get("Text_9EC8C7063332"),(_,_)=>Queue(new ImageJobOptions{Action=sevenZip?ImageAction.Extract7z:ImageAction.ExtractTar,OutputDirectory=_outputDirectory})));
        _panelContent.Children.Add(Btn(sevenZip?global::Orbit.Core.Localization.L.Get("Text_6A086E048DC2"):global::Orbit.Core.Localization.L.Get("Text_B3507A71CDFF"),(_,_)=>Queue(new ImageJobOptions{Action=sevenZip?ImageAction.SevenZipList:ImageAction.TarList,OutputDirectory=_outputDirectory})));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_DDBCFD210F94"),11));
        void Queue(ImageJobOptions options){
            if(!current()){Toast(global::Orbit.Core.Localization.L.Get("Text_EF1BD6BF7AAC"));return;}
            if(passwordProtected){
                if(_sevenZipBrowsePassword is not { } password){Toast(global::Orbit.Core.Localization.L.Get("Text_0CD13217F818"));return;}
                QueueSevenZipWithPassword(new[]{path},options with{SevenZipPasswordProtected=true},password.Clone());
            }else QueueEditing(options);
        }
        static bool Valid(IEnumerable<string> names){var values=names.ToArray();return values.Length is >=1 and <=1000&&values.Sum(n=>(long)n.Length)<=131072;}
        ArchiveBrowserRow[] Visible()=>rows.Where(row=>row.Name.Contains(search.Text.Trim(),StringComparison.OrdinalIgnoreCase)).ToArray();
        void Update(){var visible=Visible();select.IsEnabled=current()&&visible.Length>0&&Valid(chosen.Concat(visible.Select(row=>row.Name)).Distinct(StringComparer.Ordinal));clear.IsEnabled=current()&&chosen.Count>0;run.IsEnabled=current()&&Valid(chosen);status.Text=global::Orbit.Core.Localization.L.Format("Text_C78109D7CC89",(object?)(visible.Length),(object?)(rows.Count),(object?)(chosen.Count));}
        void Refresh(){if(!current())return;refreshing=true;try{var visible=Visible();list.ItemsSource=visible;foreach(var row in visible)if(chosen.Contains(row.Name))list.SelectedItems.Add(row);}finally{refreshing=false;}Update();}
        select.Click+=(_,_)=>{if(!current())return;var all=chosen.Concat(Visible().Select(row=>row.Name)).Distinct(StringComparer.Ordinal).ToArray();if(!Valid(all))return;chosen.UnionWith(all);Refresh();};
        clear.Click+=(_,_)=>{if(!current())return;chosen.Clear();Refresh();};
        list.SelectionChanged+=(_,args)=>{
            if(refreshing||!current())return;
            foreach(ArchiveBrowserRow row in args.RemovedItems)chosen.Remove(row.Name);foreach(ArchiveBrowserRow row in args.AddedItems)chosen.Add(row.Name);
            if(!Valid(chosen)&&chosen.Count>0){foreach(ArchiveBrowserRow row in args.AddedItems)chosen.Remove(row.Name);Toast(global::Orbit.Core.Localization.L.Get("Text_39896625AA9E"));Refresh();}else Update();
        };
        search.TextChanged+=(_,_)=>Refresh();Refresh();
    }
}
