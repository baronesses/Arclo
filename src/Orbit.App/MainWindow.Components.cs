using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Orbit.App.Platform;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private void ShowComponents()
    {
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_1E8466EBEBAF"), global::Orbit.Core.Localization.L.Get("Text_E0E6A5D98A0D"));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_B49DCAAA8359"), 12));
        foreach (var component in AppComponents.Catalog)
        {
            _panelContent.Children.Add(Text(component.Name, 16, "#E9F2FF"));
            _panelContent.Children.Add(Text(component.Purpose, 12));
            string? selected = AppComponents.Selected(component);
            var state = Text(selected is null ? global::Orbit.Core.Localization.L.Get("Text_763FC13A634C") : global::Orbit.Core.Localization.L.Get("Text_86EF7AB4A95F") + selected, 11); _panelContent.Children.Add(state);
            var row = new WrapPanel();
            row.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_87D1B12D1538"), (_, _) =>
            {
                if (IsDiagnosticRun) return;
                try { Process.Start(new ProcessStartInfo(component.Url) { UseShellExecute = true }); } catch (Exception ex) { Toast(ex.Message); }
            }));
            row.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_D36DE2F6497A"), (_, _) =>
            {
                if (IsDiagnosticRun) return;
                var dialog = new OpenFileDialog { Title = global::Orbit.Core.Localization.L.Get("Text_E89E2D5D0071") + component.Name, Filter = component.Executable + "|" + component.Executable, CheckFileExists = true };
                if (dialog.ShowDialog(this) != true) return;
                try { AppComponents.Select(component, dialog.FileName); state.Text = global::Orbit.Core.Localization.L.Get("Text_86EF7AB4A95F") + dialog.FileName; Toast(component.Name + global::Orbit.Core.Localization.L.Get("Text_72D0D5C007B0")); } catch (Exception ex) { Toast(ex.Message); }
            }));
            _panelContent.Children.Add(row);
        }
        string? notices = AppComponents.BundledDirectory;
        if (notices is not null)
        {
            _panelContent.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_B64A4D8B8784"), (_, _) =>
            {
                if (IsDiagnosticRun) return;
                try { Process.Start(new ProcessStartInfo(Path.Combine(notices, "licenses")) { UseShellExecute = true }); } catch (Exception ex) { Toast(ex.Message); }
            }));
        }
    }
}
