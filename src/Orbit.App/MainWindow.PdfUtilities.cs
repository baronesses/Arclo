using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Orbit.Core;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private void ShowPdfUtilities()
    {
        if(!CheckPdfSelection(false))return;
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_A7244CFC8CF9"),_files.Count==1?System.IO.Path.GetFileName(_files[0]):$"PDF: {_files.Count}");
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_28927326F67C"),15,"#E9E3FF"));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_E77D0D224DCD"),12));
        _panelContent.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_6C23796E3D6B"),(_,_)=>ShowPdfUtility(ImageAction.PdfText)));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_B7A65B9C4A18"),15,"#E9E3FF"));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_D9D9694D00FD"),12));
        _panelContent.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_1776C1D19FF4"),(_,_)=>ShowPdfUtility(ImageAction.PdfEmbeddedImages)));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_1D5FC6C94768"),15,"#E9E3FF"));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_B67655C14B7C"),12));
        _panelContent.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_501386FC78EC"),(_,_)=>ShowPdfUtility(ImageAction.PdfOptimize)));
    }

    private void ShowPdfUtility(ImageAction action)
    {
        if(action is not(ImageAction.PdfText or ImageAction.PdfEmbeddedImages or ImageAction.PdfOptimize)||!CheckPdfSelection(false))return;
        bool available=action switch{ImageAction.PdfText=>_engine.PdfTextAvailable,ImageAction.PdfEmbeddedImages=>_engine.PdfImagesAvailable,_=>_engine.PdfOptimizeAvailable};
        OpenPanel(ActionName(action),_files.Count==1?System.IO.Path.GetFileName(_files[0]):global::Orbit.Core.Localization.L.Format("Text_2DF51C86FFF5",(object?)(_files.Count)));
        CheckBox? option=null;
        if(action==ImageAction.PdfText){
            _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_29D56A68D133"),13));
            option=new CheckBox{Content=global::Orbit.Core.Localization.L.Get("Text_082BF8036307"),IsChecked=true,Margin=new Thickness(0,6,0,8)};_panelContent.Children.Add(option);
            _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_EB9BF18C2951"),12));
        }else if(action==ImageAction.PdfEmbeddedImages){
            _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_78218E02DA44"),13));
            _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_AB22248DB6E0"),12));
            _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_B65290FCA2DB"),12));
        }else{
            _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_21E9732086AB"),13));
            option=new CheckBox{Content=global::Orbit.Core.Localization.L.Get("Text_BC7A47BB9D4F"),IsChecked=false,Margin=new Thickness(0,6,0,8)};_panelContent.Children.Add(option);
            _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_38BB775C6732"),12));
            _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_DC2CB270A0F7"),12));
        }
        if(!available)_panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_7A0444BDDD95"),12,"#EDACB9"));
        _panelContent.Children.Add(Text(action==ImageAction.PdfEmbeddedImages?global::Orbit.Core.Localization.L.Get("Text_C627548AC57C"):global::Orbit.Core.Localization.L.Get("Text_6DE80835F528"),11));
        int version=_loadVersion;var paths=_files.ToArray();
        var run=Btn(action switch{ImageAction.PdfText=>global::Orbit.Core.Localization.L.Get("Text_7D4AA8E11BFF"),ImageAction.PdfEmbeddedImages=>global::Orbit.Core.Localization.L.Get("Text_6A22508FEEAB"),_=>global::Orbit.Core.Localization.L.Get("Text_974DC9B31142")},(_,_)=>{
            if(version!=_loadVersion||!CheckPdfSelection(false)||!paths.SequenceEqual(_files)){Toast(global::Orbit.Core.Localization.L.Get("Text_4E5758BA6091"));return;}
            QueueEditing(new ImageJobOptions{Action=action,OutputDirectory=_outputDirectory,
                PdfTextPreserveLayout=action!=ImageAction.PdfText||option?.IsChecked==true,
                PdfOptimizeLinearize=action==ImageAction.PdfOptimize&&option?.IsChecked==true});
        });run.IsEnabled=available;GlassAccent(run);_panelContent.Children.Add(run);
        _panelContent.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_643A899928B6"),(_,_)=>ShowPdfUtilities()));
    }
}
