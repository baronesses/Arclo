using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Orbit.App.History;
using Orbit.App.Presets;
using Orbit.Core;
using Orbit.Core.Presets;

namespace Orbit.App;

public sealed partial class MainWindow
{
    /// <summary>Explicit artifact-only WPF diagnostic; shell opening is intercepted and no work is restored automatically.</summary>
    private async Task RunProductivitySmokeAsync()
    {
        var destination = Path.Combine(AppContext.BaseDirectory, "productivity-smoke");
        var run = Path.Combine(destination, "run-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        var fixtures = Path.Combine(run, "fixtures");
        var firstOutput = Path.Combine(run, "output one");
        var currentOutput = Path.Combine(run, "output two");
        var checks = new List<string>();
        var opened = new List<(string Path, bool Reveal)>();
        var historyLoadBarriers = new List<TaskCompletionSource>();
        bool gateHeld = false;
        void Require(bool passed, string description)
        {
            if (!passed) throw new InvalidOperationException("Productivity integration: " + description);
            checks.Add(description);
        }
        Button Named(string caption) => SystemsDescendants<Button>(_panelContent)
            .Single(button => string.Equals(button.Content as string, caption, StringComparison.Ordinal));
        async Task WaitUntilAsync(Func<bool> condition, string description)
        {
            var elapsed = Stopwatch.StartNew();
            while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
            Require(condition(), description);
        }
        async Task<JobEntry> ClickJobAsync(Button button, int quality)
        {
            var before = _jobs.Count;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => _jobs.Count == before + 1, "actual UI click queues exactly one processing job");
            await Task.WhenAll(_jobTasks).WaitAsync(TimeSpan.FromSeconds(45));
            var job = _jobs[^1];
            Require(job.Finished && job.HistoryStatus == HistoryStatus.Succeeded && job.Result is not null
                && File.Exists(job.Result.OutputPath), "UI processing produces a successful real result");
            Require(job.Options.Action == ImageAction.WebP && job.Options.Quality == quality,
                "queued job carries the selected WebP quality " + quality);
            return job;
        }
        async Task ShotAsync(string name)
        {
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            Capture(Path.Combine(destination, name));
        }
        async Task CheckPresetNamesAsync(ComboBox picker, bool checkPopup)
        {
            picker.ApplyTemplate();
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            var selected = (ProcessingPreset)picker.SelectedItem;
            var visible = ProductivityVisualDescendants<TextBlock>(picker).Where(text => text.IsVisible).Select(text => text.Text).ToArray();
            Require(visible.Contains(selected.Name) && !visible.Any(text => text.Contains("ProcessingPreset", StringComparison.Ordinal)),
                "selected preset renders its friendly name rather than record implementation details");
            if (!checkPopup) return;
            try
            {
                picker.IsDropDownOpen = true;
                await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Loaded);
                var popup = picker.Template.FindName("PART_Popup", picker) as Popup;
                var names = picker.Items.Cast<ProcessingPreset>().Select(preset => preset.Name).ToHashSet(StringComparer.Ordinal);
                var rows = popup?.Child is null ? Array.Empty<string>() : ProductivityVisualDescendants<TextBlock>(popup.Child)
                    .Where(text => text.IsVisible && !string.IsNullOrWhiteSpace(text.Text)).Select(text => text.Text).ToArray();
                Require(popup?.IsOpen == true && rows.Contains(selected.Name) && rows.All(names.Contains),
                    "opened preset popup rows display friendly names through the real glass item templates");
                if (popup?.Child is FrameworkElement popupSurface)
                {
                    var rendered = new RenderTargetBitmap((int)Math.Ceiling(popupSurface.ActualWidth),
                        (int)Math.Ceiling(popupSurface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    rendered.Render(popupSurface);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(rendered));
                    using var file = File.Create(Path.Combine(destination, "02-presets-dropdown.png"));
                    encoder.Save(file);
                }
            }
            finally { picker.IsDropDownOpen = false; }
        }
        try
        {
            Directory.CreateDirectory(fixtures);
            Directory.CreateDirectory(firstOutput);
            Directory.CreateDirectory(currentOutput);
            File.Delete(Path.Combine(destination, "passed.json"));
            File.Delete(Path.Combine(destination, "error.txt"));
            Require(IsDiagnosticRun && _args.Contains("--productivity-smoke") && _bridge is null,
                "diagnostic skips user preferences, tray and real global hotkeys");
            Require(_jobs.Count == 0 && _jobTasks.Count == 0, "normal diagnostic startup does not load profile jobs");

            var source = Path.Combine(fixtures, "первое изображение.png");
            var currentSource = Path.Combine(fixtures, "текущий выбор.png");
            WriteProductivityImage(source, 128, 80, 50);
            WriteProductivityImage(currentSource, 96, 64, 170);
            var sourceHash = ProductivityDigest(source);
            var currentHash = ProductivityDigest(currentSource);
            var historyPath = Path.Combine(run, "history.json");
            var presetsPath = Path.Combine(run, "presets.json");
            _historyStore = new HistoryStore(historyPath);
            _presetStore = new PresetStore(presetsPath);
            _allowDiagnosticProductivityIo = true;
            _resultOpenOverride = (path, reveal) => { opened.Add((path, reveal)); return Task.CompletedTask; };
            Require(Path.GetFullPath(_historyStore.FilePath).StartsWith(Path.GetFullPath(run) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && Path.GetFullPath(_presetStore.FilePath).StartsWith(Path.GetFullPath(run) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "both explicitly injected stores remain inside the diagnostic artifact directory");

            var interruptedId = Guid.NewGuid();
            Require(await _historyStore.SaveAsync(new[] { new HistoryEntry
            {
                Id = interruptedId, CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-2), SourcePaths = new[] { source },
                Options = new ImageJobOptions { Action = ImageAction.WebP, Quality = 73, OutputDirectory = firstOutput },
                Status = HistoryStatus.Queued
            } }) is null, "queued restart fixture saves to the isolated history store");
            await LoadProductivityAsync();
            var interrupted = _jobs.Single(job => job.Id == interruptedId);
            Require(interrupted.Finished && interrupted.HistoryStatus == HistoryStatus.Interrupted && interrupted.Result is null,
                "persisted queued entry restores as a terminal interrupted record");
            Require(_jobTasks.Count == 0 && !Directory.EnumerateFileSystemEntries(firstOutput).Any(),
                "history restoration never automatically executes queued work");

            _outputDirectory = firstOutput;
            Require(await LoadFilesAsync(new[] { source }), "first image loads through normal WPF selection");
            if (!_compact) SetCompactMode(true);
            ShowAction(ImageAction.WebP);
            SystemsDescendants<Slider>(_panelContent).Single().Value = 73;
            var first = await ClickJobAsync(Named("Создать файл"), 73);
            var firstResult = first.Result!;
            var actual = await _engine.InspectAsync(firstResult.OutputPath);
            Require(actual.Kind == ToolkitKind.Image && actual.Format.Equals("WEBP", StringComparison.OrdinalIgnoreCase)
                && actual.Width == 128 && actual.Height == 80, "independent backend parses the actual WebP pixels and format");
            var firstResultHash = ProductivityDigest(firstResult.OutputPath);
            await FlushHistoryAsync();
            var saved = await new HistoryStore(historyPath).LoadAsync();
            var durable = saved.Entries.Single(entry => entry.Id == first.Id);
            Require(saved.Error is null && durable.Status == HistoryStatus.Succeeded && durable.Result == firstResult
                && durable.SourcePaths.SequenceEqual(new[] { source }) && durable.Options.Quality == 73,
                "history round trip preserves exact result, source selection, options and terminal status");

            var tasksBeforeRestore = _jobTasks.Count;
            _jobs.Clear();
            await LoadProductivityAsync();
            first = _jobs.Single(job => job.Id == durable.Id);
            Require(first.Finished && first.HistoryStatus == HistoryStatus.Succeeded && first.Result == firstResult
                && _jobTasks.Count == tasksBeforeRestore, "UI reload restores result cards without creating new tasks");
            ShowRecentResults();
            await ShotAsync("01-recent-results.png");
            Named("Открыть").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => opened.Count == 1, "recent result Open reaches the intercepted validated shell route");
            Named("В папке").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => opened.Count == 2, "recent result Reveal reaches the intercepted validated shell route");
            Require(opened[0] == (firstResult.OutputPath, false) && opened[1] == (firstResult.OutputPath, true),
                "recent result buttons pass the exact persisted path and requested opening mode");

            ShowSavePreset(first);
            const string customName = "Мой WebP 73";
            SystemsDescendants<TextBox>(_panelContent).Single().Text = customName;
            Named("Сохранить пресет").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => _presetStore.Load().Presets.Any(preset => preset.Name == customName),
                "Save preset UI persists a named custom recipe");
            var custom = new PresetStore(presetsPath).Load().Presets.Single(preset => preset.Name == customName);
            Require(!custom.IsBuiltIn && custom.Options.Action == ImageAction.WebP && custom.Options.Quality == 73
                && custom.Options.OutputDirectory is null, "saved recipe retains processing options and omits the old output directory");
            _presetStore = new PresetStore(presetsPath);
            await LoadProductivityAsync();
            Require(await LoadFilesAsync(new[] { currentSource }), "a different current image is selected before applying saved recipes");
            _outputDirectory = currentOutput;

