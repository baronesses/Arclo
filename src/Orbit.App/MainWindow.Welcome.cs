using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private bool _welcomeCompleted;
    // Only diagnostic code supplies this hook; normal completion uses SavePreferences.
    private Func<bool>? _welcomeSaveOverride;

    private bool ShouldShowWelcome() =>
        !IsDiagnosticRun && !_welcomeCompleted && !_closing && IsLoaded && IsVisible &&
        !_compact && !_selectionLoading && _pendingPopupInvocation == 0 &&
        _files.Count == 0 && _loadVersion == 0 && _invocationVersion == 0 &&
        _panel.Visibility != Visibility.Visible &&
        !_args.Contains("--background", StringComparer.Ordinal) &&
        !_args.Any(argument => !argument.StartsWith("--", StringComparison.Ordinal));

    private void ShowWelcome(bool firstRun = false)
    {
        if (_closing || _selectionLoading || firstRun && !ShouldShowWelcome()) return;
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_EFB0FA58E252"), global::Orbit.Core.Localization.L.Get("Text_69A26B860387"));
        int panelVersion = _comparisonVersion;
        int selectionVersion = _loadVersion;
        var selection = _files.ToArray();
        var content = new StackPanel { Tag = "welcome-content" };
        _panelContent.Children.Add(content);
        int rendering = 0;

        bool Current() => !_closing && panelVersion == _comparisonVersion &&
            selectionVersion == _loadVersion && !_selectionLoading &&
            _panel.Visibility == Visibility.Visible && selection.SequenceEqual(_files);

        // The generic panel close button is replaced so a detached welcome header cannot
        // close a newer card when an old control is invoked by a pending routed command.
        if (_panelContent.Children[0] is DockPanel header && header.Children.OfType<Button>().FirstOrDefault() is { } inheritedClose)
        {
            int index = header.Children.IndexOf(inheritedClose);
            header.Children.RemoveAt(index);
            var close = Btn("‹", (_, _) => { if (Current()) ClosePanel(); });
            close.Tag = "welcome-close";
            close.Padding = new Thickness(7, 2, 7, 2);
            DockPanel.SetDock(close, Dock.Right);
            header.Children.Insert(index, close);
        }

        void Render(int step)
        {
            if (!Current() || step is < 0 or > 2) return;
            int thisRendering = ++rendering;
            bool ThisStep() => Current() && thisRendering == rendering;
            content.Children.Clear();

            var progress = new Grid { Margin = new Thickness(0, 7, 0, 7), Tag = "welcome-progress" };
            for (var index = 0; index < 3; index++)
            {
                progress.ColumnDefinitions.Add(new ColumnDefinition());
                var segment = new Border
                {
                    Height = 4,
                    CornerRadius = new CornerRadius(2),
                    Margin = new Thickness(index == 0 ? 0 : 4, 0, index == 2 ? 0 : 4, 0),
                    Opacity = index <= step ? 1 : .3
                };
                segment.SetResourceReference(Border.BackgroundProperty, index <= step ? "GlassFocusEdge" : "GlassEdge");
                Grid.SetColumn(segment, index);
                progress.Children.Add(segment);
            }
            content.Children.Add(progress);
            var counter = Text(global::Orbit.Core.Localization.L.Format("Text_54E8196EF4EA",(object?)(step + 1)), 11);
            counter.Tag = "welcome-step";
            counter.SetResourceReference(TextBlock.ForegroundProperty, "GlassMutedText");
            content.Children.Add(counter);

            var card = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(15, 12, 15, 13),
                Margin = new Thickness(0, 4, 0, 10)
            };
            card.SetResourceReference(Border.BackgroundProperty, "GlassFieldFill");
            card.SetResourceReference(Border.BorderBrushProperty, "GlassEdge");
            var body = new StackPanel();
            card.Child = body;
            var number = Text("0" + (step + 1), 36);
            number.FontWeight = FontWeights.Light;
            number.Margin = new Thickness(0, 0, 0, 6);
            number.SetResourceReference(TextBlock.ForegroundProperty, "GlassFocusEdge");
            body.Children.Add(number);
            string title = step switch
            {
                0 => global::Orbit.Core.Localization.L.Get("Text_5450BB7C2B70"),
                1 => global::Orbit.Core.Localization.L.Get("Text_49F5D314B7C5"),
                _ => global::Orbit.Core.Localization.L.Get("Text_B19EB1785C37")
            };
            var heading = Text(title, 18);
            heading.FontWeight = FontWeights.SemiBold;
            heading.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            body.Children.Add(heading);

            if (step == 0)
            {
                body.Children.Add(WelcomeText(global::Orbit.Core.Localization.L.Get("Text_D29E8C1BEB36")));
                body.Children.Add(WelcomeText(global::Orbit.Core.Localization.L.Get("Text_4936AA8D8CA1")));
            }
            else if (step == 1)
            {
                var shortcut = Text(ShortcutDisplay, 20);
                shortcut.Tag = "welcome-shortcut";
                shortcut.FontWeight = FontWeights.SemiBold;
                shortcut.Margin = new Thickness(0, 6, 0, 9);
                shortcut.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                body.Children.Add(shortcut);
                body.Children.Add(WelcomeText(global::Orbit.Core.Localization.L.Get("Text_92DEBEFC0479")));
            }
            else
            {
                body.Children.Add(WelcomeText(global::Orbit.Core.Localization.L.Get("Text_45135BC081FF")));
                body.Children.Add(WelcomeText(global::Orbit.Core.Localization.L.Get("Text_B56B9033BE86")));
            }
            content.Children.Add(card);
            string tip = step switch
            {
                0 => global::Orbit.Core.Localization.L.Get("Text_02F6ED257824"),
                1 => global::Orbit.Core.Localization.L.Get("Text_71E4A18733A2"),
                _ => global::Orbit.Core.Localization.L.Get("Text_6020F5EC4923")
            };
            var hint = Text(tip, 12);
            hint.SetResourceReference(TextBlock.ForegroundProperty, "GlassMutedText");
            content.Children.Add(hint);

            var status = Text("", 11);
            status.Tag = "welcome-status";
            status.Visibility = Visibility.Collapsed;
            content.Children.Add(status);
            var actions = new Grid { Margin = new Thickness(0, 10, 0, 1) };
            actions.ColumnDefinitions.Add(new ColumnDefinition());
            actions.ColumnDefinitions.Add(new ColumnDefinition());
            var back = Btn(global::Orbit.Core.Localization.L.Get("Text_1A9FB1F3CF8E"), (_, _) => { if (ThisStep()) Render(step - 1); });
            back.Tag = "welcome-back";
            back.IsEnabled = step > 0;
            back.Margin = new Thickness(0, 0, 5, 0);
            actions.Children.Add(back);
            var next = Btn(step == 2 ? global::Orbit.Core.Localization.L.Get("Text_D09CEF487B9E") : global::Orbit.Core.Localization.L.Get("Text_DC9450D2C273"), (_, _) =>
            {
                if (!ThisStep()) return;
                if (step < 2) { Render(step + 1); return; }
                bool previous = _welcomeCompleted;
                _welcomeCompleted = true;
                bool saved = IsDiagnosticRun ? _welcomeSaveOverride?.Invoke() ?? true : SavePreferences();
                if (!saved)
                {
                    _welcomeCompleted = previous;
                    status.Text = global::Orbit.Core.Localization.L.Get("Text_4D0AFEB36386");
                    status.Visibility = Visibility.Visible;
                    return;
                }
                if (ThisStep()) ClosePanel();
            });
            next.Tag = step == 2 ? "welcome-finish" : "welcome-next";
            next.IsDefault = true;
            next.Margin = new Thickness(5, 0, 0, 0);
            GlassAccent(next);
            Grid.SetColumn(next, 1);
            actions.Children.Add(next);
            content.Children.Add(actions);
        }

        Render(0);
    }

    private static TextBlock WelcomeText(string text)
    {
        var block = Text(text, 13);
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        block.Margin = new Thickness(0, 3, 0, 7);
        return block;
    }
}
