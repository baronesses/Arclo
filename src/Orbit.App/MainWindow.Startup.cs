using System;
using System.Windows;
using System.Windows.Controls;
using Orbit.App.Platform;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private void AddStartupSettings()
    {
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_383AC4908F3E"),15,"#E9F2FF"));
        var input=new TextBox{Text=_shortcut.ToString(),MaxLength=128,Margin=new Thickness(0,3,0,8)};
        _panelContent.Children.Add(input);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_1CFE4E30F47B"),12));
        _panelContent.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_56F921B57317"),(_,_)=>{
            if(!HotkeyGesture.TryParse(input.Text,out var gesture,out var validation)){Toast(validation!);return;}
            if(_bridge==null){Toast(global::Orbit.Core.Localization.L.Get("Text_E86810472FCD"));return;}
            if(!_bridge.TryChangeHotkey(gesture,out var error)){Toast(error??global::Orbit.Core.Localization.L.Get("Text_6E60D01D8A7A"));return;}
            _shortcut=gesture;input.Text=gesture.ToString();_shortcutHint.Text=ShortcutDisplay;if(SavePreferences())Toast(global::Orbit.Core.Localization.L.Get("Text_D58921301544")+ShortcutDisplay);
        }));
        var startup=new CheckBox{Content=global::Orbit.Core.Localization.L.Get("Text_F6629517CE77"),IsChecked=!IsDiagnosticRun&&StartupIntegration.IsEnabled(Environment.ProcessPath)};
        startup.Click+=(_,_)=>{
            if(IsDiagnosticRun){startup.IsChecked=false;return;}
            try{if(startup.IsChecked==true)StartupIntegration.Enable(Environment.ProcessPath!);else StartupIntegration.Disable();Toast(startup.IsChecked==true?global::Orbit.Core.Localization.L.Get("Text_BA4AE4968945"):global::Orbit.Core.Localization.L.Get("Text_7D7BC540C455"));}
            catch(Exception ex){startup.IsChecked=StartupIntegration.IsEnabled(Environment.ProcessPath);Toast(ex.Message);}
        };
        _panelContent.Children.Add(startup);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_78AE7C9A36D2"),11,"#A2B3CF"));
    }
}
