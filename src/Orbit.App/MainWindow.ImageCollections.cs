using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Orbit.Core;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private void ShowImageCollection(ImageAction action)
    {
        if(_selectionLoading||_files.Count==0||_selectionMetadata.Any(m=>m.Kind!=ToolkitKind.Image)){
            Toast(global::Orbit.Core.Localization.L.Get("Text_C3C0DE67539F"));return;
        }
        if(_files.Count>100){Toast(global::Orbit.Core.Localization.L.Get("Text_F29A958972BD"));return;}
        bool pdf=action==ImageAction.ImagesToPdf;
        OpenPanel(pdf?global::Orbit.Core.Localization.L.Get("Text_59B928F728DE"):global::Orbit.Core.Localization.L.Get("Text_BB910CF02A32"),pdf?global::Orbit.Core.Localization.L.Get("Text_675F83544A4A"):global::Orbit.Core.Localization.L.Get("Text_04A4125DAFB5"));
        var paths=new ObservableCollection<string>(_files);
        var list=new ListBox{ItemsSource=paths,Height=120,Margin=new Thickness(0,4,0,7),HorizontalContentAlignment=HorizontalAlignment.Stretch};
        StyleImageCollectionList(list);
        var caption=new FrameworkElementFactory(typeof(TextBlock));
        caption.SetBinding(TextBlock.TextProperty,new Binding{Converter=new PdfFileCaption()});
        caption.SetBinding(ToolTipProperty,new Binding());caption.SetValue(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis);
        caption.SetValue(FrameworkElement.MarginProperty,new Thickness(3,5,3,5));list.ItemTemplate=new DataTemplate{VisualTree=caption};
        ScrollViewer.SetHorizontalScrollBarVisibility(list,ScrollBarVisibility.Disabled);_panelContent.Children.Add(list);
        var order=new WrapPanel();
        order.Children.Add(Btn("↑",(_,_)=>{int i=list.SelectedIndex;if(i>0){paths.Move(i,i-1);list.SelectedIndex=i-1;}}));
        order.Children.Add(Btn("↓",(_,_)=>{int i=list.SelectedIndex;if(i>=0&&i<paths.Count-1){paths.Move(i,i+1);list.SelectedIndex=i+1;}}));
        order.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_75E1A73CAA1C"),(_,_)=>{var sorted=paths.OrderBy(Path.GetFileName,StringComparer.CurrentCultureIgnoreCase).ToArray();paths.Clear();foreach(var path in sorted)paths.Add(path);}));
        order.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_07CB65087477"),(_,_)=>{if(list.SelectedIndex>=0)paths.RemoveAt(list.SelectedIndex);}));
        _panelContent.Children.Add(order);
        var count=Text("",11);_panelContent.Children.Add(count);
        var pageSize=new ComboBox{ItemsSource=new[]{global::Orbit.Core.Localization.L.Get("Text_916E1247845C"),global::Orbit.Core.Localization.L.Get("Text_8747AD50B7A2"),global::Orbit.Core.Localization.L.Get("Text_090A9179D54A")},SelectedIndex=0,Margin=new Thickness(0,3,0,6)};
        var margin=new TextBox{Text="10",MaxLength=8,Margin=new Thickness(0,3,0,6)};
        var quality=new Slider{Minimum=1,Maximum=100,Value=90,TickFrequency=1,IsSnapToTickEnabled=true,Margin=new Thickness(2,4,2,7)};
        var qualityLabel=Text(global::Orbit.Core.Localization.L.Get("Text_C927012E0B23"),11);
        var columns=new ComboBox{ItemsSource=new[]{1,2,3,4,5,6},SelectedIndex=2,Margin=new Thickness(0,3,0,6)};
        var size=new ComboBox{ItemsSource=new[]{160,320,640},SelectedIndex=1,Margin=new Thickness(0,3,0,6)};
        var white=new CheckBox{Content=global::Orbit.Core.Localization.L.Get("Text_55258047A1D1"),IsChecked=true,Margin=new Thickness(0,5,0,7)};
        var previewInfo=Text("",11);
        if(pdf){
            _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_5BD783CD31FC"),12));_panelContent.Children.Add(pageSize);
            _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_C87BB3E1D614"),12));_panelContent.Children.Add(margin);
            _panelContent.Children.Add(qualityLabel);_panelContent.Children.Add(quality);
            _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_96EFDB3B4C84"),11));
        }else{
            _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_B3112872372C"),12));_panelContent.Children.Add(columns);
            _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_FF14F9DB9D9C"),12));_panelContent.Children.Add(size);_panelContent.Children.Add(white);
            _panelContent.Children.Add(previewInfo);_panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_23EDFA3C9494"),11));
        }
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_8D28D0EED49A"),11));
        var run=Btn(pdf?global::Orbit.Core.Localization.L.Get("Text_C9D76D7B989C"):global::Orbit.Core.Localization.L.Get("Text_0834FD6463DB"),(_,_)=>{
            if(_closing||paths.Count==0)return;
            var ordered=paths.ToArray();
            try{
                double mm=0;
                if(pdf&&pageSize.SelectedIndex!=2&&(!double.TryParse(margin.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out mm)
                    &&!double.TryParse(margin.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out mm)
                    ||!double.IsFinite(mm)||mm is <0 or >40))throw new InvalidOperationException(global::Orbit.Core.Localization.L.Get("Text_3A46F4645AAF"));
                var options=new ImageJobOptions{Action=action,OutputDirectory=_outputDirectory,Quality=(int)quality.Value,
                    ImagePdfPageSize=pageSize.SelectedIndex switch{1=>"A4Landscape",2=>"FitImage",_=>"A4"},ImagePdfMarginMillimeters=mm,
                    ContactSheetColumns=(int)columns.SelectedItem,ContactSheetThumbnailSize=(int)size.SelectedItem,ContactSheetWhiteBackground=white.IsChecked==true};
                Enqueue(ordered[0],options,ordered);if(_compact)ClosePanel();else ShowJobs();
            }catch(Exception ex){Toast(ex.Message);}
        });
        GlassAccent(run);run.Margin=new Thickness(0,7,0,0);_panelContent.Children.Add(run);
        void Update(){
            count.Text=global::Orbit.Core.Localization.L.Format("Text_0293E80E9CC4",(object?)(paths.Count));
            int col=(int)columns.SelectedItem,tile=(int)size.SelectedItem,rows=(paths.Count+col-1)/col;
            int w=col*tile+(col+1)*12,h=rows*tile+(rows+1)*12;
            bool fits=(long)w*h<=40_000_000&&w<=16000&&h<=16000;
            previewInfo.Text=$"{w} × {h} px · PNG"+(fits?"":global::Orbit.Core.Localization.L.Get("Text_BF5DC7F81D61"));
            run.IsEnabled=paths.Count>0&&(pdf||fits);
            margin.IsEnabled=pageSize.SelectedIndex!=2;
        }
        paths.CollectionChanged+=(_,_)=>Update();columns.SelectionChanged+=(_,_)=>Update();size.SelectionChanged+=(_,_)=>Update();
        pageSize.SelectionChanged+=(_,_)=>Update();quality.ValueChanged+=(_,_)=>qualityLabel.Text=global::Orbit.Core.Localization.L.Format("Text_CD07997FA429",(object?)((int)quality.Value));
        list.SelectedIndex=0;Update();
    }

    private void ShowMetadataCleaner()
    {
        if(_selectionLoading||_files.Count==0||_selectionMetadata.Any(m=>m.Kind!=ToolkitKind.Image)){
            Toast(global::Orbit.Core.Localization.L.Get("Text_B5EA73B7CD63"));return;
        }
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_EC034C354A73"),_files.Count==1?Path.GetFileName(_files[0]):global::Orbit.Core.Localization.L.Format("Text_BB11F685FDB9",(object?)(_files.Count)));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_B9382BD04B52"),13));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_F37A99E18FEF"),12));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_0D559EA0CF5D"),12));
        _panelContent.Children.Add(Text(_outputDirectory??global::Orbit.Core.Localization.L.Get("Text_7469553AEF5B"),11));
        var run=Btn(global::Orbit.Core.Localization.L.Get("Text_C0DE7ED12A27"),(_,_)=>QueueEditing(new ImageJobOptions{Action=ImageAction.RemoveImageMetadata,OutputDirectory=_outputDirectory}));
        GlassAccent(run);run.Margin=new Thickness(0,10,0,0);_panelContent.Children.Add(run);
    }

    private void StyleImageCollectionList(ListBox list,bool virtualize=false)
    {
        list.SetResourceReference(Control.BackgroundProperty,"GlassFieldFill");list.SetResourceReference(Control.ForegroundProperty,"TextBrush");
        list.SetResourceReference(Control.BorderBrushProperty,"GlassEdge");list.BorderThickness=new Thickness(1);
        var border=new FrameworkElementFactory(typeof(Border));border.SetValue(Border.CornerRadiusProperty,new CornerRadius(8));
        border.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty,new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty,new TemplateBindingExtension(Control.BorderThicknessProperty));border.SetValue(Border.PaddingProperty,new Thickness(3));
        var scroll=new FrameworkElementFactory(typeof(ScrollViewer),"PART_ScrollViewer");
        scroll.SetValue(ScrollViewer.CanContentScrollProperty,virtualize);
        scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty,ScrollBarVisibility.Auto);scroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty,ScrollBarVisibility.Disabled);
        scroll.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));border.AppendChild(scroll);
        list.Template=new ControlTemplate(typeof(ListBox)){VisualTree=border};
        var style=new Style(typeof(ListBoxItem));style.Setters.Add(new Setter(Control.BackgroundProperty,System.Windows.Media.Brushes.Transparent));
        style.Setters.Add(new Setter(Control.ForegroundProperty,new DynamicResourceExtension("TextBrush")));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Stretch));
        var row=new FrameworkElementFactory(typeof(Border),"Row");row.SetValue(Border.CornerRadiusProperty,new CornerRadius(8));
        row.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(Control.BackgroundProperty));
        var content=new FrameworkElementFactory(typeof(ContentPresenter));content.SetValue(FrameworkElement.MarginProperty,new Thickness(4,2,4,2));row.AppendChild(content);
        var template=new ControlTemplate(typeof(ListBoxItem)){VisualTree=row};
        var hover=new Trigger{Property=UIElement.IsMouseOverProperty,Value=true};hover.Setters.Add(new Setter(Control.BackgroundProperty,new DynamicResourceExtension("GlassControlHover")));template.Triggers.Add(hover);
        var selected=new Trigger{Property=ListBoxItem.IsSelectedProperty,Value=true};selected.Setters.Add(new Setter(Control.BackgroundProperty,new DynamicResourceExtension("GlassSelection")));template.Triggers.Add(selected);
        style.Setters.Add(new Setter(Control.TemplateProperty,template));list.ItemContainerStyle=style;
    }
}
