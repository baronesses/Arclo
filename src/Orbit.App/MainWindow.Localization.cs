using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Orbit.Core.Localization;
using Orbit.App.History;
using Orbit.App.Platform;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private bool _languageSettingsVisible;
    private bool _refreshingLanguage;
    private sealed record LanguageChoice(string Id,string Label);
    private void InitializeLocalization()
    {
        L.LanguageChanged+=LanguageChanged;
        Closed+=(_,_)=>L.LanguageChanged-=LanguageChanged;
    }
    private void AddLanguageSettings()
    {
        _panelContent.Children.Add(Text(L.Get("Language.Title"),15,"#E9F2FF"));
        var languages=new ComboBox {
            Tag="LanguageSelector",DisplayMemberPath=nameof(LanguageChoice.Label),SelectedValuePath=nameof(LanguageChoice.Id),
            ItemsSource=new[]{new LanguageChoice("auto",L.Get("Language.Auto")),new LanguageChoice("ru",L.Get("Language.Russian")),new LanguageChoice("en",L.Get("Language.English"))},
            SelectedValue=L.Language,Margin=new Thickness(0,3,0,12),MinWidth=180
        };
        AutomationProperties.SetName(languages,L.Get("Language.Title"));
        AutomationProperties.SetAutomationId(languages,"LanguageSelector");
        languages.SelectionChanged+=(_,_)=>{
            if(_refreshingLanguage||languages.SelectedItem is not LanguageChoice choice||choice.Id==L.Language)return;
            string previous=L.Language;L.SetLanguage(choice.Id);
            if(!SavePreferences())L.SetLanguage(previous);
        };
        _panelContent.Children.Add(languages);
    }
    private void LanguageChanged(object? sender,EventArgs args)
    {
        if(_closing)return;
        if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke(DispatcherPriority.Normal,new Action(()=>LanguageChanged(sender,args)));return;}
        if(_refreshingLanguage)return;
        _refreshingLanguage=true;
        try {
            RefreshWindowControlLocalization();
            foreach(var button in _header is Panel header ? header.Children.OfType<StackPanel>().SelectMany(stack=>stack.Children.OfType<Button>()) : Enumerable.Empty<Button>()) {
                switch(button.Tag as string){
                    case "Header.Files":button.Content=L.Get("Header.Files");button.ToolTip=L.Get("Header.Files.Help");break;
                    case "Header.Folders":button.Content=L.Get("Header.Folders");button.ToolTip=L.Get("Header.Folders.Help");break;
                    case "Header.Compact":button.ToolTip=L.Get("Header.Compact.Help");break;
                    case "Header.More":button.ToolTip=L.Get("Header.More.Help");break;
                }
            }
            if(_invocationHelp is not null)_invocationHelp.Text=L.Get("Footer.Invoke");
            _radial.SettingsButton.ToolTip=L.Get("Radial.Settings");
            AutomationProperties.SetName(_radial.SettingsButton,L.Get("Radial.Settings"));
            AutomationProperties.SetHelpText(_radial.SettingsButton,L.Get("Radial.Settings.Help"));
            AutomationProperties.SetName(_radial.CenterCard,L.Get("Radial.ChooseFiles"));
            AutomationProperties.SetHelpText(_radial.CenterCard,L.Get("Radial.ChooseFiles.Help"));
            foreach(var job in _jobs) {
                if(job.HistoryStatus==HistoryStatus.Succeeded)job.State=L.Get("Job.Done");
                else if(job.HistoryStatus==HistoryStatus.Failed)job.State=L.Get("Job.Failed");
                else if(job.HistoryStatus==HistoryStatus.Cancelled)job.State=L.Get("Job.Cancelled");
                else if(job.HistoryStatus==HistoryStatus.Queued)job.State=L.Get(_queueRunControl.IsPaused?"Job.Paused":"Job.Queued");
                else if(job.HistoryStatus==HistoryStatus.Running)job.State=L.Get(job.Cancellation.IsCancellationRequested?"Job.Cancelling":job.Result is not null?"Job.Copying":job.Download is not null?"Job.Downloading":"Job.Running");
            }
            if(_files.Count==0&&!_browserContext){_status.Text=L.Get("Status.ChooseFiles");_selectionInfo.Text=L.Get("Status.FileTypes");}
            else if(_files.Count==1&&_metadata is not null){_selectionInfo.Text=_metadata.Description+" · "+FormatBytes(_metadata.Bytes);}
            if(_radialWorkspaceVisible)RenderRadialFile();
            else if(_browserContext)PublishBrowserContext(new BrowserUrlResult(_browserUrl,null));
            else if(!_browserContext){
                _radial.SetActions(ContextActions(_selectionKind,_selectionMetadata.All(m=>m.HasAudio)));
                if(_files.Count==0){_radial.FileName.Text=L.Get("Radial.EmptyFile");_radial.CenterHint.Text=L.Get("Radial.DropHere");_radial.Metadata.Text=L.Get("Radial.ClickCenter");}
            }
            _bridge?.RefreshLocalization();
            UpdateJobsButton();RefreshJobs();
            if(_languageSettingsVisible){
                bool themes=_panelContent.Children.OfType<TabControl>().FirstOrDefault()?.SelectedIndex==1;
                double offset=(_panel.Child as ScrollViewer)?.VerticalOffset??0;
                ShowSettings(themes);if(_panel.Child is ScrollViewer scroll)scroll.ScrollToVerticalOffset(offset);
            }
        }
        finally{_refreshingLanguage=false;}
    }
    private TextBlock? _invocationHelp;
}
