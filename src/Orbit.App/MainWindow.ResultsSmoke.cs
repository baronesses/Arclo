using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Orbit.App.Controls;
using Orbit.App.History;
using Orbit.App.Platform;
using Orbit.App.Presets;
using Orbit.Core;
using FormsDataObject = System.Windows.Forms.DataObject;
using FormsTextDataFormat = System.Windows.Forms.TextDataFormat;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private async Task RunResultsSmokeAsync()
    {
        var destination = Path.Combine(AppContext.BaseDirectory, "results-smoke");
        var run = Path.Combine(destination, "run-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        var fixtures = Path.Combine(run, "fixtures");
        var output = Path.Combine(run, "output");
        var checks = new List<string>();
        bool gateHeld = false;
        CancellationTokenSource? observerCancellation = null;
        Task? observer = null;
        void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Results integration: " + message);
            checks.Add(message);
        }
        Button Named(string name) => SystemsDescendants<Button>(_panelContent).Single(button => button.Content as string == name);
        async Task UntilAsync(Func<bool> condition, string description)
        {
            var watch = Stopwatch.StartNew();
            while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
            Require(condition(), description);
        }
        async Task ResultTasksAsync() => await Task.WhenAll(_resultToolTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(30));
        async Task ShotAsync(string name)
        {
            _toast.Visibility = Visibility.Collapsed;
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            Capture(Path.Combine(destination, name));
        }
        void Filter(JobListFilter filter, string search = "")
        {
            _jobFilterPicker!.SelectedIndex = (int)filter;
            _jobSearchBox!.Text = search;
        }
        try
        {
            Directory.CreateDirectory(fixtures);
            Directory.CreateDirectory(output);
            File.Delete(Path.Combine(destination, "passed.json"));
            File.Delete(Path.Combine(destination, "error.txt"));
            Require(IsDiagnosticRun && _args.Contains("--results-smoke") && _bridge is null && _jobs.Count == 0,
                "isolated results diagnostic loads no user jobs, tray or real hotkeys");
            var source = Path.Combine(fixtures, "оригинал прозрачный.png");
            WriteResultsImage(source);
            var originalHash = ResultsDigest(source);
            var historyPath = Path.Combine(run, "history.json");
            _historyStore = new HistoryStore(historyPath);
            _presetStore = new PresetStore(Path.Combine(run, "presets.json"));
            _allowDiagnosticProductivityIo = true;
            _outputDirectory = output;
            _resultOpenOverride = (_, _) => Task.CompletedTask;
            var interruptedId = Guid.NewGuid();
            Require(await _historyStore.SaveAsync(new[] { new HistoryEntry
            {
                Id = interruptedId, CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-2), Status = HistoryStatus.Queued,
                SourcePaths = new[] { source }, Options = new ImageJobOptions { Action = ImageAction.WebP, OutputDirectory = output }
            } }) is null, "restart fixture is stored only inside the diagnostic run directory");
            await LoadProductivityAsync();
            var interrupted = _jobs.Single();
            Require(interrupted.Id == interruptedId && interrupted.Finished && interrupted.HistoryStatus == HistoryStatus.Interrupted
                && _jobTasks.Count == 0, "loaded queued history is interrupted without automatic execution");
            Require(await LoadFilesAsync(new[] { source }), "real alpha PNG loads through ordinary selection");
            if (!_compact) SetCompactMode(true);
            ShowAction(ImageAction.WebP);
            SystemsDescendants<Slider>(_panelContent).Single().Value = 57;
            int before = _jobs.Count;
            Named("Создать файл").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(_jobs.Count == before + 1, "real WebP parameter card queues one job");
            var first = _jobs[^1];
            await Task.WhenAll(_jobTasks).WaitAsync(TimeSpan.FromSeconds(45));
            Require(first.Finished && first.HistoryStatus == HistoryStatus.Succeeded && first.Result is not null
                && File.Exists(first.Result.OutputPath), "WPF processing creates a real successful WebP result");
            var firstResult = first.Result!;
            var resultHash = ResultsDigest(firstResult.OutputPath);
            var inspected = await _engine.InspectAsync(firstResult.OutputPath);
            Require(inspected.Format.Equals("WEBP", StringComparison.OrdinalIgnoreCase) && inspected.Width == 280 && inspected.Height == 180,
                "independent inspection confirms actual result format and dimensions");

            var sink = new ResultsClipboardCapture(Dispatcher);
            _resultClipboard = null;
            await CopyKnownResultAsync(firstResult.OutputPath, false);
            Require(_resultClipboard is null && sink.Payloads.Count == 0 && _toastText.Text.Contains("диагностике", StringComparison.OrdinalIgnoreCase),
                "diagnostic UI refuses copy without creating a default system clipboard helper");
            _resultClipboard = new ResultClipboard(Dispatcher);
            await CopyKnownResultAsync(firstResult.OutputPath, false);
            Require(sink.Payloads.Count == 0 && _resultClipboard.UsesSystemClipboard,
                "diagnostic UI also rejects an explicitly supplied default native clipboard instance");
            _resultClipboard = new ResultClipboard(Dispatcher, sink);
            ShowJobs();
            Named("Копировать").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await ResultTasksAsync();
            Require(sink.Payloads.Count == 1 && sink.Payloads[^1].GetFileDropList().Cast<string>().SequenceEqual(new[] { firstResult.OutputPath }),
                "real task-card Copy button publishes the exact result as a Unicode file-drop payload");
            var effect = sink.Payloads[^1].GetData(ResultClipboard.PreferredDropEffectFormat, false) as MemoryStream;
            Require(effect is not null && effect.ToArray().SequenceEqual(new byte[] { 1, 0, 0, 0 }) && sink.Persistent && sink.Sta,
                "task-card file copy requests persistent COPY on the WPF STA dispatcher");
            Named("Путь").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await ResultTasksAsync();
            Require(sink.Payloads.Count == 2 && sink.Payloads[^1].GetText(FormsTextDataFormat.UnicodeText) == firstResult.OutputPath
                && sink.Payloads[^1].GetFileDropList().Count == 0, "real task-card Path button copies only the exact Unicode path text");
            await ShotAsync("01-result-tools.png");

            Named("Сравнить").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await ResultTasksAsync();
            var comparison = _comparisonView!;
            Require(comparison is not null && comparison.BeforeSource is { PixelWidth: > 0, PixelHeight: > 0 }
                && comparison.AfterSource is { PixelWidth: > 0, PixelHeight: > 0 } && comparison.BeforeSource.IsFrozen && comparison.AfterSource.IsFrozen,
                "Compare button loads two real decoded frozen PNG previews into ImageCompareView");
            Require(comparison!.BeforeSource!.PixelWidth == 280 && comparison.AfterSource!.PixelWidth == 280
                && comparison.CanWipe && comparison.Mode == ComparisonMode.SideBySide, "equal-aspect real image previews support both comparison modes");
            await ShotAsync("02-compare-side-by-side.png");
            var wipe = SystemsDescendants<ToggleButton>(comparison).Single(button => button.Content as string == "Шторка");
            wipe.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var split = SystemsDescendants<Slider>(comparison).Single();
            split.Value = .27;
            Require(comparison.Mode == ComparisonMode.Wipe && split.IsEnabled && Math.Abs(comparison.SplitPosition - .27) < .00001,
                "real Wipe toggle and bound slider change the comparison split");
            await ShotAsync("03-compare-wipe.png");
            var closingComparison = ShowComparisonAsync(first);
            int closingVersion = _comparisonVersion;
            ClosePanel();
            await closingComparison.WaitAsync(TimeSpan.FromSeconds(30));
            Require(_comparisonVersion > closingVersion && _comparisonView is null && _panel.Visibility == Visibility.Collapsed,
                "closing during a real comparison load prevents late preview attachment");
            var replacedComparison = ShowComparisonAsync(first);
            ShowRecentResults();
            await replacedComparison.WaitAsync(TimeSpan.FromSeconds(30));
            Require(_comparisonView is null && _recentVisible && !SystemsDescendants<ImageCompareView>(_panelContent).Any(),
                "opening a different card invalidates stale comparison work");
            Named("Копировать").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await ResultTasksAsync();
            Require(sink.Payloads.Count == 3 && sink.Payloads[^1].GetFileDropList()[0] == firstResult.OutputPath,
                "recent-results card exposes the same validated file-copy action");
            await CopyKnownResultAsync(output, false);
            Require(sink.Payloads[^1].GetFileDropList().Cast<string>().SequenceEqual(new[] { output }),
                "result copy integration also accepts an existing output directory");
            int copiedBeforeMissing = sink.Payloads.Count;
            await CopyKnownResultAsync(Path.Combine(output, "missing-result.webp"), false);
            Require(sink.Payloads.Count == copiedBeforeMissing && !string.IsNullOrWhiteSpace(_toastText.Text),
                "missing result gives feedback without replacing captured clipboard contents");

            var failedSource = Path.Combine(fixtures, "удаляемый источник.png");
            File.Copy(source, failedSource);
            await _queueGate.WaitAsync();gateHeld = true;
            Enqueue(source, new ImageJobOptions { Action = ImageAction.WebP, Quality = 62, OutputDirectory = output });
            var waiting = _jobs[^1];
            Enqueue(source, new ImageJobOptions { Action = ImageAction.WebP, Quality = 63, OutputDirectory = output });
            var cancelled = _jobs[^1];cancelled.Cancellation.Cancel();
            await UntilAsync(() => cancelled.Finished, "queued task cancellation reaches a real terminal state");
            Enqueue(failedSource, new ImageJobOptions { Action = ImageAction.WebP, Quality = 64, OutputDirectory = output });
            var failed = _jobs[^1];File.Delete(failedSource);
            ShowJobs();
            Filter(JobListFilter.Active, "ОРИГИНАЛ");
            Require(FilteredJobs().Select(job => job.Id).SequenceEqual(new[] { waiting.Id }), "Active filter and case-insensitive search isolate the queued original-image job");
            var searchBox = _jobSearchBox!;var filterPicker = _jobFilterPicker!;
            Activate();searchBox.Focus();Keyboard.Focus(searchBox);searchBox.CaretIndex = 3;
            RefreshJobs();
            Require(ReferenceEquals(searchBox, _jobSearchBox) && ReferenceEquals(filterPicker, _jobFilterPicker)
                && ReferenceEquals(Keyboard.FocusedElement, searchBox) && searchBox.CaretIndex == 3,
                "refresh preserves search/filter control identity, keyboard focus and caret position");
            Require(!Named("Убрать завершённые").IsEnabled, "active-only filtered rows cannot be removed by completed-history clear");
            await ShotAsync("04-filtered-active.png");
            _queueGate.Release();gateHeld = false;
            await Task.WhenAll(_jobTasks).WaitAsync(TimeSpan.FromSeconds(45));
            await FlushHistoryAsync();
            Require(waiting.HistoryStatus == HistoryStatus.Succeeded && cancelled.HistoryStatus == HistoryStatus.Cancelled
                && failed.HistoryStatus == HistoryStatus.Failed && failed.Result is null, "real processing, cancellation and a deleted diagnostic clone create distinct mixed statuses");
            Require(!File.Exists(failedSource) && File.Exists(source) && ResultsDigest(source) == originalHash,
                "failure fixture deletes only its owned clone and preserves the original image");
            foreach (var sample in new[] { (JobListFilter.Succeeded, 2), (JobListFilter.Failed, 1), (JobListFilter.Cancelled, 1), (JobListFilter.Interrupted, 1), (JobListFilter.Active, 0) })
            {
                Filter(sample.Item1);
                Require(FilteredJobs().Count() == sample.Item2, "status filter returns the expected actual jobs: " + sample.Item1);
            }
            Filter(JobListFilter.All, Path.GetFileName(firstResult.OutputPath));
            Require(FilteredJobs().Single().Id == first.Id, "search finds a job by its generated result filename");
            Filter(JobListFilter.All, "WebP");
            Require(FilteredJobs().Count() == _jobs.Count, "search also matches the user-visible operation name");
            Named("Сбросить фильтры").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(_jobListFilter == JobListFilter.All && _jobSearch.Length == 0 && FilteredJobs().Count() == _jobs.Count,
                "reset button restores all jobs and clears search through the real filter controls");

            await _queueGate.WaitAsync();gateHeld = true;
            Enqueue(source, new ImageJobOptions { Action = ImageAction.WebP, Quality = 66, OutputDirectory = output });
            var retainedActive = _jobs[^1];
            await FlushHistoryAsync();
            Filter(JobListFilter.Failed, "удаляемый");
            Require(FilteredJobs().Single().Id == failed.Id, "failed-status and source-path search select exactly the intended terminal record");
            await ShotAsync("05-filtered-failure.png");
            var retainedIds = _jobs.Where(job => job.Id != failed.Id).Select(job => job.Id).ToHashSet();
            var observedIds = new List<HashSet<Guid>>();
            observerCancellation = new CancellationTokenSource();
            var observerToken = observerCancellation.Token;
            var firstObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            observer = Task.Run(async () =>
            {
                try
                {
                    var reader = new HistoryStore(historyPath);
                    while (!observerToken.IsCancellationRequested)
                    {
                        var snapshot = await reader.LoadAsync();
                        if (snapshot.Error is not null) throw new InvalidOperationException(snapshot.Error);
                        observedIds.Add(snapshot.Entries.Select(entry => entry.Id).ToHashSet());
                        firstObserved.TrySetResult();
                        await Task.Delay(1, observerToken);
                    }
                }
                catch (OperationCanceledException) when (observerToken.IsCancellationRequested) { }
                catch (Exception exception) { firstObserved.TrySetException(exception);throw; }
            });
            await firstObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Named("Убрать завершённые").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await FlushHistoryAsync();
            observerCancellation.Cancel();await observer.WaitAsync(TimeSpan.FromSeconds(5));
            Require(!_jobs.Contains(failed) && _jobs.Select(job => job.Id).ToHashSet().SetEquals(retainedIds),
                "filtered clear removes only matching failed rows and preserves every other status");
            Require(!retainedActive.Finished && !retainedActive.Cancellation.IsCancellationRequested && _jobs.Contains(retainedActive),
                "scoped clear preserves the real queued task outside the filter");
            Require(observedIds.Count > 0 && observedIds.All(snapshot => retainedIds.IsSubsetOf(snapshot)),
                "concurrent history reads never observe unrelated terminal or active records disappearing during atomic scoped clear");
            var scoped = await new HistoryStore(historyPath).LoadAsync();
            await File.WriteAllTextAsync(Path.Combine(run, "scoped-clear-observation.json"), JsonSerializer.Serialize(new
            {
                _historyWarning, _historyBlocked, ExpectedIds = retainedIds, UiIds = _jobs.Select(job => job.Id).ToArray(),
                SavedIds = scoped.Entries.Select(entry => entry.Id).ToArray(), scoped.Error, ObservedSnapshots = observedIds.Count
            }, new JsonSerializerOptions { WriteIndented = true }));
            Require(scoped.Error is null && scoped.Entries.Select(entry => entry.Id).ToHashSet().SetEquals(retainedIds),
                "history round trip after scoped clear contains exactly the retained record IDs; warning=" + (_historyWarning ?? scoped.Error ?? "none"));
            Require(ReferenceEquals(searchBox, _jobSearchBox) && ReferenceEquals(filterPicker, _jobFilterPicker),
                "filter/search controls also survive scoped clear and card refresh");
            _queueGate.Release();gateHeld = false;
            await Task.WhenAll(_jobTasks).WaitAsync(TimeSpan.FromSeconds(45));
            await FlushHistoryAsync();
            Require(retainedActive.Finished && retainedActive.Result is not null && File.Exists(retainedActive.Result.OutputPath),
                "queued task retained across scoped clear completes with a real new result");
            Filter(JobListFilter.Succeeded, Path.GetFileName(firstResult.OutputPath));
            Require(FilteredJobs().Single().Id == first.Id, "success-plus-result search scopes a second clear to one successful row");
            Named("Убрать завершённые").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await FlushHistoryAsync();
            Require(!_jobs.Contains(first) && _jobs.Contains(waiting) && _jobs.Contains(cancelled) && _jobs.Contains(interrupted),
                "clearing one searched successful row leaves other successful, cancelled and interrupted history intact");
            Require(File.Exists(firstResult.OutputPath) && ResultsDigest(firstResult.OutputPath) == resultHash
                && File.Exists(waiting.Result!.OutputPath) && File.Exists(retainedActive.Result!.OutputPath),
                "scoped history removal preserves all successful output files and original output bytes");
            Require(ResultsDigest(source) == originalHash && sink.Sta && sink.Persistent,
                "comparison, clipboard and filtering never modify source bytes and only use the injected STA capture sink");
            var final = await new HistoryStore(historyPath).LoadAsync();
            Require(final.Error is null && final.Entries.Count == _jobs.Count && final.Entries.All(entry => entry.Id != first.Id && entry.Id != failed.Id),
                "final durable history exactly matches the UI after both scoped clears");
            var report = JsonSerializer.Serialize(new { Passed = checks.Count, Checks = checks, Artifacts = run,
                HistoryPath = historyPath, NativeClipboardReads = 0, NativeClipboardWrites = 0, NativeShellLaunches = 0 }, new JsonSerializerOptions { WriteIndented = true });
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
            observerCancellation?.Cancel();
            if (observer is not null) try { await observer.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            observerCancellation?.Dispose();
            if (gateHeld) _queueGate.Release();
            Quit();
        }
    }

    private static string ResultsDigest(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void WriteResultsImage(string path)
    {
        const int width = 280, height = 180;
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = (y * width + x) * 4;
            pixels[offset] = (byte)(40 + x * 180 / width);
            pixels[offset + 1] = (byte)(30 + y * 180 / height);
            pixels[offset + 2] = (byte)(220 - y * 130 / height);
            pixels[offset + 3] = x < width / 3 ? (byte)100 : (byte)255;
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));
        using var output = File.Create(path);encoder.Save(output);
    }
    private sealed class ResultsClipboardCapture(Dispatcher owner) : IResultClipboardSink
    {
        public List<FormsDataObject> Payloads { get; } = new();
        public bool Sta { get; private set; } = true;
        public bool Persistent { get; private set; } = true;
        public void SetDataObject(FormsDataObject payload, bool copy)
        {
            Sta &= owner.CheckAccess() && Thread.CurrentThread.GetApartmentState() == ApartmentState.STA;
            Persistent &= copy;Payloads.Add(payload);
        }
    }
}
