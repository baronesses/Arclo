using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Orbit.App.Controls;
using Orbit.App.History;
using Orbit.App.Platform;
using Orbit.Core;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private async Task RunSelectionLatencySmokeAsync()
    {
        var destination = Path.Combine(AppContext.BaseDirectory, "selection-latency-smoke");
        var run = Path.Combine(destination, "run-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        var checks = new List<string>();var owned = new List<Task>();var release = new List<Action>();
        var timings = new Dictionary<string, double>();
        void Require(bool value, string name) { if (!value) throw new InvalidOperationException("Selection latency integration: " + name);checks.Add(name); }
        async Task Until(Func<bool> ready, string name)
        {
            var watch = Stopwatch.StartNew();
            while (!ready() && watch.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
            if (!ready()) throw new TimeoutException(name);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        }
        Task Track(Task task) { owned.Add(task);return task; }
        bool Current(string path) => _files.Count == 1 && _files[0] == path && _metadata?.Path == path;
        bool Actionable() => !_selectionLoading && _radial.Children.OfType<SectorButton>().All(s => s.HasFile);
        static TaskCompletionSource<T> Gate<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        static ToolkitMetadata Metadata(string path) => new(path, "PNG", 80, 48, new FileInfo(path).Length, ToolkitKind.Image);
        static string Digest(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        static bool Blue(ImageSource? source)
        {
            if (source is not BitmapSource bitmap) return false;
            var pixel = new byte[4];var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            converted.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
            return pixel[0] > 180 && pixel[2] < 60 && pixel[3] == 255;
        }
        static byte[] Fixture(string path, bool blue)
        {
            var pixels = new byte[80 * 48 * 4];for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = blue ? (byte)230 : (byte)25;pixels[i + 1] = 45;pixels[i + 2] = blue ? (byte)25 : (byte)230;pixels[i + 3] = 255; }
            var bitmap = BitmapSource.Create(80, 48, 96, 96, PixelFormats.Bgra32, null, pixels, 80 * 4);
            var encoder = new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();encoder.Save(stream);var bytes = stream.ToArray();File.WriteAllBytes(path, bytes);return bytes;
        }
        try
        {
            Directory.CreateDirectory(run);File.Delete(Path.Combine(destination, "passed.json"));File.Delete(Path.Combine(destination, "error.txt"));
            Require(_args.Contains("--selection-latency-smoke") && IsDiagnosticRun && _bridge is null && !ProductivityIoAllowed && !PersonalizationIoAllowed,
                "isolated mode disables real COM selection, bridge, user stores, clipboard and shell writes");
            Require(Path.GetTempPath().StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase), "temporary native work stays inside diagnostic runtime");
            var oldFile = Path.Combine(run, "старое фото [1].png");var newFile = Path.Combine(run, "новое фото [2].png");
            var oldPng = Fixture(oldFile, false);var newPng = Fixture(newFile, true);var oldHash = Digest(oldFile);var newHash = Digest(newFile);
            _selectionInspectorOverride = (path, _) => Task.FromResult(Metadata(path));
            _selectionPreviewOverride = (path, _) => Task.FromResult<byte[]?>(path == oldFile ? oldPng : newPng);
            Require(await LoadFilesAsync(new[] { oldFile }), "owned old source seeds a real previous selection");
            var previousPreview = _preview;
            var ownerA = new IntPtr(0x100001);var ownerB = new IntPtr(0x100002);var foreign = new IntPtr(0x100003);
            var selection = Gate<ExplorerSelectionResult>();var inspect = Gate<ToolkitMetadata>();var preview = Gate<byte[]?>();var inspectorEntered = Gate<bool>();
            release.Add(() => selection.TrySetResult(new(Array.Empty<string>(), null)));release.Add(() => inspect.TrySetResult(Metadata(newFile)));release.Add(() => preview.TrySetResult(newPng));
            _selectionReaderOverride = _ => selection.Task;_invocationForegroundOverride = () => ownerA;
            _selectionInspectorOverride = (_, _) => { inspectorEntered.TrySetResult(true);return inspect.Task; };
            _selectionPreviewOverride = (_, _) => preview.Task;
            var firstPaint = Stopwatch.StartNew();var invocation = Track(InvokeAtCursorCoreAsync(new(ownerA, 500, 350)));
            await Until(() => IsVisible && WindowState == WindowState.Normal && _radial.FileName.Text == "Получаем выделение", "popup first paint before COM response");
            timings["PendingSelectionFirstPaintMilliseconds"] = firstPaint.Elapsed.TotalMilliseconds;
            Require(!selection.Task.IsCompleted && !invocation.IsCompleted && !ShowActivated, "popup is visible without activation while captured Explorer selection is still pending");
            Require(_files.Count == 0 && _metadata is null && _preview is null && !ReferenceEquals(_radial.Preview.Source, previousPreview), "pending invocation clears old paths, metadata and preview immediately");
            Require(_selectionLoading && _radial.Children.OfType<SectorButton>().All(s => !s.HasFile), "old source actions remain unavailable while selection is unresolved");
            int before = _jobs.Count;QueueSelected(new ImageJobOptions { Action = ImageAction.WebP });Require(_jobs.Count == before, "pending selection cannot enqueue work for the previous file");
            selection.SetResult(new(new[] { newFile }, null));await inspectorEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(_radial.FileName.Text == Path.GetFileName(newFile) && _selectionLoading && _files.Count == 0, "new filename is visible before delayed metadata becomes valid");
            Require(_preview is null && !ReferenceEquals(_radial.Preview.Source, previousPreview), "known filename never temporarily restores the old thumbnail");
            inspect.SetResult(Metadata(newFile));await Until(() => Current(newFile) && Actionable(), "metadata publication before delayed preview");
            Require(!preview.Task.IsCompleted && !invocation.IsCompleted, "validated metadata enables actions while thumbnail decoding is still pending");
            Require(!_radial.Children.OfType<SectorButton>().Single(s=>s.Action==ImageAction.Crop).IsEnabled,"visual crop waits for the actual image instead of exposing a generic file icon as its canvas");
            Require(_radial.CenterHint.Text == "Выберите действие" && _radial.Children.OfType<SectorButton>().Any(s => s.Action == ImageAction.Png), "the new file gets its current image actions before preview completion");
            preview.SetResult(newPng);await invocation.WaitAsync(TimeSpan.FromSeconds(5));
            Require(Current(newFile) && Blue(_preview), "matching thumbnail eventually publishes for the same current source");
            Require(_radial.Children.OfType<SectorButton>().Single(s=>s.Action==ImageAction.Crop).IsEnabled,"visual crop becomes available when the matching thumbnail is ready");

            var oldInspect = Gate<ToolkitMetadata>();var oldPreview = Gate<byte[]?>();var oldEntered = Gate<bool>();
            release.Add(() => oldInspect.TrySetResult(Metadata(oldFile)));release.Add(() => oldPreview.TrySetResult(oldPng));
            _selectionInspectorOverride = (path, _) => path == oldFile ? SlowInspect() : Task.FromResult(Metadata(path));
            _selectionPreviewOverride = (path, _) => path == oldFile ? oldPreview.Task : Task.FromResult<byte[]?>(newPng);
            var staleLoader = Track(LoadFilesAsync(new[] { oldFile }));await oldEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(_radial.FileName.Text == Path.GetFileName(oldFile) && _preview is null && _files.Count == 0, "a subsequent load clears the already displayed source before its own decoder waits");
            Require(await LoadFilesAsync(new[] { newFile }), "a fast newer selection can finish while old metadata ignores cancellation");
            oldInspect.SetResult(Metadata(oldFile));oldPreview.SetResult(oldPng);await staleLoader.WaitAsync(TimeSpan.FromSeconds(5));
            Require(Current(newFile) && Blue(_preview) && Actionable(), "late cancelled metadata and thumbnail cannot overwrite a newer selection");

            var previewOnly = Gate<byte[]?>();release.Add(() => previewOnly.TrySetResult(oldPng));
            _selectionInspectorOverride = (path, _) => Task.FromResult(Metadata(path));
            _selectionPreviewOverride = (path, _) => path == oldFile ? previewOnly.Task : Task.FromResult<byte[]?>(newPng);
            var stalePreview = Track(LoadFilesAsync(new[] { oldFile }));await Until(() => Current(oldFile) && Actionable(), "old metadata ready with pending old preview");
            Require(!stalePreview.IsCompleted, "a metadata-ready selection can still own an unfinished preview task");
            Require(await LoadFilesAsync(new[] { newFile }), "new source replaces a metadata-ready source without awaiting its old preview");
            previewOnly.SetResult(oldPng);await stalePreview.WaitAsync(TimeSpan.FromSeconds(5));
            Require(Current(newFile) && Blue(_radial.Preview.Source), "old preview completion alone cannot repaint the new center card");

            var firstCom = Gate<ExplorerSelectionResult>();var secondCom = Gate<ExplorerSelectionResult>();int reads = 0;int staleInspections = 0;
            release.Add(() => firstCom.TrySetResult(new(Array.Empty<string>(), null)));release.Add(() => secondCom.TrySetResult(new(Array.Empty<string>(), null)));
            _selectionReaderOverride = _ => ++reads == 1 ? firstCom.Task : secondCom.Task;
            _selectionInspectorOverride = (path, _) => { if (path == oldFile) staleInspections++;return Task.FromResult(Metadata(path)); };
            _selectionPreviewOverride = (_, _) => Task.FromResult<byte[]?>(newPng);_invocationForegroundOverride = () => ownerA;
            var oldInvocation = Track(InvokeAtCursorCoreAsync(new(ownerA, 510, 350)));await Until(() => reads == 1 && IsVisible, "first pending invocation");
            _invocationForegroundOverride = () => ownerB;var newInvocation = Track(InvokeAtCursorCoreAsync(new(ownerB, 520, 350)));await Until(() => reads == 2 && IsVisible, "second pending invocation");
            secondCom.SetResult(new(new[] { newFile }, null));await newInvocation.WaitAsync(TimeSpan.FromSeconds(5));
            firstCom.SetResult(new(new[] { oldFile }, null));await oldInvocation.WaitAsync(TimeSpan.FromSeconds(5));
            Require(Current(newFile) && Blue(_preview) && staleInspections == 0, "late Explorer response from an older invocation cannot even start old-file inspection");
            Require(_pendingPopupInvocation == 0 && !_selectionLoading, "only the current invocation clears pending/loading state");

            var foreignSelection = Gate<ExplorerSelectionResult>();release.Add(() => foreignSelection.TrySetResult(new(Array.Empty<string>(), null)));int foreignInspections = 0;
            _selectionReaderOverride = _ => foreignSelection.Task;_invocationForegroundOverride = () => foreign;
            _selectionInspectorOverride = (path, _) => { foreignInspections++;return Task.FromResult(Metadata(path)); };
            var foreignInvocation = Track(InvokeAtCursorCoreAsync(new(ownerA, 530, 350)));await Until(() => IsVisible && !ShowActivated, "foreign-window pending selection");
            foreignSelection.SetResult(new(new[] { newFile }, null));await foreignInvocation.WaitAsync(TimeSpan.FromSeconds(5));
            Require((!IsVisible || WindowState == WindowState.Minimized) && foreignInspections == 0 && _files.Count == 0, "changing to a foreign foreground owner dismisses instead of activating or reading the selected file");
            Require(ShowActivated && !_selectionLoading, "foreign-owner rejection restores future activation behavior and ends loading");

            _selectionInspectorOverride = (path, _) => Task.FromResult(Metadata(path));
            _selectionPreviewOverride = (_, _) => Task.FromException<byte[]?>(new IOException("Owned diagnostic thumbnail failure"));
            Require(await LoadFilesAsync(new[] { newFile }), "thumbnail failure does not fail a successfully validated source");
            Require(Current(newFile) && Actionable() && _preview is not null && _radial.FileName.Text == Path.GetFileName(newFile), "preview error retains the new file metadata, fallback icon and usable actions");

            _selectionReaderOverride = null;_invocationForegroundOverride = null;_selectionInspectorOverride = null;_selectionPreviewOverride = null;
            Require(await LoadFilesAsync(new[] { newFile }) && _metadata is { Width: 80, Height: 48, Format: "PNG" }, "hooks removed: production metadata and thumbnail loaders validate the actual PNG");
            _outputDirectory = Path.Combine(run, "output");Directory.CreateDirectory(_outputDirectory);Enqueue(newFile, new ImageJobOptions { Action = ImageAction.WebP, Quality = 85, OutputDirectory = _outputDirectory });
            var actualJob = _jobs.Last();await Task.WhenAll(_jobTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(45));
            Require(actualJob.HistoryStatus == HistoryStatus.Succeeded && actualJob.Result?.Format == "WEBP" && File.Exists(actualJob.Result.OutputPath), "current selected source still completes a real independently validated native WebP job");
            Require(actualJob.Result!.OutputPath != newFile && Digest(oldFile) == oldHash && Digest(newFile) == newHash, "all selection races and real processing preserve both original PNG fixtures");
            Require(!ProductivityIoAllowed && !PersonalizationIoAllowed, "diagnostic completion keeps all user persistence disabled");
            File.WriteAllText(Path.Combine(destination, "passed.json"), JsonSerializer.Serialize(new { Passed = checks.Count, Failed = 0, Checks = checks, Timings = timings, Fixtures = run, NativeComRead = false, NativeInputSynthesized = false, UserProfileRead = false, UserProfileWritten = false, RealNativeWebP = true, CachePerformanceReport = "artifacts/selection-cache-tests/20261021-133206-403/passed.json" }, new JsonSerializerOptions { WriteIndented = true }));

            Task<ToolkitMetadata> SlowInspect() { oldEntered.TrySetResult(true);return oldInspect.Task; }
        }
        catch (Exception error) { Directory.CreateDirectory(destination);File.WriteAllText(Path.Combine(destination, "error.txt"), error + "\n" + string.Join("\n", checks)); }
        finally
        {
            ++_loadVersion;++_invocationVersion;_loadCancellation?.Cancel();foreach (var complete in release) complete();
            try { await Task.WhenAll(owned.Concat(_loadTasks).Concat(_jobTasks)).WaitAsync(TimeSpan.FromSeconds(45)); }
            catch (Exception error) { App.Log(error); }
            _selectionReaderOverride = null;_invocationForegroundOverride = null;_selectionInspectorOverride = null;_selectionPreviewOverride = null;
            Quit();
        }
    }
}
