using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Orbit.App.Controls;
using Orbit.App.Editing;
using Orbit.Core;

namespace Orbit.App;

public sealed partial class MainWindow
{
    // The caller owns async metadata/current-session guards. This card binds directly to
    // its completed result, never to the unrelated Explorer/global selection.
    private void BuildDownloadedMediaEditor(JobEntry job, ToolkitMetadata metadata)
    {
        if (_closing || job.Result is null) return;
        var path = job.Result.OutputPath;
        if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(metadata.Path), StringComparison.OrdinalIgnoreCase)
            || metadata.Kind is not (ToolkitKind.Audio or ToolkitKind.Video)
            || !double.IsFinite(metadata.DurationSeconds) || metadata.DurationSeconds is <= 0 or > 86400)
        { Toast(global::Orbit.Core.Localization.L.Get("Text_A39B7CAF1FE0"));return; }
        var video = metadata.Kind == ToolkitKind.Video;
        var duration = metadata.DurationSeconds;
        OpenPanel(video ? global::Orbit.Core.Localization.L.Get("Text_AC0C8620DBB6") : global::Orbit.Core.Localization.L.Get("Text_653F03C94726"), Path.GetFileName(path));
        var version = _comparisonVersion;
        bool Current() => !_closing && version == _comparisonVersion && _panel.Visibility == Visibility.Visible;
        if (_panelContent.Children.Count > 1 && _panelContent.Children[1] is TextBlock filename)
        { filename.Tag = "DownloadCurrentFile";filename.ToolTip = path; }
        var file = Text(metadata.Description + " · " + FormatBytes(metadata.Bytes), 12);file.Tag = "DownloadCurrentMetadata";file.ToolTip = path;file.Margin = new Thickness(0, 2, 0, 4);
        _panelContent.Children.Add(file);
        if (job.AutomaticCopySucceeded is { } copied)
        {
            var copyStatus = Text(copied ? global::Orbit.Core.Localization.L.Get("Text_4630B7A941FA") : global::Orbit.Core.Localization.L.Get("Text_ABFB4728D730"), 11);
            copyStatus.Tag = "DownloadCurrentCopyStatus";copyStatus.Margin = new Thickness(0, 0, 0, 4);
            copyStatus.SetResourceReference(TextBlock.ForegroundProperty, copied ? "GlassMutedText" : "TextBrush");
            _panelContent.Children.Add(copyStatus);
        }

        var pane = new MediaPreviewPane(path, video, duration, mute: IsDiagnosticRun, fullLength: true)
        { Tag = "DownloadCurrentPlayer", Height = video ? (_compact ? 175 : 240) : (_compact ? 110 : 135), Margin = new Thickness(0, 0, 0, 3) };
        _mediaPreviewPane = pane;
        _mediaPreviewResult = null; // Existing lifecycle closes playback; it must never delete this result.
        var host = new Border { Child = pane };_mediaPreviewHost = host;_panelContent.Children.Add(host);

        var timeline = new TrimTimeline(duration) { Tag = "DownloadTrimTimeline", Height = 90, Margin = new Thickness(0, 0, 0, 1) };
        _panelContent.Children.Add(timeline);

        var range = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        range.ColumnDefinitions.Add(new ColumnDefinition());range.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });range.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel();var right = new StackPanel();Grid.SetColumn(right, 2);range.Children.Add(left);range.Children.Add(right);
        var aLabel = Text(global::Orbit.Core.Localization.L.Get("Text_EBB0A86A5340"), 12);var bLabel = Text(global::Orbit.Core.Localization.L.Get("Text_17A53D4D277C"), 12);aLabel.Margin = bLabel.Margin = new Thickness(0, 0, 0, 2);
        left.Children.Add(aLabel);right.Children.Add(bLabel);
        var start = new TextBox { Text = "00:00:00.000", MaxLength = 32, Tag = "DownloadTrimStart", MinHeight = 34, Padding = new Thickness(6, 4, 6, 4) };
        var end = new TextBox { Text = MediaTimecode.Format(duration), MaxLength = 32, Tag = "DownloadTrimEnd", MinHeight = 34, Padding = new Thickness(6, 4, 6, 4) };
        AutomationProperties.SetName(start, global::Orbit.Core.Localization.L.Get("Text_295D91A1FED7"));AutomationProperties.SetName(end, global::Orbit.Core.Localization.L.Get("Text_A1D18CD1447B"));
        start.ToolTip = end.ToolTip = global::Orbit.Core.Localization.L.Get("Text_8DEC41B5525D");
        left.Children.Add(start);right.Children.Add(end);
        var markStart = Btn(global::Orbit.Core.Localization.L.Get("Text_4D03964EE12B"), (_, _) => { if (Current() && pane.IsOpened) start.Text = MediaTimecode.Format(pane.PositionSeconds); });
        var markEnd = Btn(global::Orbit.Core.Localization.L.Get("Text_AC5004AD6BAB"), (_, _) => { if (Current() && pane.IsOpened) end.Text = MediaTimecode.Format(Math.Min(duration, pane.PositionSeconds)); });
        markStart.Tag = "DownloadTrimMarkStart";markEnd.Tag = "DownloadTrimMarkEnd";
        markStart.ToolTip = global::Orbit.Core.Localization.L.Get("Text_D30DDC006D93");markEnd.ToolTip = global::Orbit.Core.Localization.L.Get("Text_D25E79502DD6");
        markStart.Margin = markEnd.Margin = new Thickness(0, 3, 0, 0);markStart.Padding = markEnd.Padding = new Thickness(6, 4, 6, 4);
        markStart.FontSize = markEnd.FontSize = 11;markStart.IsEnabled = markEnd.IsEnabled = false;
        left.Children.Add(markStart);right.Children.Add(markEnd);_panelContent.Children.Add(range);
        var interval = Text("", 12);interval.Tag = "DownloadTrimRange";interval.Margin = new Thickness(0, 2, 0, 4);_panelContent.Children.Add(interval);
        var save = Btn(video ? global::Orbit.Core.Localization.L.Get("Text_B52E1DC1EB8C") : global::Orbit.Core.Localization.L.Get("Text_04A389DE6511"), (_, _) => { });
        save.Tag = "DownloadTrimSave";save.Margin = new Thickness(0, 0, 0, 6);GlassAccent(save);
        bool Read(out double from, out double to)
        {
            var parsed = MediaTimecode.TryParse(start.Text, out from) & MediaTimecode.TryParse(end.Text, out to);
            if (parsed && to > duration && to - duration <= .001) to = duration;
            return parsed && from >= 0 && from < to && to <= duration;
        }
        bool synchronizing = false;
        void Update()
        {
            if (synchronizing) return;
            var valid = Read(out var from, out var to);save.IsEnabled = valid && Current();
            interval.Text = valid ? global::Orbit.Core.Localization.L.Get("Text_19855B50B769") + (to - from < .001 ? (to - from).ToString("0.###############", CultureInfo.InvariantCulture) + global::Orbit.Core.Localization.L.Get("Text_DDAE8EB8BF21") : MediaTimecode.Format(to - from))
                : global::Orbit.Core.Localization.L.Get("Text_91DC11EB3CBE");
            interval.SetResourceReference(TextBlock.ForegroundProperty, valid ? "GlassMutedText" : "TextBrush");
            if (valid)
            {
                synchronizing = true;
                try { timeline.SetRange(from, to); }
                finally { synchronizing = false; }
            }
        }
        void WriteRange(double from, double to)
        {
            var first = MediaTimecode.Format(from);var last = MediaTimecode.Format(to);
            if (!MediaTimecode.TryParse(first, out var roundedFrom) || !MediaTimecode.TryParse(last, out var roundedTo) || roundedFrom >= roundedTo)
            { first = from.ToString("0.###############", CultureInfo.InvariantCulture);last = to.ToString("0.###############", CultureInfo.InvariantCulture); }
            synchronizing = true;
            try { start.Text = first;end.Text = last; }
            finally { synchronizing = false; }
            Update();
        }
        start.TextChanged += (_, _) => Update();end.TextChanged += (_, _) => Update();
        timeline.SelectionChanged += (_, _) => { if (!synchronizing && Current()) WriteRange(timeline.StartSeconds, timeline.EndSeconds); };
        pane.Opened += (_, _) => { if (Current()) markStart.IsEnabled = markEnd.IsEnabled = true; };
        pane.Failed += (_, _) => { markStart.IsEnabled = markEnd.IsEnabled = false; };
        save.Click += (_, _) =>
        {
            if (!Current() || !Read(out var from, out var to)) { Update();return; }
            pane.Pause();
            EnqueueDownloadEdit(path, new ImageJobOptions { Action = video ? ImageAction.VideoTrim : ImageAction.AudioTrim,
                TrimStartSeconds = from, TrimEndSeconds = to, OutputDirectory = _outputDirectory ?? Path.GetDirectoryName(path) });
        };
        _panelContent.Children.Add(save);
        _panelContent.Children.Add(Text(video ? global::Orbit.Core.Localization.L.Get("Text_6261EFD28AFA") : global::Orbit.Core.Localization.L.Get("Text_32BE92D5B478"), 11));
        var actions = new WrapPanel { Margin = new Thickness(-3, 1, -3, 0) };
        var copy = Btn(global::Orbit.Core.Localization.L.Get("Text_35032A378518"), async (_, _) => { if (Current()) await CopyKnownResultAsync(path, false); });copy.Tag = "DownloadCurrentCopy";
        var open = Btn(global::Orbit.Core.Localization.L.Get("Text_EA1F40824607"), async (_, _) => { if (Current()) await OpenKnownResultAsync(path, false); });open.Tag = "DownloadCurrentOpen";
        var reveal = Btn(global::Orbit.Core.Localization.L.Get("Text_A99564C5702F"), async (_, _) => { if (Current()) await OpenKnownResultAsync(path, true); });reveal.Tag = "DownloadCurrentReveal";
        var history = Btn(global::Orbit.Core.Localization.L.Get("Text_1417B7792D57"), (_, _) => { if (Current()) ShowJobs(); });history.Tag = "DownloadCurrentHistory";
        actions.Children.Add(copy);actions.Children.Add(open);actions.Children.Add(reveal);actions.Children.Add(history);_panelContent.Children.Add(actions);
        if (!video)
        {
            _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_90C94281FC69"), 12));
            var conversion = new Grid();conversion.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(95) });conversion.ColumnDefinitions.Add(new ColumnDefinition());
            var format = new ComboBox { ItemsSource = new[] { "MP3", "WAV", "FLAC" }, SelectedIndex = 0, Tag = "DownloadExportFormat", Margin = new Thickness(0, 3, 8, 3) };
            AutomationProperties.SetName(format, global::Orbit.Core.Localization.L.Get("Text_65ED2C5E26C9"));conversion.Children.Add(format);
            var export = Btn(global::Orbit.Core.Localization.L.Get("Text_4FD0643205CA"), (_, _) =>
            {
                if (!Current()) return;
                var action = format.SelectedIndex switch { 1 => ImageAction.AudioWav, 2 => ImageAction.AudioFlac, _ => ImageAction.AudioMp3 };
                pane.Pause();EnqueueDownloadEdit(path, new ImageJobOptions { Action = action, OutputDirectory = _outputDirectory ?? Path.GetDirectoryName(path) });
            });
            export.Tag = "DownloadExportSave";export.ToolTip = global::Orbit.Core.Localization.L.Get("Text_ED1E098F7B5C");
            Grid.SetColumn(export, 1);conversion.Children.Add(export);_panelContent.Children.Add(conversion);
        }
        var more = Btn(global::Orbit.Core.Localization.L.Get("Text_5060D71C541D"), async (_, _) =>
        {
            if (!Current()) return;
            await OpenDownloadedFileToolsAsync(job);
        });
        more.Tag = "DownloadCurrentTools";more.Margin = new Thickness(0, 5, 0, 0);
        more.ToolTip = global::Orbit.Core.Localization.L.Get("Text_B9ACE895EE0B");
        _panelContent.Children.Add(more);WriteRange(0, duration);
    }
}
