using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Orbit.Core;
using Orbit.Core.Presets;
using Orbit.Core.Watermarks;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private readonly ImageWatermarkEngine _watermarkTools=new();
    private CancellationTokenSource? _watermarkPreviewCancellation;
    private CancellationTokenSource? _watermarkLogoCancellation;
    private Func<string?>? _watermarkLogoPickerOverride;
    private void StopWatermarkTools(){var preview=_watermarkPreviewCancellation;var logo=_watermarkLogoCancellation;_watermarkPreviewCancellation=null;_watermarkLogoCancellation=null;preview?.Cancel();logo?.Cancel();}
    private sealed record WatermarkChoice(string Id,string Label){public override string ToString()=>Label;}
    private void ShowWatermarkEditor(ImageJobOptions? initial=null)
    {
        if(!CheckEditableImages())return;
        initial??=new ImageJobOptions{Action=ImageAction.ImageWatermark};
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_2F86E281ECE0"),global::Orbit.Core.Localization.L.Format("Text_56C05A342978",(object?)(_files.Count)));
        int panelVersion=_comparisonVersion,loadVersion=_loadVersion,editVersion=0;var paths=_files.ToArray();
        bool Current()=>!_closing&&panelVersion==_comparisonVersion&&loadVersion==_loadVersion&&!_selectionLoading&&paths.SequenceEqual(_files);
        ComboBox Choices(string tag,WatermarkChoice[] items,string selected)=>new(){Tag=tag,ItemsSource=items,SelectedItem=items.FirstOrDefault(i=>i.Id==selected)??items.FirstOrDefault(),Margin=new Thickness(0,2,0,5)};
        ComboBox Number(string tag,int[] choices,int selected)=>new(){Tag=tag,ItemsSource=choices.Append(selected).Distinct().Order().ToArray(),SelectedItem=selected,Margin=new Thickness(0,2,0,5)};
        void Pair(FrameworkElement left,string leftLabel,FrameworkElement right,string rightLabel){var grid=new Grid();grid.ColumnDefinitions.Add(new ColumnDefinition());grid.ColumnDefinitions.Add(new ColumnDefinition());
            var a=new StackPanel{Margin=new Thickness(0,0,5,0)};a.Children.Add(Text(leftLabel,11));a.Children.Add(left);grid.Children.Add(a);var b=new StackPanel{Margin=new Thickness(5,0,0,0)};b.Children.Add(Text(rightLabel,11));b.Children.Add(right);Grid.SetColumn(b,1);grid.Children.Add(b);_panelContent.Children.Add(grid);}
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_9BF20172DA57"),11));var mode=Choices("watermark-mode",new[]{new WatermarkChoice("Text",global::Orbit.Core.Localization.L.Get("Text_3091ACA226EF")),new WatermarkChoice("Logo",global::Orbit.Core.Localization.L.Get("Text_2183263F6141"))},initial.WatermarkMode);_panelContent.Children.Add(mode);
        var textFields=new StackPanel();textFields.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_BCAF62A5754E"),11));
        var text=new TextBox{Tag="watermark-text",Text=initial.WatermarkText,MaxLength=200,Height=36,Padding=new Thickness(9,5,9,5),Margin=new Thickness(0,2,0,5)};textFields.Children.Add(text);_panelContent.Children.Add(textFields);
        var logoFields=new StackPanel();var choose=Btn(global::Orbit.Core.Localization.L.Get("Text_96DAE088413B"),(_,_)=>{});choose.Tag="watermark-logo-picker";logoFields.Children.Add(choose);
        string? logoPath=initial.WatermarkLogoPath,logoSha=initial.WatermarkLogoSha256;bool logoBusy=false;
        var logoCaption=Text(logoPath is null?global::Orbit.Core.Localization.L.Get("Text_EBBD6B44CDEA"):Path.GetFileName(logoPath),11);logoCaption.Tag="watermark-logo-caption";logoFields.Children.Add(logoCaption);_panelContent.Children.Add(logoFields);
        var fontChoices=_watermarkTools.GetAvailableFonts().Select(id=>new WatermarkChoice(id,id=="SegoeUI"?"Segoe UI":id)).ToArray();
        var font=Choices("watermark-font",fontChoices,initial.WatermarkFont);var color=new TextBox{Tag="watermark-color",Text=initial.WatermarkColor,MaxLength=7,Height=36,Padding=new Thickness(9,5,9,5),CharacterCasing=CharacterCasing.Upper,Margin=new Thickness(0,2,0,5)};
        Pair(font,global::Orbit.Core.Localization.L.Get("Text_239978457355"),color,global::Orbit.Core.Localization.L.Get("Text_EECAC9040D27"));var fontRow=_panelContent.Children[_panelContent.Children.Count-1];
        var position=Choices("watermark-position",new[]{new WatermarkChoice("TopLeft",global::Orbit.Core.Localization.L.Get("Text_7522A1ADE9BB")),new WatermarkChoice("Top",global::Orbit.Core.Localization.L.Get("Text_1E4DAAD81029")),new WatermarkChoice("TopRight",global::Orbit.Core.Localization.L.Get("Text_810428694614")),new WatermarkChoice("Left",global::Orbit.Core.Localization.L.Get("Text_D7F76A36C9FB")),new WatermarkChoice("Center",global::Orbit.Core.Localization.L.Get("Text_93F3D6B8DC12")),new WatermarkChoice("Right",global::Orbit.Core.Localization.L.Get("Text_98AB54C1C01F")),new WatermarkChoice("BottomLeft",global::Orbit.Core.Localization.L.Get("Text_525FF9B4B406")),new WatermarkChoice("Bottom",global::Orbit.Core.Localization.L.Get("Text_CFC06F2B8D8D")),new WatermarkChoice("BottomRight",global::Orbit.Core.Localization.L.Get("Text_BC9384DD60B8"))},initial.WatermarkPosition);
        var size=Number("watermark-size",Enumerable.Range(1,15).ToArray(),initial.WatermarkFontSizePercent);Pair(position,global::Orbit.Core.Localization.L.Get("Text_8B0130C9C163"),size,global::Orbit.Core.Localization.L.Get("Text_CC3498FE2C9F"));
        var opacity=Number("watermark-opacity",new[]{1,10,25,40,50,60,75,90,100},initial.WatermarkOpacityPercent);var margin=Number("watermark-margin",new[]{0,1,2,3,5,8,10,15,20},initial.WatermarkMarginPercent);Pair(opacity,global::Orbit.Core.Localization.L.Get("Text_F33849275C3C"),margin,global::Orbit.Core.Localization.L.Get("Text_8A425A17AB9D"));
        var status=Text("",11,"#EDACB9");status.Tag="watermark-status";_panelContent.Children.Add(status);
        var preview=Btn(global::Orbit.Core.Localization.L.Get("Text_32060A34F5BE"),(_,_)=>{});preview.Tag="watermark-preview-button";var save=Btn(global::Orbit.Core.Localization.L.Get("Text_6A22508FEEAB"),(_,_)=>{});save.Tag="watermark-save";GlassAccent(save);
        var actions=new Grid();actions.ColumnDefinitions.Add(new ColumnDefinition());actions.ColumnDefinitions.Add(new ColumnDefinition());preview.FontSize=save.FontSize=11;preview.Padding=save.Padding=new Thickness(6,8,6,8);actions.Children.Add(preview);Grid.SetColumn(save,1);actions.Children.Add(save);_panelContent.Children.Add(actions);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_3F6BF7CAD0BD"),11));
        bool Read(out ImageJobOptions options,out string message){
            options=new ImageJobOptions{Action=ImageAction.ImageWatermark};message="";if(!Current()||logoBusy){message=logoBusy?global::Orbit.Core.Localization.L.Get("Text_B2DCB32DAAD5"):global::Orbit.Core.Localization.L.Get("Text_9E06DBAF5245");return false;}
            bool logo=(mode.SelectedItem as WatermarkChoice)?.Id=="Logo";
            if(position.SelectedItem is not WatermarkChoice place||size.SelectedItem is not int relative||opacity.SelectedItem is not int alpha||margin.SelectedItem is not int border)return false;
            options=new ImageJobOptions{Action=ImageAction.ImageWatermark,OutputDirectory=_outputDirectory,WatermarkMode=logo?"Logo":"Text",WatermarkPosition=place.Id,WatermarkOpacityPercent=alpha,WatermarkMarginPercent=border};
            if(logo){if(logoPath is null||logoSha is null){message=global::Orbit.Core.Localization.L.Get("Text_72AB875D2036");return false;}options=options with{WatermarkLogoPath=logoPath,WatermarkLogoSha256=logoSha,WatermarkLogoWidthPercent=relative};return true;}
            if(font.SelectedItem is not WatermarkChoice family){message=global::Orbit.Core.Localization.L.Get("Text_A41D1FF502C2");return false;}
            if(!TryJpegColor(color.Text,out var hex,out _)){message=global::Orbit.Core.Localization.L.Get("Text_E081A6B77843");return false;}
            options=options with{WatermarkText=text.Text,WatermarkFont=family.Id,WatermarkColor=hex,WatermarkFontSizePercent=relative};
            try{options=PresetCatalog.SanitizeOptions(options) with{OutputDirectory=_outputDirectory};return true;}catch(PresetValidationException ex){message=ex.Message;return false;}
        }
        void Update(){if(!Current())return;bool valid=Read(out _,out var message);preview.IsEnabled=valid&&_watermarkPreviewCancellation is null;save.IsEnabled=valid;status.Text=message;choose.IsEnabled=!logoBusy;}
        void Changed(){++editVersion;_watermarkPreviewCancellation?.Cancel();Update();}
        int textSize=initial.WatermarkFontSizePercent,logoSize=initial.WatermarkLogoWidthPercent;bool sizeInitialized=false,lastLogo=false;
        void ModeChanged(){bool logo=(mode.SelectedItem as WatermarkChoice)?.Id=="Logo";
            if(sizeInitialized&&size.SelectedItem is int oldSize){if(lastLogo)logoSize=oldSize;else textSize=oldSize;}lastLogo=logo;sizeInitialized=true;
            textFields.Visibility=fontRow.Visibility=logo?Visibility.Collapsed:Visibility.Visible;logoFields.Visibility=logo?Visibility.Visible:Visibility.Collapsed;
            int value=logo?logoSize:textSize;size.ItemsSource=logo?new[]{1,5,10,15,20,25,33,50,75,100}.Append(value).Distinct().Order().ToArray():Enumerable.Range(1,15).ToArray();size.SelectedItem=value;Changed();}
        async Task InspectLogoAsync(string path){
            var cancellation=CancellationTokenSource.CreateLinkedTokenSource(_resultToolsCancellation.Token);_watermarkLogoCancellation?.Cancel();_watermarkLogoCancellation=cancellation;logoBusy=true;logoPath=logoSha=null;Changed();
            try{var info=await _watermarkTools.InspectLogoAsync(path,cancellation.Token);if(!Current()||cancellation.IsCancellationRequested)return;logoPath=info.Path;logoSha=info.Sha256;logoCaption.Text=Path.GetFileName(info.Path);logoCaption.ToolTip=info.Path;}
            catch(OperationCanceledException){}catch(Exception ex){if(Current())logoCaption.Text=global::Orbit.Core.Localization.L.Get("Text_EB0EBB6E9DEE")+ex.Message;}
            finally{if(ReferenceEquals(_watermarkLogoCancellation,cancellation))_watermarkLogoCancellation=null;cancellation.Dispose();if(Current()){logoBusy=false;Update();}}
        }
        choose.Click+=(_,_)=>{
            if(!Current()||logoBusy)return;string? path;
            if(IsDiagnosticRun)path=_watermarkLogoPickerOverride?.Invoke();else{var dialog=new OpenFileDialog{Title=global::Orbit.Core.Localization.L.Get("Text_2B92A8B1FBED"),Filter=global::Orbit.Core.Localization.L.Get("Text_9938BA8580EC"),Multiselect=false};path=WithPopupInteraction(()=>dialog.ShowDialog(this))==true?dialog.FileName:null;}
            if(path is not null)_=TrackResultTask(InspectLogoAsync(path));
        };
        preview.Click+=(_,_)=>{if(Read(out var options,out _)){int version=editVersion;_ = TrackResultTask(PrepareWatermarkPreviewAsync(paths,options,panelVersion,loadVersion,preview,status,()=>Current()&&editVersion==version,Update));}};
        save.Click+=(_,_)=>{if(Read(out var options,out _))QueueEditing(options);};
        mode.SelectionChanged+=(_,_)=>ModeChanged();text.TextChanged+=(_,_)=>Changed();color.TextChanged+=(_,_)=>Changed();font.SelectionChanged+=(_,_)=>Changed();position.SelectionChanged+=(_,_)=>Changed();size.SelectionChanged+=(_,_)=>Changed();opacity.SelectionChanged+=(_,_)=>Changed();margin.SelectionChanged+=(_,_)=>Changed();ModeChanged();
    }
    private async Task PrepareWatermarkPreviewAsync(string[] paths,ImageJobOptions options,int panelVersion,int loadVersion,Button preview,TextBlock status,Func<bool> editorCurrent,Action refresh)
    {
        var cancellation=CancellationTokenSource.CreateLinkedTokenSource(_resultToolsCancellation.Token);_watermarkPreviewCancellation?.Cancel();_watermarkPreviewCancellation=cancellation;
        string? failure=null;
        bool Current()=>!_closing&&!cancellation.IsCancellationRequested&&panelVersion==_comparisonVersion&&loadVersion==_loadVersion&&paths.SequenceEqual(_files)&&editorCurrent();
        preview.IsEnabled=false;status.Text=global::Orbit.Core.Localization.L.Get("Text_9E390C07391A");
        try{
            var bytes=await _watermarkTools.CreatePreviewAsync(paths[0],ToolkitEngine.WatermarkParameters(options with{OutputDirectory=null}),960,cancellation.Token);
            if(!Current())return;
            var bitmap=new BitmapImage();using(var stream=new MemoryStream(bytes)){bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=stream;bitmap.EndInit();}bitmap.Freeze();
            OpenPanel(global::Orbit.Core.Localization.L.Get("Text_B7DE46282211"),Path.GetFileName(paths[0]));int viewVersion=_comparisonVersion;
            bool ViewCurrent()=>!_closing&&viewVersion==_comparisonVersion&&loadVersion==_loadVersion&&!_selectionLoading&&paths.SequenceEqual(_files);
            var image=new Image{Tag="watermark-preview",Source=bitmap,Height=260,Stretch=Stretch.Uniform,Margin=new Thickness(0,8,0,8)};_panelContent.Children.Add(image);
            _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Format("Text_89377D2CA0D0",(object?)(paths.Length)),12));
            var save=Btn(global::Orbit.Core.Localization.L.Get("Text_6A22508FEEAB"),(_,_)=>{if(ViewCurrent())QueueEditing(options);});save.Tag="watermark-save";GlassAccent(save);_panelContent.Children.Add(save);
            _panelContent.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_E44444CA51E8"),(_,_)=>{if(ViewCurrent())ShowWatermarkEditor(options);}));
            _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_2C9BE2CD06A3"),11));
        }catch(OperationCanceledException){}catch(Exception ex){if(Current())failure=global::Orbit.Core.Localization.L.Get("Text_1FFDF7198A5D")+ex.Message;}
        finally{bool own=ReferenceEquals(_watermarkPreviewCancellation,cancellation);if(own)_watermarkPreviewCancellation=null;cancellation.Dispose();if(own&&panelVersion==_comparisonVersion&&!_closing){refresh();if(failure is not null&&editorCurrent())status.Text=failure;}}
    }
}
