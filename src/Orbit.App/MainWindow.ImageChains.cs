using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Orbit.App.Chains;
using Orbit.Core;
using Orbit.Core.Chains;
using Orbit.Core.Presets;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private ImageChainStore _chainStore=new();
    private IReadOnlyList<ImageChainDefinition> _savedChains=Array.Empty<ImageChainDefinition>();
    private string? _chainWarning;
    private bool _allowDiagnosticChainIo;
    private bool ChainIoAllowed=>!IsDiagnosticRun||_allowDiagnosticChainIo;

    private void ShowImageChains()
    {
        if(ChainIoAllowed){var loaded=_chainStore.Load();_savedChains=loaded.Chains;_chainWarning=loaded.Warning;}
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_9F0303A8F9C9"),global::Orbit.Core.Localization.L.Get("Text_6868819C8044"));
        if(_chainWarning!=null){
            _panelContent.Children.Add(Text(_chainWarning,12,"#EDACB9"));
            _panelContent.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_6890AC64535C"),(_,_)=>{
                if(!ChainIoAllowed)return;var reset=_chainStore.Reset();if(!reset.Success){Toast(reset.Error!);return;}_savedChains=reset.Chains;_chainWarning=null;ShowImageChains();
            }));
        }
        var create=Btn(global::Orbit.Core.Localization.L.Get("Text_8B99A2235EA9"),(_,_)=>ShowImageChainEditor());create.IsEnabled=_chainWarning==null&&_savedChains.Count<16;_panelContent.Children.Add(create);
        if(_savedChains.Count==0)_panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_7A99E761F13E"),13));
        foreach(var chain in _savedChains){
            var stack=new StackPanel();stack.Children.Add(Text(chain.Name,15,"#E2EAF6"));stack.Children.Add(Text(ImageChainPolicy.DescribeSteps(chain.Steps),12));
            bool allowed=ImageChainPolicy.CanApply(chain,_selectionMetadata,out var reason)&&!_selectionLoading;
            var run=Btn(global::Orbit.Core.Localization.L.Get("Text_05CDF0DD4E92"),(_,_)=>RunImageChain(chain));run.IsEnabled=allowed;GlassAccent(run);stack.Children.Add(run);
            if(!allowed)stack.Children.Add(Text(reason??global::Orbit.Core.Localization.L.Get("Text_0C44195BEC05"),11,"#EDACB9"));
            var actions=new WrapPanel();actions.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_B8B151D41743"),(_,_)=>ShowImageChainEditor(chain)));
            actions.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_BE99B1361201"),(_,_)=>{
                if(!ChainIoAllowed)return;var result=_chainStore.Delete(chain.Id);if(!result.Success){Toast(result.Error!);return;}_savedChains=result.Chains;ShowImageChains();
            }));stack.Children.Add(actions);
            var card=new Border{Child=stack,Padding=new Thickness(12,6,12,12),Margin=new Thickness(0,6,0,4),CornerRadius=new CornerRadius(8),BorderThickness=new Thickness(1)};
            card.SetResourceReference(Border.BackgroundProperty,"GlassControlFill");card.SetResourceReference(Border.BorderBrushProperty,"GlassEdge");_panelContent.Children.Add(card);
        }
    }
    private void RunImageChain(ImageChainDefinition chain)
    {
        if(_closing||_selectionLoading||!ImageChainPolicy.CanApply(chain,_selectionMetadata,out var reason)){Toast(global::Orbit.Core.Localization.L.Get("Text_F03617F97BFA"));return;}
        QueueEditing(new ImageJobOptions{Action=ImageAction.ImageChain,OutputDirectory=_outputDirectory,ChainSteps=ImageChainPolicy.SanitizeSteps(chain.Steps)});
    }
    private static bool IsImageChainStep(ProcessingPreset preset)=>preset.Options.Action is ImageAction.ImageTransform or ImageAction.Resize or ImageAction.RemoveImageMetadata
        or ImageAction.Jpg or ImageAction.WebP or ImageAction.Avif or ImageAction.Compress;
    private void ShowImageChainEditor(ImageChainDefinition? existing=null)
    {
        OpenPanel(existing==null?global::Orbit.Core.Localization.L.Get("Text_938AA0CA626B"):global::Orbit.Core.Localization.L.Get("Text_FB1B2E3DDE48"),global::Orbit.Core.Localization.L.Get("Text_4F73EB3AF179"));
        var name=new TextBox{Text=existing?.Name??global::Orbit.Core.Localization.L.Get("Text_810AA6842C45"),MaxLength=64,Margin=new Thickness(0,4,0,7)};_panelContent.Children.Add(name);
        var steps=(existing?.Steps??new[]{new ImageJobOptions{Action=ImageAction.Resize,Width=1600,NoUpscale=true},new ImageJobOptions{Action=ImageAction.WebP,Quality=82}}).ToList();
        var candidates=PresetCatalog.BuiltIns.Concat(_customPresets).Where(IsImageChainStep).ToList();
        void Extra(string id,string title,ImageJobOptions options)=>candidates.Add(new ProcessingPreset(id,title,PresetCatalog.DescribeOptions(options),options,true));
        Extra("editor-rotate90",global::Orbit.Core.Localization.L.Get("Text_3A51055A2B1D"),new(){Action=ImageAction.ImageTransform,RotationDegrees=90});
        Extra("editor-strip",global::Orbit.Core.Localization.L.Get("Text_8376D742D543"),new(){Action=ImageAction.RemoveImageMetadata});
        var rows=new StackPanel();_panelContent.Children.Add(rows);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_C4A1A72A000F"),12));
        var picker=new ComboBox{ItemsSource=candidates,DisplayMemberPath="Name",SelectedIndex=0,Margin=new Thickness(0,2,0,6)};_panelContent.Children.Add(picker);
        var add=new Button{Content=global::Orbit.Core.Localization.L.Get("Text_DA7CAACA0CE9"),Margin=new Thickness(3,0,3,0)};_panelContent.Children.Add(add);
        var message=Text("",12);_panelContent.Children.Add(message);
        var save=new Button{Content=global::Orbit.Core.Localization.L.Get("Text_8555D74B91C6"),FontSize=12,Padding=new Thickness(10,7,10,7),Margin=new Thickness(3,5,3,5)};
        var run=new Button{Content=global::Orbit.Core.Localization.L.Get("Text_1096D5DCB9C9"),FontSize=12,Padding=new Thickness(10,7,10,7),Margin=new Thickness(3,5,3,5)};GlassAccent(run);
        var commands=new WrapPanel();commands.Children.Add(save);commands.Children.Add(run);_panelContent.Children.Add(commands);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_96E82459908A"),11));
        void Update(){
            add.IsEnabled=steps.Count<4;
            try{
                var chain=ImageChainPolicy.Create(name.Text,steps,existing?.Id);save.IsEnabled=_chainWarning==null;
                bool allowed=ImageChainPolicy.CanApply(chain,_selectionMetadata,out var reason)&&!_selectionLoading;run.IsEnabled=allowed;
                message.Text=allowed?global::Orbit.Core.Localization.L.Format("Text_04B250F902E8",(object?)(steps.Count),(object?)(_files.Count)):reason??global::Orbit.Core.Localization.L.Get("Text_0C44195BEC05");message.Foreground=Ink(allowed?"#A7D8C8":"#BAC6DE");
            }catch(Exception ex){save.IsEnabled=run.IsEnabled=false;message.Text=ex.Message;message.Foreground=Ink("#EDACB9");}
        }
        void Render(){
            rows.Children.Clear();for(int i=0;i<steps.Count;i++){
                int index=i;var stack=new StackPanel();stack.Children.Add(Text($"{i+1}. {ActionName(steps[i].Action)}",13,"#E2EAF6"));stack.Children.Add(Text(PresetCatalog.DescribeOptions(steps[i]),11));
                var actions=new WrapPanel();var up=Btn("↑",(_,_)=>{(steps[index],steps[index-1])=(steps[index-1],steps[index]);Render();});up.IsEnabled=i>0;
                var down=Btn("↓",(_,_)=>{(steps[index],steps[index+1])=(steps[index+1],steps[index]);Render();});down.IsEnabled=i<steps.Count-1;
                actions.Children.Add(up);actions.Children.Add(down);actions.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_07CB65087477"),(_,_)=>{steps.RemoveAt(index);Render();}));stack.Children.Add(actions);rows.Children.Add(stack);
            }Update();
        }
        add.Click+=(_,_)=>{if(steps.Count<4&&picker.SelectedItem is ProcessingPreset preset){steps.Add(PresetCatalog.SanitizeOptions(preset.Options));Render();}};
        save.Click+=(_,_)=>{
            if(!ChainIoAllowed)return;
            try{var result=existing==null?_chainStore.Add(name.Text,steps):_chainStore.Update(existing.Id,name.Text,steps);
                if(!result.Success){Toast(result.Error!);return;}_savedChains=result.Chains;_chainWarning=null;ShowImageChains();}
            catch(Exception ex){Toast(ex.Message);}
        };
        run.Click+=(_,_)=>{try{RunImageChain(ImageChainPolicy.Create(name.Text,steps,existing?.Id));}catch(Exception ex){Toast(ex.Message);}};
        name.TextChanged+=(_,_)=>Update();Render();
    }
}
