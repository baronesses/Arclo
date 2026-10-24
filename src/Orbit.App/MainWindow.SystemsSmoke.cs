using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Orbit.App.Controls;
using Orbit.App.Platform;
using Orbit.App.Settings;
using Orbit.Core;
using Orbit.Core.Documents;

namespace Orbit.App;

public sealed partial class MainWindow
{
    /// <summary>Real WPF-to-core systems diagnostic; never installs a hotkey or writes startup/user settings.</summary>
    private async Task RunSystemsSmokeAsync()
    {
        var destination = Path.Combine(AppContext.BaseDirectory, "systems-smoke");
        var fixtures = Path.Combine(destination, "fixtures");
        var output = Path.Combine(destination, "output");
        var checks = new List<string>();
        void Require(bool passed, string description)
        {
            if (!passed) throw new InvalidOperationException("Systems integration: " + description);
            checks.Add(description);
        }
        Button ButtonNamed(string caption) => SystemsDescendants<Button>(_panelContent)
            .Single(button => string.Equals(button.Content as string, caption, StringComparison.Ordinal));
        async Task<ImageJobResult> ClickAndAwaitJobAsync(Button button, ImageAction action)
        {
            var count = _jobs.Count;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var deadline = Stopwatch.StartNew();
            // Merge's async Click handler reloads its ordered list before Enqueue; don't snapshot _jobTasks too soon.
            while (_jobs.Count == count && deadline.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(30);
            Require(_jobs.Count == count + 1, "one actual UI job enqueued for " + action);
            await Task.WhenAll(_jobTasks).WaitAsync(TimeSpan.FromSeconds(30));
            var job = _jobs[^1];
            Require(job.Options.Action == action && job.Finished && job.Result is not null && File.Exists(job.Result.OutputPath),
                "real UI job completed with output for " + action);
            Require(_compact && _panel.Visibility == Visibility.Collapsed, "completed operation stays compact: " + action);
            return job.Result!;
        }
        try
        {
            Directory.CreateDirectory(fixtures);
            Directory.CreateDirectory(output);
            File.Delete(Path.Combine(destination, "passed.json"));
            File.Delete(Path.Combine(destination, "error.txt"));
            Require(IsDiagnosticRun && _args.Contains("--systems-smoke") && _bridge is null,
                "diagnostic skips native hotkeys, tray and real preference writes");
            _outputDirectory = output;
            var first = Path.Combine(fixtures, "первый документ.pdf");
            var second = Path.Combine(fixtures, "второй документ.pdf");
            WriteSystemsPdf(first, new[] { ("A1", 320, 240), ("A2", 440, 330) });
            WriteSystemsPdf(second, new[] { ("B1", 222, 333), ("B2", 500, 500) });
            var firstHash = SystemsDigest(first);
            var secondHash = SystemsDigest(second);
            var documentEngine = new DocumentEngine();
            Require((await documentEngine.InspectPdfAsync(first)).Pages == 2 && (await documentEngine.InspectPdfAsync(second)).Pages == 2,
                "independent Poppler parses both real two-page fixtures");
            Require(await LoadFilesAsync(new[] { first }), "first PDF loads through normal WPF selection");
            Require(_selectionKind == ToolkitKind.Pdf && _radial.ActionCount == 6,
                "PDF selection exposes six context actions");
            var context = _radial.Children.OfType<SectorButton>().Select(button => button.Action).ToArray();
            Require(context.Contains(ImageAction.PdfMerge) && context.Contains(ImageAction.PdfExtractPages) && context.Contains(ImageAction.MoreTools),
                "PDF context exposes merge, selected pages and more tools");
            if (!_compact) SetCompactMode(true);
            DispatchAction(ImageAction.PdfExtractPages);
            var range = SystemsDescendants<TextBox>(_panelContent).Single();
            range.Text = "2,1";
            Require(_compact && _panel.Visibility == Visibility.Visible, "selected-pages UI opens an editable compact card");
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            Capture(Path.Combine(destination, "pdf-pages-card.png"));
            var extracted = await ClickAndAwaitJobAsync(ButtonNamed("Создать PDF"), ImageAction.PdfExtractPages);
            Require((await documentEngine.InspectPdfAsync(extracted.OutputPath)).Pages == 2,
                "selected-pages UI creates an independently readable two-page PDF");
            Require((await ReadSystemsPdfOrderAsync(extracted.OutputPath)).SequenceEqual(new[] { "A2", "A1" }),
                "selected-pages UI preserves requested descending page order and real vector text");
            Require(SystemsDigest(first) == firstHash, "page extraction preserves original PDF bytes");

            Require(await LoadFilesAsync(new[] { first, second }), "two PDF selection loads for merge card");
            DispatchAction(ImageAction.PdfMerge);
            var list = SystemsDescendants<ListBox>(_panelContent).Single();
            Require(list.Items.Cast<string>().SequenceEqual(new[] { first, second }), "merge card begins in explicit selection order");
            list.SelectedIndex = 0;
            ButtonNamed("↓").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(list.Items.Cast<string>().SequenceEqual(new[] { second, first }) && list.SelectedIndex == 1,
                "actual merge down-button moves the selected PDF and updates selection");
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            Capture(Path.Combine(destination, "pdf-merge-card.png"));
            var merged = await ClickAndAwaitJobAsync(ButtonNamed("Объединить"), ImageAction.PdfMerge);
            Require(_jobs[^1].InputPaths.SequenceEqual(new[] { second, first }), "asynchronous merge card enqueues its displayed order");
            Require((await documentEngine.InspectPdfAsync(merged.OutputPath)).Pages == 4,
                "merge UI creates an independently parsed four-page PDF");
            Require((await ReadSystemsPdfOrderAsync(merged.OutputPath)).SequenceEqual(new[] { "B1", "B2", "A1", "A2" }),
                "merge UI output preserves actual text and file order after reordering");
            Require(SystemsDigest(first) == firstHash && SystemsDigest(second) == secondHash, "merge leaves both source PDFs byte-identical");

            DispatchAction(ImageAction.MoreTools);
            Require(_compact && _panel.Visibility == Visibility.Visible
                && SystemsDescendants<Button>(_panelContent).Any(button => (button.Content as string) == "Переименовать файлы…"),
                "more-tools card exposes rename without leaving compact mode");
            ClosePanel();

            var renameFirst = Path.Combine(fixtures, "имя alpha.txt");
            var renameSecond = Path.Combine(fixtures, "имя beta.txt");
            File.WriteAllText(renameFirst, "rename first fixture", new UTF8Encoding(false));
            File.WriteAllText(renameSecond, "rename second fixture", new UTF8Encoding(false));
            var renameFirstHash = SystemsDigest(renameFirst);
            var renameSecondHash = SystemsDigest(renameSecond);
            Require(await LoadFilesAsync(new[] { renameFirst, renameSecond }), "actual text fixtures load for batch rename");
            ShowRename();
            var prefix = SystemsDescendants<TextBox>(_panelContent).Single(box => (box.Tag as string) == "rename-prefix");
            prefix.Text = "new_";
            var apply = ButtonNamed("Переименовать");
            Require(_compact && _panel.Visibility == Visibility.Visible && !apply.IsEnabled && _preparedRenamePlan is null,
                "rename compact card requires preview after pattern edit");
            ButtonNamed("Предпросмотр").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var previewDeadline = Stopwatch.StartNew();
            while (_preparedRenamePlan is null && previewDeadline.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(25);
            Require(_preparedRenamePlan is { HasChanges: true } && _preparedRenamePlan.Items.Count == 2 && apply.IsEnabled,
                "rename preview prepares a real two-file plan and enables confirmation");
            await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ContextIdle);
            var previewTable=SystemsDescendants<ListBox>(_panelContent).Single(box=>(box.Tag as string)=="rename-preview-table");
            var confirmationPoint=apply.TranslatePoint(new Point(0,apply.ActualHeight),this);
            Require(previewTable.Items.Count==2&&confirmationPoint.Y<=ActualHeight-12&&confirmationPoint.Y>0,
                "preview scrolls its real name list and confirmation button into the compact viewport");
            var renamedFirst = Path.Combine(fixtures, "new_" + Path.GetFileName(renameFirst));
            var renamedSecond = Path.Combine(fixtures, "new_" + Path.GetFileName(renameSecond));
            Require(_preparedRenamePlan!.Items.Select(item => item.TargetPath).SequenceEqual(new[] { renamedFirst, renamedSecond })
                && File.Exists(renameFirst) && File.Exists(renameSecond) && !File.Exists(renamedFirst) && !File.Exists(renamedSecond),
                "rename preview shows requested names while leaving sources untouched");
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            Capture(Path.Combine(destination, "rename-preview-card.png"));
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await _renameWork.WaitAsync(TimeSpan.FromSeconds(15));
            Require(_lastRenameReceipt is { IsUndone: false } && _lastRenameReceipt.Items.Count == 2,
                "rename confirmation produces a real guarded undo receipt");
            Require(!File.Exists(renameFirst) && !File.Exists(renameSecond) && File.Exists(renamedFirst) && File.Exists(renamedSecond),
                "rename UI actually moves both files to the previewed names");
            Require(SystemsDigest(renamedFirst) == renameFirstHash && SystemsDigest(renamedSecond) == renameSecondHash,
                "rename preserves exact source bytes and extensions");
            Require(_files.SequenceEqual(new[] { renamedFirst, renamedSecond }) && _compact && _panel.Visibility == Visibility.Visible,
                "rename UI reloads new paths and displays compact result card");
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            Capture(Path.Combine(destination, "rename-result-card.png"));
            ButtonNamed("Отменить переименование").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await _renameWork.WaitAsync(TimeSpan.FromSeconds(15));
            Require(_lastRenameReceipt!.IsUndone && File.Exists(renameFirst) && File.Exists(renameSecond)
                && !File.Exists(renamedFirst) && !File.Exists(renamedSecond),
                "actual result-card undo restores original names and consumes receipt");
            Require(_files.SequenceEqual(new[] { renameFirst, renameSecond })
                && SystemsDigest(renameFirst) == renameFirstHash && SystemsDigest(renameSecond) == renameSecondHash,
                "rename undo reloads original selection with byte-identical contents");
            Require(SystemsDigest(first) == firstHash && SystemsDigest(second) == secondHash,
                "unrelated source PDFs remain unchanged after rename and undo");

            var gesture = HotkeyGesture.Parse("Ctrl+Shift+Q");
            var temporaryStore = new SettingsStore(Path.Combine(destination, "isolated-settings.json"));
            Require(temporaryStore.Save(new OrbitSettings { Shortcut = gesture.ToString(), CompactSize = 520, OutputDirectory = output }) is null,
                "isolated settings store saves custom shortcut without touching user preferences");
            var settings = temporaryStore.Load();
            Require(settings.Shortcut == "Ctrl+Shift+Q" && HotkeyGesture.TryParse(settings.Shortcut, out var reloaded) && reloaded == gesture,
                "settings shortcut round-trip matches runtime gesture parser");
            Require(settings.CompactSize == 520 && settings.OutputDirectory == output, "other isolated settings survive shortcut round-trip");
            Require(!HotkeyGesture.TryParse("Ctrl+F12", out _, out var reservedError) && reservedError is not null,
                "reserved shortcut is rejected without native registration");
            var processPath = Environment.ProcessPath!;
            var startup = StartupIntegration.BuildCommand(processPath);
            Require(startup == "\"" + Path.GetFullPath(processPath) + "\" --background"
                && StartupIntegration.MatchesCommand(startup, processPath),
                "startup command is safely quoted and matches background-only application flag");
            Require(!StartupIntegration.MatchesCommand(startup + " --unexpected", processPath),
                "startup matcher rejects additional unrequested arguments without registry access");
            File.WriteAllText(Path.Combine(destination, "passed.json"), JsonSerializer.Serialize(new
            {
                Passed = checks.Count,
                Checks = checks,
                RegistryWrites = false,
                NativeHotkeyRegistration = false,
                SyntheticInput = false,
                BackgroundLaunchTested = false
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception)
        {
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, "error.txt"), exception.ToString());
        }
        finally { Quit(); }
    }

    private static IEnumerable<T> SystemsDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var descendant in SystemsDescendants<T>(child)) yield return descendant;
        }
    }
    private static string SystemsDigest(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static async Task<IReadOnlyList<string>> ReadSystemsPdfOrderAsync(string path)
    {
        var start = new ProcessStartInfo(new PdfAssemblyEngine().ExecutablePath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "--json", "--json-key=pages", "--json-key=qpdf", "--json-stream-data=inline", path }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not inspect systems PDF output.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            if (process.ExitCode != 0) throw new InvalidOperationException("qpdf systems inspection failed: " + await error);
            using var json = JsonDocument.Parse(await output);
            var objects = json.RootElement.GetProperty("qpdf")[1];
            var texts = new List<string>();
            foreach (var page in json.RootElement.GetProperty("pages").EnumerateArray())
            {
                if (page.GetProperty("images").GetArrayLength() != 0) throw new InvalidOperationException("Systems PDF was unexpectedly rasterized.");
                var content = string.Concat(page.GetProperty("contents").EnumerateArray().Select(reference =>
                    Encoding.ASCII.GetString(Convert.FromBase64String(objects.GetProperty("obj:" + reference.GetString()).GetProperty("stream").GetProperty("data").GetString()!))));
                var from = content.IndexOf('(');
                var to = content.IndexOf(')', from + 1);
                if (from < 0 || to < 0) throw new InvalidOperationException("Original PDF text is missing.");
                texts.Add(content[(from + 1)..to]);
            }
            return texts;
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
    }

    private static void WriteSystemsPdf(string path, IReadOnlyList<(string Text, int Width, int Height)> pages)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count " + pages.Count + " /Kids [" + string.Join(" ", Enumerable.Range(0, pages.Count).Select(index => (3 + index * 2) + " 0 R")) + "] >>"
        };
        var fontId = 3 + pages.Count * 2;
        for (var index = 0; index < pages.Count; index++)
        {
            var page = pages[index];
            objects.Add(FormattableString.Invariant($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {page.Width} {page.Height}] /Resources << /Font << /F1 {fontId} 0 R >> >> /Contents {4 + index * 2} 0 R >>"));
            var content = $"BT /F1 22 Tf 24 100 Td ({page.Text}) Tj ET\n";
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream");
        }
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        using var output = File.Create(path);
        void Write(string text) { var bytes = Encoding.ASCII.GetBytes(text); output.Write(bytes); }
        Write("%PDF-1.4\n");
        var offsets = new List<long>();
        for (var index = 0; index < objects.Count; index++) { offsets.Add(output.Position); Write($"{index + 1} 0 obj\n{objects[index]}\nendobj\n"); }
        var xref = output.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) Write(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }
}
