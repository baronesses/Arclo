using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Orbit.App.Controls;
using Orbit.Core;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private sealed record ImageOptionChoice(string Id,string Label){public override string ToString()=>Label;}
    private bool CheckEditableImages(bool single=false)
    {
        if(_selectionLoading||_files.Count==0||_selectionMetadata.Any(m=>m.Kind!=ToolkitKind.Image)||single&&_files.Count!=1){Toast(single?global::Orbit.Core.Localization.L.Get("Text_72F0A9ABCDE3"):global::Orbit.Core.Localization.L.Get("Text_F731E73149BC"));return false;}return true;
    }
    private bool SameImageSelection(int version,string[] paths,bool single=false)
    {
        if(version!=_loadVersion||!CheckEditableImages(single)||!paths.SequenceEqual(_files)){Toast(global::Orbit.Core.Localization.L.Get("Text_4E5758BA6091"));return false;}return true;
    }
    private void ShowImageResize()
    {
        if(!CheckEditableImages()||_metadata==null)return;
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_3837BA14575C"),_files.Count==1?Path.GetFileName(_files[0]):global::Orbit.Core.Localization.L.Format("Text_0293E80E9CC4",(object?)(_files.Count)));
        var mode=new ComboBox{Tag="resize-mode",ItemsSource=new[]{global::Orbit.Core.Localization.L.Get("Text_C6FF51D9A1FD"),global::Orbit.Core.Localization.L.Get("Text_AF87930200B0")},SelectedIndex=0,Margin=new Thickness(0,1,0,5)};_panelContent.Children.Add(mode);
        var pixels=new Grid();pixels.ColumnDefinitions.Add(new ColumnDefinition());pixels.ColumnDefinitions.Add(new ColumnDefinition());
        var wcol=new StackPanel{Margin=new Thickness(0,0,5,0)};wcol.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_45B5E94DDEA8"),12));var width=new TextBox{Tag="resize-width",Text=Math.Min(1920,_metadata.Width).ToString(),MaxLength=6};wcol.Children.Add(width);pixels.Children.Add(wcol);
        var hcol=new StackPanel{Margin=new Thickness(5,0,0,0)};hcol.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_47DCC74F054D"),12));var height=new TextBox{Tag="resize-height",Text="",MaxLength=6};hcol.Children.Add(height);Grid.SetColumn(hcol,1);pixels.Children.Add(hcol);_panelContent.Children.Add(pixels);
        var percentBox=new StackPanel{Visibility=Visibility.Collapsed};percentBox.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_83FF27B09768"),12));var percent=new TextBox{Tag="resize-percent",Text="50",MaxLength=12};percentBox.Children.Add(percent);_panelContent.Children.Add(percentBox);
        var aspect=new CheckBox{Content=global::Orbit.Core.Localization.L.Get("Text_9988E40194B7"),IsChecked=true,Margin=new Thickness(0,5,0,2)};_panelContent.Children.Add(aspect);
        var noUpscale=new CheckBox{Content=global::Orbit.Core.Localization.L.Get("Text_A29032FA21FA"),IsChecked=true,Margin=new Thickness(0,3,0,3)};_panelContent.Children.Add(noUpscale);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_116383043FF5"),12));
        var filter=new ComboBox{Tag="resize-filter",ItemsSource=new[]{new ImageOptionChoice("Auto",global::Orbit.Core.Localization.L.Get("Text_7930CB4A40E8")),new ImageOptionChoice("Lanczos",global::Orbit.Core.Localization.L.Get("Text_1AC0FD535F2C")),new ImageOptionChoice("Mitchell",global::Orbit.Core.Localization.L.Get("Text_DC2BCFB09699")),new ImageOptionChoice("Point",global::Orbit.Core.Localization.L.Get("Text_DD57449D0200"))},SelectedIndex=0,Margin=new Thickness(0,1,0,5)};_panelContent.Children.Add(filter);
        var preview=Text("",12);_panelContent.Children.Add(preview);
        var help=Text("",11);_panelContent.Children.Add(help);
        int version=_loadVersion;var paths=_files.ToArray();
        var run=Btn(global::Orbit.Core.Localization.L.Get("Text_3837BA14575C"),(_,_)=>{
            if(!SameImageSelection(version,paths)||!Read(out var options,out _))return;
            QueueEditing(options! with{OutputDirectory=_outputDirectory});
        });GlassAccent(run);_panelContent.Children.Add(run);
        bool Read(out ImageJobOptions? options,out string message){
            options=null;message=global::Orbit.Core.Localization.L.Get("Text_85C4061CBB0C");
            try{
                double? scale=null;int? w=null,h=null;
                if(mode.SelectedIndex==1){
                    if(!double.TryParse(percent.Text.Replace(',','.'),NumberStyles.Float,CultureInfo.InvariantCulture,out var value)||!double.IsFinite(value)||value is <1 or >400){message=global::Orbit.Core.Localization.L.Get("Text_94758E70D7D5");return false;}scale=value;
                }else{
                    w=ParseDimension(width.Text);h=ParseDimension(height.Text);
                    if(w==null&&h==null){message=global::Orbit.Core.Localization.L.Get("Text_7014813106A2");return false;}
                    if(aspect.IsChecked!=true&&(w==null||h==null)){message=global::Orbit.Core.Localization.L.Get("Text_403C3E16CC2F");return false;}
                }
                options=new ImageJobOptions{Action=ImageAction.Resize,ResizePercent=scale,ResizeFilter=((ImageOptionChoice)filter.SelectedItem).Id,
                    Width=w,Height=h,MaintainAspectRatio=scale!=null||aspect.IsChecked==true,NoUpscale=noUpscale.IsChecked==true};
                var first=_metadata!;double factor=scale.HasValue?scale.Value/100:Math.Min(w.HasValue?(double)w.Value/first.Width:double.PositiveInfinity,h.HasValue?(double)h.Value/first.Height:double.PositiveInfinity);
                if(options.NoUpscale)factor=Math.Min(1,factor);
                int ow,oh;
                if(options.MaintainAspectRatio){
                    double rw=Math.Round(first.Width*factor,MidpointRounding.AwayFromZero),rh=Math.Round(first.Height*factor,MidpointRounding.AwayFromZero);
                    if(!double.IsFinite(rw)||!double.IsFinite(rh)||rw>100000||rh>100000){message=global::Orbit.Core.Localization.L.Get("Text_EF340788D8F3");options=null;return false;}
                    ow=Math.Max(1,(int)rw);oh=Math.Max(1,(int)rh);
                }
                else{ow=options.NoUpscale?Math.Min(first.Width,w!.Value):w!.Value;oh=options.NoUpscale?Math.Min(first.Height,h!.Value):h!.Value;}
                if(ow>100000||oh>100000){message=global::Orbit.Core.Localization.L.Get("Text_EF340788D8F3");options=null;return false;}
                message=(_files.Count>1?global::Orbit.Core.Localization.L.Get("Text_D64817EF4D46"):global::Orbit.Core.Localization.L.Get("Text_BADC97D3B036"))+$"{ow} × {oh} px";return true;
            }catch(Exception ex){message=ex.Message;return false;}
        }
        void Update(){pixels.Visibility=mode.SelectedIndex==0?Visibility.Visible:Visibility.Collapsed;percentBox.Visibility=mode.SelectedIndex==1?Visibility.Visible:Visibility.Collapsed;aspect.Visibility=pixels.Visibility;
            help.Text=mode.SelectedIndex==1?global::Orbit.Core.Localization.L.Get("Text_657EDD2B07B9")
                :aspect.IsChecked==true?global::Orbit.Core.Localization.L.Get("Text_ACB6E52B1854")
                :global::Orbit.Core.Localization.L.Get("Text_E5C080B3F7E4");
            run.IsEnabled=Read(out _,out var message);preview.Text=message;preview.Foreground=Ink(run.IsEnabled?"#A7D8C8":"#EDACB9");}
        mode.SelectionChanged+=(_,_)=>Update();filter.SelectionChanged+=(_,_)=>Update();width.TextChanged+=(_,_)=>Update();height.TextChanged+=(_,_)=>Update();percent.TextChanged+=(_,_)=>Update();aspect.Click+=(_,_)=>Update();noUpscale.Click+=(_,_)=>Update();Update();
    }

    private static bool TryJpegColor(string text,out string canonical,out Color color)
    {
        canonical=text.ToUpperInvariant();color=Colors.White;
        if(canonical.Length!=7||canonical[0]!='#'||!canonical.AsSpan(1).ToArray().All(char.IsAsciiHexDigit))return false;
        int rgb=int.Parse(canonical.AsSpan(1),NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture);color=Color.FromRgb((byte)(rgb>>16),(byte)(rgb>>8),(byte)rgb);return true;
    }
    private void ShowJpegExport()
    {
        if(!CheckEditableImages())return;
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_325F75D801E4"),_files.Count==1?Path.GetFileName(_files[0]):global::Orbit.Core.Localization.L.Format("Text_0293E80E9CC4",(object?)(_files.Count)));
        var qualityLabel=Text(global::Orbit.Core.Localization.L.Get("Text_A38311F4D827"),12);_panelContent.Children.Add(qualityLabel);var quality=new Slider{Minimum=1,Maximum=100,Value=85,IsSnapToTickEnabled=true,TickFrequency=1,Margin=new Thickness(2,0,2,6)};_panelContent.Children.Add(quality);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_BD05C38FF828"),12));
        var color=new TextBox{Tag="jpeg-background",Text="#FFFFFF",CharacterCasing=CharacterCasing.Upper,MaxLength=7};_panelContent.Children.Add(color);
        var choices=new WrapPanel{Margin=new Thickness(0,5,0,5)};foreach(var (name,value) in new[]{(global::Orbit.Core.Localization.L.Get("Text_A024A93D4E0C"),"#FFFFFF"),(global::Orbit.Core.Localization.L.Get("Text_1CA9B6B05DFC"),"#000000"),(global::Orbit.Core.Localization.L.Get("Text_EA1AE91DFEBC"),"#808080")}){
            var button=Btn(name,(_,_)=>color.Text=value);button.FontSize=12;button.Padding=new Thickness(10,6,10,6);choices.Children.Add(button);}_panelContent.Children.Add(choices);
        var matte=new Border{Height=130,CornerRadius=new CornerRadius(10),BorderThickness=new Thickness(1),BorderBrush=Ink("#65819C"),Child=new Image{Source=_preview,Stretch=Stretch.Uniform}};_panelContent.Children.Add(matte);
        var status=Text("",11);_panelContent.Children.Add(status);_panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_715C7425958B"),11));
        int version=_loadVersion;var paths=_files.ToArray();var run=Btn(global::Orbit.Core.Localization.L.Get("Text_029A51331026"),(_,_)=>{
            if(!SameImageSelection(version,paths)||!TryJpegColor(color.Text,out var value,out _))return;
            QueueEditing(new ImageJobOptions{Action=ImageAction.Jpg,Quality=(int)quality.Value,JpegBackgroundColor=value,OutputDirectory=_outputDirectory});
        });GlassAccent(run);_panelContent.Children.Add(run);
        void Update(){run.IsEnabled=TryJpegColor(color.Text,out var value,out var rgb);status.Text=run.IsEnabled?global::Orbit.Core.Localization.L.Get("Text_EB0B5D2BEA8D")+value:global::Orbit.Core.Localization.L.Get("Text_A419999D2231");status.Foreground=Ink(run.IsEnabled?"#A7D8C8":"#EDACB9");if(run.IsEnabled)matte.Background=new SolidColorBrush(rgb);}
        color.TextChanged+=(_,_)=>Update();quality.ValueChanged+=(_,_)=>qualityLabel.Text=global::Orbit.Core.Localization.L.Format("Text_1F18BBACAA50",(object?)((int)quality.Value));Update();
    }

    private void ShowImageCrop()
    {
        if(!CheckEditableImages(true)||_metadata==null)return;
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_5F7D48FBC737"),Path.GetFileName(_files[0]));
        var crop=new CropPreview{Source=_preview,PixelWidth=_metadata.Width,PixelHeight=_metadata.Height,Height=140};crop.Reset();_panelContent.Children.Add(crop);
        var ratio=new ComboBox{Tag="crop-ratio",ItemsSource=new[]{global::Orbit.Core.Localization.L.Get("Text_330060A8D5A0"),"1:1","4:3","16:9","9:16"},SelectedIndex=0,Margin=new Thickness(0,5,0,3)};_panelContent.Children.Add(ratio);
        var coordinates=new Grid();for(int i=0;i<4;i++)coordinates.ColumnDefinitions.Add(new ColumnDefinition());
        var fields=new TextBox[4];var labels=new[]{"X","Y",global::Orbit.Core.Localization.L.Get("Text_A97D46CDFEE7"),global::Orbit.Core.Localization.L.Get("Text_CC8385071149")};for(int i=0;i<4;i++){
            var column=new StackPanel{Margin=new Thickness(i==0?0:3,0,i==3?0:3,0)};column.Children.Add(Text(labels[i],11));fields[i]=new TextBox{Tag="crop-"+i,MaxLength=6,FontSize=12,Padding=new Thickness(6,7,6,7)};column.Children.Add(fields[i]);Grid.SetColumn(column,i);coordinates.Children.Add(column);}_panelContent.Children.Add(coordinates);
        var buttons=new WrapPanel{Margin=new Thickness(0,5,0,0)};var apply=Btn(global::Orbit.Core.Localization.L.Get("Text_F76ADCC274A0"),(_,_)=>{});apply.FontSize=11;apply.Padding=new Thickness(8,6,8,6);buttons.Children.Add(apply);
        var reset=Btn(global::Orbit.Core.Localization.L.Get("Text_94178161E303"),(_,_)=>crop.Reset());reset.FontSize=11;reset.Padding=new Thickness(8,6,8,6);buttons.Children.Add(reset);_panelContent.Children.Add(buttons);
        var status=Text("",11);_panelContent.Children.Add(status);_panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_5DE9B94FFF69"),11));
        bool syncing=false,dirty=false;int version=_loadVersion;var paths=_files.ToArray();
        var run=Btn(global::Orbit.Core.Localization.L.Get("Text_01934448231D"),(_,_)=>{
            if(dirty||!SameImageSelection(version,paths,true))return;var r=crop.Selection;
            QueueEditing(new ImageJobOptions{Action=ImageAction.Crop,CropX=r.X,CropY=r.Y,CropWidth=r.Width,CropHeight=r.Height,OutputDirectory=_outputDirectory});
        });GlassAccent(run);_panelContent.Children.Add(run);
        bool Read(out Int32Rect rectangle){rectangle=default;var values=new int[4];for(int i=0;i<4;i++)if(!int.TryParse(fields[i].Text,out values[i]))return false;
            if(values[0]<0||values[1]<0||values[2]<=0||values[3]<=0||(long)values[0]+values[2]>crop.PixelWidth||(long)values[1]+values[3]>crop.PixelHeight)return false;
            rectangle=new Int32Rect(values[0],values[1],values[2],values[3]);return true;}
        void Sync(){syncing=true;try{var r=crop.Selection;fields[0].Text=r.X.ToString();fields[1].Text=r.Y.ToString();fields[2].Text=r.Width.ToString();fields[3].Text=r.Height.ToString();dirty=false;status.Text=$"{r.Width} × {r.Height} px · X {r.X}, Y {r.Y}";status.Foreground=Ink("#A7D8C8");run.IsEnabled=r.Width>0&&r.Height>0;apply.IsEnabled=false;}finally{syncing=false;}}
        foreach(var field in fields)field.TextChanged+=(_,_)=>{if(syncing)return;dirty=true;run.IsEnabled=false;apply.IsEnabled=Read(out _);status.Text=apply.IsEnabled?global::Orbit.Core.Localization.L.Get("Text_465D70CA4B53"):global::Orbit.Core.Localization.L.Get("Text_E1F263E4E05C");status.Foreground=Ink("#EDACB9");};
        apply.Click+=(_,_)=>{if(!Read(out var r))return;if(crop.TrySetSelection(r,out var reason))Sync();else{status.Text=reason;status.Foreground=Ink("#EDACB9");}};
        crop.SelectionChanged+=_=>Sync();ratio.SelectionChanged+=(_,_)=>crop.AspectRatio=ratio.SelectedIndex switch{1=>1d,2=>4d/3,3=>16d/9,4=>9d/16,_=>null};Sync();
    }
}