            ShowPresets();
            var chooser = SystemsDescendants<ComboBox>(_panelContent).Single();
            var allPresets = chooser.Items.Cast<ProcessingPreset>().ToArray();
            Require(allPresets.Count(preset => preset.IsBuiltIn) >= 8 && allPresets.Any(preset => preset.Id == custom.Id),
                "preset card offers built-in recipes alongside the reloaded custom recipe");
            chooser.SelectedItem = allPresets.Single(preset => preset.Id == "builtin-audio-mp3-192");
            Require(!Named("Применить пресет").IsEnabled, "audio recipe is disabled for the current image selection");
            chooser.SelectedItem = allPresets.Single(preset => preset.Id == "builtin-webp-82");
            Require(Named("Применить пресет").IsEnabled, "compatible built-in WebP recipe becomes available");
            await CheckPresetNamesAsync(chooser, checkPopup: true);
            await ShotAsync("02-presets.png");
            var builtInJob = await ClickJobAsync(Named("Применить пресет"), 82);
            Require(builtInJob.InputPaths.SequenceEqual(new[] { currentSource }) && builtInJob.Options.OutputDirectory == currentOutput,
                "built-in preset applies to the current selection and current output directory");
            ShowPresets();
            chooser = SystemsDescendants<ComboBox>(_panelContent).Single();
            chooser.SelectedItem = chooser.Items.Cast<ProcessingPreset>().Single(preset => preset.Id == custom.Id);
            await CheckPresetNamesAsync(chooser, checkPopup: false);
            var customJob = await ClickJobAsync(Named("Применить пресет"), 73);
            Require(customJob.InputPaths.SequenceEqual(new[] { currentSource }) && customJob.Options.OutputDirectory == currentOutput
                && Path.GetDirectoryName(customJob.Result!.OutputPath) == currentOutput,
                "reloaded custom recipe uses the current selection and output folder without captured source paths");

