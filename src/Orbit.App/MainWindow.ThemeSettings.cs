using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Orbit.App.Appearance;
using Orbit.App.Controls;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private void WrapSettingsTabs(bool showThemes)
    {
        var general=new StackPanel{Tag="GeneralSettings"};
        general.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_D300A1C79BC3"),15,"#E9F2FF"));
        general.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_F44AC1FA3391"),(_,_)=>ShowMoreTools()));
        general.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_DFF729E1AF66"),(_,_)=>ShowJobs()));
        general.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_B97E1C1B4327"),(_,_)=>SetCompactMode(false)));
        var windowActions=new WrapPanel();windowActions.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_E9647145BFA2"),(_,_)=>HideToTray()));windowActions.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_5690C2E63B9D"),(_,_)=>Quit()));general.Children.Add(windowActions);
        general.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_75160B86A895"),15,"#E9F2FF"));
        while(_panelContent.Children.Count>2){var item=_panelContent.Children[2];_panelContent.Children.RemoveAt(2);general.Children.Add(item);}
        var tabs=new TabControl{Tag="SettingsTabs",Margin=new Thickness(0,12,0,0),Background=Brushes.Transparent,BorderThickness=new Thickness(0)};
        tabs.Items.Add(new TabItem{Header=global::Orbit.Core.Localization.L.Get("Text_72E67FB326D9"),Content=general});
        tabs.Items.Add(new TabItem{Header=global::Orbit.Core.Localization.L.Get("Text_79237E0E2D43"),Content=BuildThemeSettings(),Tag="ThemesTab"});
        tabs.SelectedIndex=showThemes?1:0;
        tabs.SelectionChanged+=(_,e)=>{if(ReferenceEquals(e.Source,tabs)&&_panel.Child is ScrollViewer scroll)scroll.ScrollToTop();};
        _panelContent.Children.Add(tabs);tabs.ApplyTemplate();UpdateLayout();
    }

    private StackPanel BuildThemeSettings()
    {
        var content=new StackPanel{Tag="ThemeSettings"};
        content.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_1731619EFA3A"),18,"#E9F2FF"));
        content.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_2A4EA52B7FD5"),12));
        content.Children.Add(ThemeChoice(OrbitTheme.LiquidGlassCrimson,"Liquid Glass · Crimson Noir",global::Orbit.Core.Localization.L.Get("Text_3C5C3CF29B72"),"#02060E","#C50337"));
        content.Children.Add(ThemeChoice(OrbitTheme.Light,global::Orbit.Core.Localization.L.Get("Text_F0CD3A603372"),global::Orbit.Core.Localization.L.Get("Text_6217B3318066"),"#F8FAFC","#68788C"));
        content.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_C6053ACCD282"),15,"#E9F2FF"));
        content.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_8EE0600729C1"),12));
        content.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_B32C7F37F702"),12));
        return content;
    }

    private RadioButton ThemeChoice(OrbitTheme theme,string title,string description,string first,string second)
    {
        var body=new StackPanel();
        var heading=Text(title,14,"#E9F2FF");heading.FontWeight=FontWeights.SemiBold;body.Children.Add(heading);
        body.Children.Add(Text(description,12));
        var colors=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,4,0,0)};
        foreach(string color in new[]{first,second})colors.Children.Add(new Border{Width=30,Height=18,CornerRadius=new CornerRadius(4),Margin=new Thickness(0,0,6,0),Background=RadialMenu.Brush(color),BorderThickness=new Thickness(1),BorderBrush=RadialMenu.Brush("#70FFFFFF")});
        body.Children.Add(colors);
        var choice=new RadioButton{Content=body,GroupName="OrbitTheme",IsChecked=_theme==theme,Tag=theme,Margin=new Thickness(0,5,0,7),HorizontalContentAlignment=HorizontalAlignment.Stretch};
        AutomationProperties.SetName(choice,title);
        choice.Checked+=async(_,_)=>{_theme=theme;ApplyGlassAppearance();SavePreferences();if(_popupBackdropFrame==null)await RecaptureThemeBackdropAsync();};
        return choice;
    }
}
