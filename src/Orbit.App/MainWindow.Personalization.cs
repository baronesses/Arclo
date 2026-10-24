using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Orbit.App.Controls;
using Orbit.App.Personalization;
using Orbit.Core;
using Orbit.Core.Presets;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private RadialLayoutStore _layoutStore=new();
    private RadialLayout _radialLayout=new();
    private FavoritePresetStore _favoriteStore=new();
    private IReadOnlyList<string> _favoriteIds=Array.Empty<string>();
    private string? _layoutWarning;
    private string? _favoriteWarning;
    private bool _favoritesBlocked;
    private bool _favoriteBusy;
    private Action? _personalizationStateChanged;
    private bool _allowDiagnosticPersonalizationIo;
    private RepeatRecipe? _repeatRecipe;
    private bool PersonalizationIoAllowed=>!IsDiagnosticRun||_allowDiagnosticPersonalizationIo;

    private async Task LoadPersonalizationAsync()
    {
        if(!PersonalizationIoAllowed)return;
        var layout=_layoutStore.Load();_radialLayout=layout.Layout;_layoutWarning=layout.Warning;
        var favorites=await _favoriteStore.LoadAsync();
        _favoriteIds=favorites.Ids;_favoriteWarning=favorites.Error;_favoritesBlocked=favorites.ChangesBlocked;
        if(!_closing)RefreshRadialOrder();
    }
    private static string LayoutContext(ToolkitKind kind)=>kind==ToolkitKind.Mixed?"Other":kind.ToString();
    private void RefreshRadialOrder()
    {
        if(_radialWorkspacePath is not null||_radialWorkspaceJob is not null){if(_radialWorkspaceVisible)RenderRadialFile();return;}
        if(_browserContext)return;
        if(_selectionLoading)return;
        _keyboardIndex=-1;
        _radial.SetActions(ContextActions(_selectionKind,_selectionMetadata.All(m=>m.HasAudio)));
    }
    private IReadOnlyList<ProcessingPreset> AllPresets()=>PresetCatalog.BuiltIns.Concat(_customPresets).ToArray();
    private void ApplyCurrentRecipe(ProcessingPreset preset)
    {
        string? reason=null;
        if(_closing||_selectionLoading||!PresetCatalog.CanApply(preset,_selectionMetadata,out reason)){
            Toast(_selectionLoading?global::Orbit.Core.Localization.L.Get("Text_0C44195BEC05"):reason??global::Orbit.Core.Localization.L.Get("Text_86CBADF4FE2D"));return;
        }
        if(preset.Options.Action==ImageAction.PdfText&&!_engine.PdfTextAvailable){Toast(global::Orbit.Core.Localization.L.Get("Text_389D4B9122B5"));return;}
        var recipe=PresetCatalog.SanitizeOptions(preset.Options) with{OutputDirectory=_outputDirectory};
        if(recipe.Action==ImageAction.AudioAdjust)ApplyAudioRecipe(recipe);else QueueEditing(recipe);
    }
    private void RememberCompletedRecipe(JobEntry job)
    {
        // Only a successful completed operation can replace the session's reusable recipe.
        // Unsupported/source-bound operations leave the previous reusable recipe available.
        if(RepeatRecipe.TryCapture(job.Options,job.Finished&&job.HistoryStatus==History.HistoryStatus.Succeeded&&job.Result!=null,out var recipe))
            _repeatRecipe=recipe;
    }
    private void ShowRepeatRecipe()
    {
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_D7AA0F548029"),global::Orbit.Core.Localization.L.Get("Text_5025B53B5852"));
        if(_repeatRecipe is not { } recipe){
            _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_EFB71F699A99"),13));return;
        }
        _panelContent.Children.Add(Text(ActionName(recipe.Options.Action),16,"#ECE5FF"));
        _panelContent.Children.Add(Text(recipe.Description,13));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Format("Text_3994B153B112",(object?)(_files.Count)),12));
        bool allowed=recipe.CanApply(_selectionMetadata,out var reason)&&!_selectionLoading;
        if(recipe.Options.Action==ImageAction.PdfText&&!_engine.PdfTextAvailable){allowed=false;reason=global::Orbit.Core.Localization.L.Get("Text_389D4B9122B5");}
        _panelContent.Children.Add(Text(allowed?global::Orbit.Core.Localization.L.Get("Text_A932197FC99C"):reason??global::Orbit.Core.Localization.L.Get("Text_0C44195BEC05"),12,allowed?"#A7D8C8":"#EDACB9"));
        var run=Btn(global::Orbit.Core.Localization.L.Get("Text_047BD276C63A"),(_,_)=>{
            if(_closing||_selectionLoading||!recipe.TryBind(_selectionMetadata,_outputDirectory,out var options,out var error)){Toast(global::Orbit.Core.Localization.L.Get("Text_A315A357BC73"));return;}
            if(options!.Action==ImageAction.AudioAdjust)ApplyAudioRecipe(options);else QueueEditing(options);
        });run.IsEnabled=allowed;GlassAccent(run);_panelContent.Children.Add(run);
    }
    private void ShowFavorites()
    {
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_21275795345F"),global::Orbit.Core.Localization.L.Get("Text_01BA5C0A52FC"));
        _personalizationStateChanged=()=>ShowFavorites();
        if(_favoriteWarning!=null)_panelContent.Children.Add(Text(_favoriteWarning,12,"#EDACB9"));
        if(_favoritesBlocked){
            var reset=Btn(global::Orbit.Core.Localization.L.Get("Text_EBBA231388A7"),(_,_)=>TrackResultTask(ResetFavoritesAsync()));reset.IsEnabled=!_favoriteBusy;_panelContent.Children.Add(reset);
        }
        var presets=AllPresets().ToDictionary(p=>p.Id,StringComparer.Ordinal);
        if(_favoriteIds.Count==0)_panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_47133F9384FF"),13));
        foreach(var id in _favoriteIds){
            var stack=new StackPanel();
            if(presets.TryGetValue(id,out var preset)){
                stack.Children.Add(Text(preset.Name,14,"#E2EAF6"));stack.Children.Add(Text(PresetCatalog.DescribeOptions(preset.Options),12));
                bool allowed=PresetCatalog.CanApply(preset,_selectionMetadata,out var reason)&&!_selectionLoading;
                if(preset.Options.Action==ImageAction.PdfText&&!_engine.PdfTextAvailable){allowed=false;reason=global::Orbit.Core.Localization.L.Get("Text_789B22704151");}
                var run=Btn(global::Orbit.Core.Localization.L.Get("Text_E15F960D93EB"),(_,_)=>ApplyCurrentRecipe(preset));run.IsEnabled=allowed;GlassAccent(run);stack.Children.Add(run);
                if(!allowed)stack.Children.Add(Text(reason??global::Orbit.Core.Localization.L.Get("Text_0C44195BEC05"),11,"#EDACB9"));
            }else stack.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_0012BA04A2D9"),13,"#EDACB9"));
            var remove=Btn(global::Orbit.Core.Localization.L.Get("Text_334C3299A899"),(_,_)=>TrackResultTask(ToggleFavoriteAsync(id,true)));
            remove.IsEnabled=!_favoriteBusy&&!_favoritesBlocked;stack.Children.Add(remove);
            var card=new Border{Child=stack,Padding=new Thickness(12,6,12,12),Margin=new Thickness(0,6,0,4),CornerRadius=new CornerRadius(8),BorderThickness=new Thickness(1)};
            card.SetResourceReference(Border.BackgroundProperty,"GlassControlFill");card.SetResourceReference(Border.BorderBrushProperty,"GlassEdge");_panelContent.Children.Add(card);
        }
        _panelContent.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_2AA17595C868"),(_,_)=>ShowPresets()));
    }
    private void AddFavoriteButton(WrapPanel actions,Func<ProcessingPreset?> getSelected)
    {
        var star=Btn(global::Orbit.Core.Localization.L.Get("Text_8EA1A075844C"),(_,_)=>{
            if(getSelected() is { } preset)TrackResultTask(ToggleFavoriteAsync(preset.Id,_favoriteIds.Contains(preset.Id)));
        });
        void Update(){
            var preset=getSelected();bool favorite=preset!=null&&_favoriteIds.Contains(preset.Id);
            star.Content=favorite?global::Orbit.Core.Localization.L.Get("Text_09D1C277C9C4"):global::Orbit.Core.Localization.L.Get("Text_8EA1A075844C");
            star.IsEnabled=preset!=null&&!_favoriteBusy&&!_favoritesBlocked&&(favorite||_favoriteIds.Count<16);
            star.ToolTip=_favoritesBlocked?global::Orbit.Core.Localization.L.Get("Text_821D33E88BA1"):_favoriteIds.Count>=16&&!favorite?global::Orbit.Core.Localization.L.Get("Text_38740A0272DD"):global::Orbit.Core.Localization.L.Get("Text_5A2FF4AD180F");
        }
        actions.Children.Insert(0,star);_personalizationStateChanged=Update;Update();
        // The picker raises SelectionChanged before the user can activate the button.
        if(_panelContent.Children.OfType<ComboBox>().FirstOrDefault() is { } picker)picker.SelectionChanged+=(_,_)=>Update();
    }
    private async Task ToggleFavoriteAsync(string id,bool remove)
    {
        if(_favoriteBusy||!PersonalizationIoAllowed||_closing)return;
        _favoriteBusy=true;_personalizationStateChanged?.Invoke();
        try{
            var ids=_favoriteIds.Where(x=>x!=id).ToList();if(!remove)ids.Add(id);
            var result=await _favoriteStore.SaveAsync(ids);
            if(result.Success)_favoriteIds=result.Ids;
            _favoritesBlocked=result.ChangesBlocked;_favoriteWarning=result.Error;
            if(result.Error!=null)Toast(result.Error);
        }finally{_favoriteBusy=false;if(!_closing)_personalizationStateChanged?.Invoke();}
    }
    private async Task ResetFavoritesAsync()
    {
        if(_favoriteBusy||!PersonalizationIoAllowed||_closing)return;
        _favoriteBusy=true;_personalizationStateChanged?.Invoke();
        try{
            var result=await _favoriteStore.ResetAsync();
            if(result.Success)_favoriteIds=result.Ids;
            _favoritesBlocked=result.ChangesBlocked;_favoriteWarning=result.Error;
            if(result.Error!=null)Toast(result.Error);
        }finally{_favoriteBusy=false;if(!_closing)_personalizationStateChanged?.Invoke();}
    }
    private void ShowRadialLayout(ToolkitKind? selectedKind=null)
    {
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_2FD9CC2C84D2"),global::Orbit.Core.Localization.L.Get("Text_8FC028E4DE6B"));
        if(_layoutWarning!=null){
            _panelContent.Children.Add(Text(_layoutWarning,12,"#EDACB9"));
            _panelContent.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_298E41327FEE"),(_,_)=>{
                if(!PersonalizationIoAllowed)return;var loaded=_layoutStore.Load();_radialLayout=loaded.Layout;_layoutWarning=loaded.Warning;
                RefreshRadialOrder();ShowRadialLayout(selectedKind);
            }));
            _panelContent.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_64D1BC31797D"),(_,_)=>{
                if(!PersonalizationIoAllowed)return;var reset=_layoutStore.Reset();if(!reset.Success){Toast(reset.Error!);return;}
                _radialLayout=reset.Layout;_layoutWarning=null;RefreshRadialOrder();ShowRadialLayout(selectedKind);
            }));
        }
        var kinds=new[]{ToolkitKind.Image,ToolkitKind.Video,ToolkitKind.Audio,ToolkitKind.Pdf,ToolkitKind.Zip,ToolkitKind.Tar,ToolkitKind.SevenZip,ToolkitKind.Folder,ToolkitKind.Other};
        var names=new[]{global::Orbit.Core.Localization.L.Get("Text_0A6258A5F30F"),global::Orbit.Core.Localization.L.Get("Text_F92FAFB4C00A"),global::Orbit.Core.Localization.L.Get("Text_1718E90D59C5"),"PDF","ZIP","TAR / TAR.GZ","7z",global::Orbit.Core.Localization.L.Get("Text_3005C1BC505F"),global::Orbit.Core.Localization.L.Get("Text_6C042CDECD53")};
        var current=selectedKind??(_selectionKind==ToolkitKind.Mixed?ToolkitKind.Other:_selectionKind);
        var picker=new ComboBox{ItemsSource=names,SelectedIndex=Math.Max(0,Array.IndexOf(kinds,current)),Margin=new Thickness(0,6,0,12)};_panelContent.Children.Add(picker);
        var kind=kinds[picker.SelectedIndex];
        var ordered=ContextActions(kind,true).ToList();
        var rows=new StackPanel();_panelContent.Children.Add(rows);
        void Render(){
            rows.Children.Clear();
            for(int i=0;i<ordered.Count;i++){
                int index=i;var row=new DockPanel{Margin=new Thickness(0,3,0,4)};
                var down=Btn("↓",(_,_)=>{(ordered[index],ordered[index+1])=(ordered[index+1],ordered[index]);Render();});down.IsEnabled=i<ordered.Count-1;down.ToolTip=global::Orbit.Core.Localization.L.Get("Text_74B32503991C");DockPanel.SetDock(down,Dock.Right);row.Children.Add(down);
                var up=Btn("↑",(_,_)=>{(ordered[index],ordered[index-1])=(ordered[index-1],ordered[index]);Render();});up.IsEnabled=i>0;up.ToolTip=global::Orbit.Core.Localization.L.Get("Text_836141628EED");DockPanel.SetDock(up,Dock.Right);row.Children.Add(up);
                row.Children.Add(Text($"{i+1}. {ordered[i].Label}",13));rows.Children.Add(row);
            }
        }
        Render();picker.SelectionChanged+=(_,_)=>ShowRadialLayout(kinds[picker.SelectedIndex]);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_C3A785CA7F3F"),12));
        if(kind==ToolkitKind.Video)_panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_AFDBBE9768D5"),12));
        var save=Btn(global::Orbit.Core.Localization.L.Get("Text_C376B5AF0F0C"),(_,_)=>{
            if(!PersonalizationIoAllowed)return;var result=_layoutStore.SetOrder(LayoutContext(kind),ordered.Select(a=>a.Action).ToArray());
            if(!result.Success){Toast(result.Error!);return;}_radialLayout=result.Layout;RefreshRadialOrder();ClosePanel();Toast(global::Orbit.Core.Localization.L.Get("Text_C0C869450278"));
        });save.IsEnabled=_layoutWarning==null;GlassAccent(save);_panelContent.Children.Add(save);
        var resetCurrent=Btn(global::Orbit.Core.Localization.L.Get("Text_33A3416E4927"),(_,_)=>{
            if(!PersonalizationIoAllowed)return;var result=_layoutStore.ClearOrder(LayoutContext(kind));
            if(!result.Success){Toast(result.Error!);return;}_radialLayout=result.Layout;RefreshRadialOrder();ShowRadialLayout(kind);
        });resetCurrent.IsEnabled=_layoutWarning==null;_panelContent.Children.Add(resetCurrent);
    }
}