            var missingPath = customJob.Result!.OutputPath;
            File.Delete(missingPath);
            int openedBeforeMissing = opened.Count;
            _toast.Visibility = Visibility.Collapsed;
            _toastText.Text = "";
            await OpenKnownResultAsync(missingPath, false);
            Require(opened.Count == openedBeforeMissing && _toast.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(_toastText.Text),
                "deleted result gives visible feedback and never reaches the shell opener");

            await _queueGate.WaitAsync();
            gateHeld = true;
            Enqueue(currentSource, new ImageJobOptions { Action = ImageAction.WebP, Quality = 61, OutputDirectory = currentOutput });
            var pending = _jobs[^1];
            Require(!pending.Finished && pending.HistoryStatus == HistoryStatus.Queued, "real queued job waits behind the held processing gate");
            await ClearCompletedHistoryAsync();
            await FlushHistoryAsync();
            Require(_jobs.Count == 1 && ReferenceEquals(_jobs[0], pending) && !pending.Cancellation.IsCancellationRequested,
                "clear completed history preserves the queued job and its uncancelled task");
            var afterClear = await new HistoryStore(historyPath).LoadAsync();
            Require(afterClear.Error is null && afterClear.Entries.Count == 1 && afterClear.Entries[0].Id == pending.Id,
                "ordered clear snapshot cannot resurrect older terminal records");
            ShowJobs();
            await ShotAsync("03-queued-after-clear.png");
            _queueGate.Release();
            gateHeld = false;
            await Task.WhenAll(_jobTasks).WaitAsync(TimeSpan.FromSeconds(45));
            await FlushHistoryAsync();
            var completed = await new HistoryStore(historyPath).LoadAsync();
            Require(pending.Finished && pending.Result is not null && completed.Entries.Count == 1
                && completed.Entries[0].Id == pending.Id && completed.Entries[0].Status == HistoryStatus.Succeeded,
                "job retained during clear completes normally and persists its own new result");
            await ClearCompletedHistoryAsync();
            await FlushHistoryAsync();
            Require(_jobs.Count == 0 && (await new HistoryStore(historyPath).LoadAsync()).Entries.Count == 0,
                "final clear is durable and leaves no terminal rows to reappear on reload");
            Require(File.Exists(firstResult.OutputPath) && ProductivityDigest(firstResult.OutputPath) == firstResultHash
                && File.Exists(builtInJob.Result!.OutputPath) && File.Exists(pending.Result!.OutputPath),
                "clearing history preserves successful output files and their exact bytes");

