using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Orbit.Core;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private void ShowMoreTools()
    {
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_4E90074F740B"),_files.Count==0?global::Orbit.Core.Localization.L.Get("Text_0E90C29E8897"):global::Orbit.Core.Localization.L.Format("Text_54F49F23798C",(object?)(_files.Count)));
        var tools=new List<ToolCommand>();
        void Add(string title,Action action,string? sourceKey=null)=>tools.Add(new ToolCommand(title,ToolKeywords(sourceKey is null?title:global::Orbit.Core.Localization.L.SourceText(sourceKey)),action));
        Add(global::Orbit.Core.Localization.L.Get("Text_3E4E8D4E3E0B"),()=>ShowDownloadPanel(),sourceKey:"Text_3E4E8D4E3E0B");
        Add(global::Orbit.Core.Localization.L.Get("Text_AB8DD9BCBC4E"),ShowComponents,sourceKey:"Text_AB8DD9BCBC4E");
        Add(global::Orbit.Core.Localization.L.Get("Text_209F1AD09250"),ShowRepeatRecipe,sourceKey:"Text_209F1AD09250");Add(global::Orbit.Core.Localization.L.Get("Text_6266D9F94C71"),ShowFavorites,sourceKey:"Text_6266D9F94C71");
        var presetLookup=AllPresets().ToDictionary(p=>p.Id,StringComparer.Ordinal);
        foreach(var preset in _favoriteIds.Where(presetLookup.ContainsKey).Select(id=>presetLookup[id]).Where(p=>!_selectionLoading&&Orbit.Core.Presets.PresetCatalog.CanApply(p,_selectionMetadata)).Take(3))
            Add("★ "+preset.Name,()=>ApplyCurrentRecipe(preset));
        Add(global::Orbit.Core.Localization.L.Get("Text_1517854534ED"),ShowPresets,sourceKey:"Text_1517854534ED");Add(global::Orbit.Core.Localization.L.Get("Text_A0705C8C4AC6"),ShowRecentResults,sourceKey:"Text_A0705C8C4AC6");Add(global::Orbit.Core.Localization.L.Get("Text_DFF729E1AF66"),ShowJobs,sourceKey:"Text_DFF729E1AF66");
        Add(global::Orbit.Core.Localization.L.Get("Text_3AB706C10A21"),ShowImageChains,sourceKey:"Text_3AB706C10A21");
        Add(global::Orbit.Core.Localization.L.Get("Text_DF194A2562AD"),ShowFileChecksum,sourceKey:"Text_DF194A2562AD");
        Add(global::Orbit.Core.Localization.L.Get("Text_8E57E7FA1A29"),()=>ShowManifestChecksum(),sourceKey:"Text_8E57E7FA1A29");
        if(_files.Count>0&&!_selectionLoading){
            Add(global::Orbit.Core.Localization.L.Get("Text_BD38D43CF415"),ShowFileInfo,sourceKey:"Text_BD38D43CF415");
            Add(global::Orbit.Core.Localization.L.Get("Text_6A338F324967"),()=>DispatchAction(ImageAction.ArchiveZip),sourceKey:"Text_6A338F324967");
            Add(global::Orbit.Core.Localization.L.Get("Text_6523B3C02906"),ShowTarCreate,sourceKey:"Text_6523B3C02906");
            Add(global::Orbit.Core.Localization.L.Get("Text_0F74DB435E29"),ShowSevenZipCreate,sourceKey:"Text_0F74DB435E29");
            Add(global::Orbit.Core.Localization.L.Get("Text_E8CF83366F4F"),()=>DispatchAction(ImageAction.FileHash),sourceKey:"Text_E8CF83366F4F");
            if(_selectionMetadata.All(m=>m.Kind!=ToolkitKind.Folder))Add(global::Orbit.Core.Localization.L.Get("Text_16767A419744"),ShowRename,sourceKey:"Text_16767A419744");
            if(_selectionMetadata.All(m=>m.Kind is ToolkitKind.Image or ToolkitKind.Pdf))Add(global::Orbit.Core.Localization.L.Get("Text_91F3866DE0AF"),()=>DispatchAction(ImageAction.OcrText),sourceKey:"Text_91F3866DE0AF");
            if(_selectionKind==ToolkitKind.Image)Add(global::Orbit.Core.Localization.L.Get("Text_A9B8BE5A6976"),()=>DispatchAction(ImageAction.ImageTransform),sourceKey:"Text_A9B8BE5A6976");
            if(_selectionMetadata.All(m=>m.Kind==ToolkitKind.Image)){
                foreach(var format in new[]{ImageAction.Png,ImageAction.Bmp,ImageAction.Tiff,ImageAction.Gif,ImageAction.Ico,ImageAction.Svg,ImageAction.WebP,ImageAction.Avif}) {
                    var selectedFormat=format;Add(ActionName(format)+"…",()=>DispatchAction(selectedFormat));
                }
                Add(global::Orbit.Core.Localization.L.Get("Text_26867DD2422E"),ShowJpegExport,sourceKey:"Text_26867DD2422E");
                Add(global::Orbit.Core.Localization.L.Get("Text_45BA719FFC6E"),()=>ShowWatermarkEditor(),sourceKey:"Text_45BA719FFC6E");
                Add(global::Orbit.Core.Localization.L.Get("Text_FFACBF32D545"),()=>DispatchAction(ImageAction.ImagesToPdf),sourceKey:"Text_FFACBF32D545");
                Add(global::Orbit.Core.Localization.L.Get("Text_38609E67011D"),()=>DispatchAction(ImageAction.ContactSheet),sourceKey:"Text_38609E67011D");
                Add(global::Orbit.Core.Localization.L.Get("Text_1E1637335E0E"),()=>DispatchAction(ImageAction.RemoveImageMetadata),sourceKey:"Text_1E1637335E0E");
            }
            if(_selectionKind is ToolkitKind.Video or ToolkitKind.Audio){
                Add(global::Orbit.Core.Localization.L.Get("Text_98FECBEB6DEC"),()=>DispatchAction(_selectionKind==ToolkitKind.Video?ImageAction.VideoTrim:ImageAction.AudioTrim),sourceKey:"Text_98FECBEB6DEC");
                if(_files.Count==1){Add(global::Orbit.Core.Localization.L.Get("Text_4A612B084F7C"),()=>ShowMediaTracks(true),sourceKey:"Text_4A612B084F7C");Add(global::Orbit.Core.Localization.L.Get("Text_48DE3C97318B"),()=>ShowMediaTracks(false),sourceKey:"Text_48DE3C97318B");}
            }
            if(_selectionKind==ToolkitKind.Video){
                Add(global::Orbit.Core.Localization.L.Get("Text_5A0ED2068FE7"),()=>DispatchAction(ImageAction.VideoGif),sourceKey:"Text_5A0ED2068FE7");
                Add(global::Orbit.Core.Localization.L.Get("Text_6C2709557374"),()=>DispatchAction(ImageAction.VideoExport),sourceKey:"Text_6C2709557374");
                Add(global::Orbit.Core.Localization.L.Get("Text_82612ACC82A2"),ShowVideoFrames,sourceKey:"Text_82612ACC82A2");
            }
            if(_selectionKind==ToolkitKind.Audio)Add(global::Orbit.Core.Localization.L.Get("Text_79D68D7B077A"),()=>DispatchAction(ImageAction.AudioAdjust),sourceKey:"Text_79D68D7B077A");
            if(_selectionKind==ToolkitKind.Audio)Add(global::Orbit.Core.Localization.L.Get("Text_53E4CA961B68"),ShowAudioNormalize,sourceKey:"Text_53E4CA961B68");
            if(_selectionKind==ToolkitKind.Audio)Add(global::Orbit.Core.Localization.L.Get("Text_8E24D337644F"),ShowAudioExport,sourceKey:"Text_8E24D337644F");
            if(_selectionKind==ToolkitKind.Audio&&_files.All(p=>Path.GetExtension(p).ToLowerInvariant() is ".mp3" or ".flac" or ".m4a"))Add(global::Orbit.Core.Localization.L.Get("Text_99FBD5378460"),ShowAudioTags,sourceKey:"Text_99FBD5378460");
            if(_selectionKind==ToolkitKind.Audio)Add(global::Orbit.Core.Localization.L.Get("Text_FDA67857E7D6"),ShowAudioCover,sourceKey:"Text_FDA67857E7D6");
            if(_selectionMetadata.All(m=>m.Kind==ToolkitKind.Pdf)){
                Add(global::Orbit.Core.Localization.L.Get("Text_D751A39D0573"),ShowPdfMerge,sourceKey:"Text_D751A39D0573");
                if(_files.Count==1)Add(global::Orbit.Core.Localization.L.Get("Text_88421A3D7C2F"),ShowPdfPages,sourceKey:"Text_88421A3D7C2F");
                if(_files.Count==1)Add(global::Orbit.Core.Localization.L.Get("Text_D7967E77CC29"),ShowPdfRotation,sourceKey:"Text_D7967E77CC29");
                Add(global::Orbit.Core.Localization.L.Get("Text_1336DA935326"),ShowPdfSplit,sourceKey:"Text_1336DA935326");
                Add(global::Orbit.Core.Localization.L.Get("Text_FFDB23CD347D"),ShowPdfUtilities,sourceKey:"Text_FFDB23CD347D");
            }
            if(_files.Count==1&&_selectionKind==ToolkitKind.Zip)Add(global::Orbit.Core.Localization.L.Get("Text_22FEFDF582FC"),ShowZipBrowser,sourceKey:"Text_22FEFDF582FC");
            if(_files.Count==1&&_selectionKind==ToolkitKind.Tar)Add(global::Orbit.Core.Localization.L.Get("Text_B6EE07911B31"),ShowTarBrowser,sourceKey:"Text_B6EE07911B31");
            if(_files.Count==1&&_selectionKind==ToolkitKind.SevenZip)Add(global::Orbit.Core.Localization.L.Get("Text_14C22D4D56AE"),ShowSevenZipBrowser,sourceKey:"Text_14C22D4D56AE");
        }
        if(_lastRenameReceipt is {IsUndone:false})Add(global::Orbit.Core.Localization.L.Get("Text_4001A33D51D3"),()=>StartRenameUndo(),sourceKey:"Text_4001A33D51D3");
        if(_renameRecovery!=null)Add(global::Orbit.Core.Localization.L.Get("Text_8A35B95AFA00"),ShowRenameRecovery,sourceKey:"Text_8A35B95AFA00");
        Add(global::Orbit.Core.Localization.L.Get("Text_0FB52CCFB842"),ChooseFiles,sourceKey:"Text_0FB52CCFB842");Add(global::Orbit.Core.Localization.L.Get("Text_993B2028D35F"),ChooseFolders,sourceKey:"Text_993B2028D35F");Add(global::Orbit.Core.Localization.L.Get("Text_985B5E0F2CCF"),ShowSettings,sourceKey:"Text_985B5E0F2CCF");
        Add(global::Orbit.Core.Localization.L.Get("Text_BFB35ECBEC39"),()=>ShowWelcome(),sourceKey:"Text_BFB35ECBEC39");Add(global::Orbit.Core.Localization.L.Get("Text_70DB6B6CEC79"),ShowDiagnostics,sourceKey:"Text_70DB6B6CEC79");
        AddSearchOnlyCommands(tools);
        RenderToolSearch(tools);
    }
    private void ShowPdfPages()
    {
        if(_files.Count!=1||_selectionKind!=ToolkitKind.Pdf){Toast(global::Orbit.Core.Localization.L.Get("Text_FBC5A3502881"));return;}
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_4B3E10A5027E"),Path.GetFileName(_files[0]));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Format("Text_70911A47A9B5",(object?)(_metadata!.Pages)),14));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_E8231A40C8D0"),12));
        var pages=new TextBox{Text="1",MaxLength=8000,Margin=new Thickness(0,8,0,8)};_panelContent.Children.Add(pages);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_9B5F65F91260"),12));
        var run=Btn(global::Orbit.Core.Localization.L.Get("Text_C9D76D7B989C"),(_,_)=>{
            try{
                Orbit.Core.Documents.PdfAssemblyEngine.ParsePageSelection(pages.Text,_metadata.Pages);
                QueueSelected(new ImageJobOptions{Action=ImageAction.PdfExtractPages,PdfPageSelection=pages.Text,OutputDirectory=_outputDirectory});
                if(_compact)ClosePanel();else ShowJobs();
            }catch(Exception ex){Toast(ex.Message);}
        });GlassAccent(run);_panelContent.Children.Add(run);
    }
    private void ShowPdfMerge()
    {
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_195C2182B05D"),global::Orbit.Core.Localization.L.Get("Text_3A7BAB5E385C"));
        var paths=new ObservableCollection<string>(_selectionMetadata.Where(m=>m.Kind==ToolkitKind.Pdf).Select(m=>m.Path));
        var list=new ListBox{ItemsSource=paths,Height=165,Background=System.Windows.Media.Brushes.Transparent,Foreground=Ink("#EDF3FF"),BorderBrush=Ink("#6A9CAEC9"),BorderThickness=new Thickness(1),FontSize=12,Margin=new Thickness(0,7,0,9),HorizontalContentAlignment=HorizontalAlignment.Stretch};
        var caption=new FrameworkElementFactory(typeof(TextBlock));caption.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding{Converter=new PdfFileCaption()});caption.SetBinding(ToolTipProperty,new System.Windows.Data.Binding());caption.SetValue(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis);caption.SetValue(FrameworkElement.MarginProperty,new Thickness(3,6,3,6));
        list.ItemTemplate=new DataTemplate{VisualTree=caption};
        ScrollViewer.SetHorizontalScrollBarVisibility(list,ScrollBarVisibility.Disabled);_panelContent.Children.Add(list);
        var controls=new WrapPanel();
        controls.Children.Add(Btn("＋ PDF",(_,_)=>{
            var dialog=new OpenFileDialog{Title=global::Orbit.Core.Localization.L.Get("Text_15DE68594348"),Filter=global::Orbit.Core.Localization.L.Get("Text_6687C5212EAD"),Multiselect=true};
            if(WithPopupInteraction(()=>dialog.ShowDialog(this))==true)foreach(var path in dialog.FileNames)if(!paths.Contains(path,StringComparer.OrdinalIgnoreCase))paths.Add(path);
        }));
        controls.Children.Add(Btn("↑",(_,_)=>{int i=list.SelectedIndex;if(i>0){paths.Move(i,i-1);list.SelectedIndex=i-1;}}));
        controls.Children.Add(Btn("↓",(_,_)=>{int i=list.SelectedIndex;if(i>=0&&i<paths.Count-1){paths.Move(i,i+1);list.SelectedIndex=i+1;}}));
        controls.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_07CB65087477"),(_,_)=>{if(list.SelectedIndex>=0)paths.RemoveAt(list.SelectedIndex);}));
        _panelContent.Children.Add(controls);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_8B20BAB08E40"),12));
        var run=new Button{Content=global::Orbit.Core.Localization.L.Get("Text_8F27F05CA87A"),Margin=new Thickness(0,10,0,0)};GlassAccent(run);
        run.Click+=async(_,_)=>{
            if(paths.Count<2){Toast(global::Orbit.Core.Localization.L.Get("Text_610FDD908360"));return;}
            var ordered=paths.ToArray();run.IsEnabled=false;
            try{
                if(await LoadFilesAsync(ordered)){
                    if(_selectionMetadata.Any(m=>m.Kind!=ToolkitKind.Pdf)){Toast(global::Orbit.Core.Localization.L.Get("Text_EA8506263487"));return;}
                    Enqueue(ordered[0],new ImageJobOptions{Action=ImageAction.PdfMerge,OutputDirectory=_outputDirectory},ordered);
                    if(_compact)ClosePanel();else ShowJobs();
                }
            }finally{run.IsEnabled=true;}
        };
        _panelContent.Children.Add(run);
    }
    private sealed class PdfFileCaption : System.Windows.Data.IValueConverter
    {
        public object Convert(object value,Type targetType,object parameter,System.Globalization.CultureInfo culture)=>value is string path?Path.GetFileName(path):"";
        public object ConvertBack(object value,Type targetType,object parameter,System.Globalization.CultureInfo culture)=>throw new NotSupportedException();
    }
}
