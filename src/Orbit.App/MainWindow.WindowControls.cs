using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Orbit.Core.Localization;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private Button? _minimizeButton;

    private Button CreateMinimizeButton()
    {
        var button=Btn("−",(_,_)=>MinimizeMainWindow());
        button.Tag="WindowMinimize";
        AutomationProperties.SetAutomationId(button,"WindowMinimize");
        button.Width=34;
        button.MinWidth=34;
        button.FontSize=18;
        button.Padding=new Thickness(5,0,5,1);
        _minimizeButton=button;
        RefreshWindowControlLocalization();
        return button;
    }

    private void MinimizeMainWindow()
    {
        if(_closing||_compact)return;
        // Standard taskbar minimization keeps the window and all active jobs alive.
        WindowState=WindowState.Minimized;
    }

    private void RefreshWindowControlLocalization()
    {
        if(_minimizeButton is null)return;
        var label=L.Get("Window.Minimize");
        _minimizeButton.ToolTip=label;
        AutomationProperties.SetName(_minimizeButton,label);
        AutomationProperties.SetHelpText(_minimizeButton,label);
    }
}