            var validRacePath = Path.Combine(run, "startup-valid-history.json");
            var oldRaceId = Guid.NewGuid();
            var validRaceStore = new HistoryStore(validRacePath);
            Require(await validRaceStore.SaveAsync(new[] { new HistoryEntry
            {
                Id = oldRaceId, CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-5), SourcePaths = new[] { source },
                Options = new ImageJobOptions { Action = ImageAction.WebP, Quality = 77, OutputDirectory = firstOutput },
                Status = HistoryStatus.Queued
            } }) is null, "controlled startup race has an existing valid queued history record");
            var beforeValidLoad = ProductivityDigest(validRacePath);
            _historyStore = validRaceStore;
            var validLoadBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            historyLoadBarriers.Add(validLoadBarrier);
            _historyLoadBarrier = () => validLoadBarrier.Task;
            var validLoading = LoadProductivityAsync();
            Require(_historyLoading && !validLoading.IsCompleted, "startup history loading pauses deterministically before store I/O");
            Enqueue(currentSource, new ImageJobOptions { Action = ImageAction.WebP, Quality = 67, OutputDirectory = currentOutput });
            var duringValidLoad = _jobs[^1];
            await Task.WhenAll(_jobTasks).WaitAsync(TimeSpan.FromSeconds(45));
            Require(duringValidLoad.Finished && duringValidLoad.Result is not null && File.Exists(duringValidLoad.Result.OutputPath),
                "real processing can finish while startup history is still loading");
            Require(ProductivityDigest(validRacePath) == beforeValidLoad,
                "completed processing cannot overwrite existing history before startup merge");
            var validFlush = FlushHistoryAsync();
            Require(!validFlush.IsCompleted, "flush waits for the pending startup history load");
            validLoadBarrier.TrySetResult();
            await Task.WhenAll(validLoading, validFlush).WaitAsync(TimeSpan.FromSeconds(10));
            _historyLoadBarrier = null;
            var mergedRace = await new HistoryStore(validRacePath).LoadAsync();
            Require(mergedRace.Error is null && mergedRace.Entries.Count == 2
                && mergedRace.Entries.Single(entry => entry.Id == oldRaceId).Status == HistoryStatus.Interrupted
                && mergedRace.Entries.Single(entry => entry.Id == duringValidLoad.Id).Status == HistoryStatus.Succeeded,
                "startup merge durably preserves both the old interrupted record and the newly completed result");
            ShowJobs();
            await ShotAsync("04-startup-merge.png");

            await FlushHistoryAsync();
            _jobs.Clear();
            var malformedRacePath = Path.Combine(run, "startup-malformed-history.json");
            await File.WriteAllTextAsync(malformedRacePath, "{ deliberately malformed history");
            var malformedHash = ProductivityDigest(malformedRacePath);
            _historyStore = new HistoryStore(malformedRacePath);
            var malformedLoadBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            historyLoadBarriers.Add(malformedLoadBarrier);
            _historyLoadBarrier = () => malformedLoadBarrier.Task;
            var malformedLoading = LoadProductivityAsync();
            Require(_historyLoading && !malformedLoading.IsCompleted, "malformed startup fixture pauses before its error can be known");
            Enqueue(currentSource, new ImageJobOptions { Action = ImageAction.WebP, Quality = 68, OutputDirectory = currentOutput });
            var duringMalformedLoad = _jobs[^1];
            await Task.WhenAll(_jobTasks).WaitAsync(TimeSpan.FromSeconds(45));
            Require(duringMalformedLoad.Finished && duringMalformedLoad.Result is not null
                && File.Exists(duringMalformedLoad.Result.OutputPath) && ProductivityDigest(malformedRacePath) == malformedHash,
                "processing during an unresolved malformed load preserves the original malformed bytes");
            var malformedFlush = FlushHistoryAsync();
            Require(!malformedFlush.IsCompleted, "flush also waits when startup history will be malformed");
            malformedLoadBarrier.TrySetResult();
            await Task.WhenAll(malformedLoading, malformedFlush).WaitAsync(TimeSpan.FromSeconds(10));
            _historyLoadBarrier = null;
            Require(_historyBlocked && ProductivityDigest(malformedRacePath) == malformedHash,
                "malformed history remains blocked and byte-identical after merge and flush");
            ShowJobs();
            await ShotAsync("05-startup-malformed.png");
            await ClearCompletedHistoryAsync();
            await FlushHistoryAsync();
            var recoveredRace = await new HistoryStore(malformedRacePath).LoadAsync();
            Require(!_historyBlocked && recoveredRace.Error is null && recoveredRace.Entries.Count == 0,
                "explicit clear recovers malformed history without touching completed output files");
            Enqueue(currentSource, new ImageJobOptions { Action = ImageAction.WebP, Quality = 69, OutputDirectory = currentOutput });
            var afterRecovery = _jobs[^1];
            await Task.WhenAll(_jobTasks).WaitAsync(TimeSpan.FromSeconds(45));
            await FlushHistoryAsync();
            var recoveredCompletion = await new HistoryStore(malformedRacePath).LoadAsync();
            Require(afterRecovery.Finished && afterRecovery.Result is not null && recoveredCompletion.Error is null
                && recoveredCompletion.Entries.Count == 1 && recoveredCompletion.Entries[0].Id == afterRecovery.Id
                && recoveredCompletion.Entries[0].Status == HistoryStatus.Succeeded,
                "a real job can persist normally again after explicit malformed-history recovery");
            Require(File.Exists(duringValidLoad.Result!.OutputPath) && File.Exists(duringMalformedLoad.Result!.OutputPath),
                "startup-race merge and recovery retain all real processing results");

            await ClearCompletedHistoryAsync();
            await FlushHistoryAsync();
            ShowSettings();
            var historyToggle = SystemsDescendants<CheckBox>(_panelContent)
                .Single(box => box.Content as string == "Сохранять историю между запусками");
            Require(_keepHistory && historyToggle.IsChecked == true, "actual settings checkbox reflects enabled history persistence");
            historyToggle.IsChecked = false;
            historyToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await FlushHistoryAsync();
            var disabledHistory = await new HistoryStore(malformedRacePath).LoadAsync();
            Require(!_keepHistory && disabledHistory.Error is null && disabledHistory.Entries.Count == 0,
                "disabling the real history checkbox clears durable records");
            Enqueue(currentSource, new ImageJobOptions { Action = ImageAction.WebP, Quality = 72, OutputDirectory = currentOutput });
            var whileHistoryDisabled = _jobs[^1];
            await Task.WhenAll(_jobTasks).WaitAsync(TimeSpan.FromSeconds(45));
            await FlushHistoryAsync();
            var stillDisabledHistory = await new HistoryStore(malformedRacePath).LoadAsync();
            Require(whileHistoryDisabled.Finished && whileHistoryDisabled.Result is not null
                && File.Exists(whileHistoryDisabled.Result.OutputPath) && _jobs.Contains(whileHistoryDisabled),
                "processing continues and its completed job stays visible while history persistence is disabled");
            Require(stillDisabledHistory.Error is null && stillDisabledHistory.Entries.Count == 0,
                "completed processing and flush leave the history store empty while disabled");
            ShowJobs();
            await ShotAsync("06-history-disabled-job.png");
            ShowSettings();
            historyToggle = SystemsDescendants<CheckBox>(_panelContent)
                .Single(box => box.Content as string == "Сохранять историю между запусками");
            Require(historyToggle.IsChecked == false, "settings reopened after processing still shows history persistence disabled");
            historyToggle.IsChecked = true;
            historyToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await FlushHistoryAsync();
            var enabledHistory = await new HistoryStore(malformedRacePath).LoadAsync();
            Require(_keepHistory && enabledHistory.Error is null && enabledHistory.Entries.Count == 1
                && enabledHistory.Entries[0].Id == whileHistoryDisabled.Id && enabledHistory.Entries[0].Status == HistoryStatus.Succeeded,
                "reenabling the real checkbox persists the current completed job without rerunning it");
            Require(File.Exists(whileHistoryDisabled.Result!.OutputPath) && File.Exists(afterRecovery.Result!.OutputPath),
                "history checkbox changes preserve files created both before and while persistence was disabled");
            Require(ProductivityDigest(source) == sourceHash && ProductivityDigest(currentSource) == currentHash,
                "all original image files remain SHA256-identical through presets, history and clear");
            Require(!Directory.EnumerateFiles(run, ".orbit-*.tmp", SearchOption.AllDirectories).Any(),
                "history and preset persistence leaves no partial temporary files");
            var report = JsonSerializer.Serialize(new { Passed = checks.Count, Checks = checks, Artifacts = run,
                HistoryPath = historyPath, PresetsPath = presetsPath, NativeShellLaunches = 0 }, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(destination, "passed.json"), report);
            await File.WriteAllTextAsync(Path.Combine(run, "passed.json"), report);
        }
        catch (Exception exception)
        {
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Combine(destination, "error.txt"), exception.ToString());
        }
        finally
        {
            foreach (var barrier in historyLoadBarriers) barrier.TrySetResult();
            _historyLoadBarrier = null;
            if (gateHeld) _queueGate.Release();
            Quit();
        }
    }

    private static string ProductivityDigest(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static IEnumerable<T> ProductivityVisualDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in ProductivityVisualDescendants<T>(child)) yield return descendant;
        }
    }
    private static void WriteProductivityImage(string path, int width, int height, byte shade)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = (y * width + x) * 4;
            pixels[offset] = (byte)(shade + x % 70);
            pixels[offset + 1] = (byte)(30 + y % 100);
            pixels[offset + 2] = (byte)(210 - x % 80);
            pixels[offset + 3] = 255;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }
}
