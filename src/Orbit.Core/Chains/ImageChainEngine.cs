using Orbit.Core.Metadata;

namespace Orbit.Core.Chains;

/// <summary>Runs 2–4 real image processors privately, then atomically publishes only the last validated image.</summary>
public sealed class ImageChainEngine
{
    private readonly ImageMagickEngine _images;
    private readonly ImageMetadataCleaner _cleaner;
    private readonly TimeSpan _timeout;
    internal Action<int, ImageJobResult>? StepCompleted { get; set; }
    public ImageChainEngine(string? imageMagickExecutable = null, TimeSpan? operationTimeout = null)
    {
        _timeout = operationTimeout ?? TimeSpan.FromMinutes(10);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        _images = new ImageMagickEngine(imageMagickExecutable, _timeout);
        _cleaner = new ImageMetadataCleaner(imageMagickExecutable, _timeout);
    }
    public string ExecutablePath => _images.ExecutablePath;

    public Task<ImageJobResult> ExecuteAsync(string path, IReadOnlyList<ImageJobOptions> steps, string? outputDirectory = null,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<ImageJobOptions> snapshot;
        try { snapshot = ImageChainPolicy.SanitizeSteps(steps); }
        catch (ImageChainValidationException ex) { throw new ImageProcessingException(ex.Message, ex); }
        return Task.Run(() => ExecuteCoreAsync(path, snapshot, outputDirectory, progress, ct), ct);
    }

    private async Task<ImageJobResult> ExecuteCoreAsync(string path, IReadOnlyList<ImageJobOptions> steps, string? outputDirectory,
        IProgress<double>? progress, CancellationToken callerToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(callerToken); timeout.CancelAfter(_timeout);
        var ct = timeout.Token; string? temporary = null, parent = null; FileStream? lease = null;
        try
        {
            ct.ThrowIfCancellationRequested(); path = SourcePath(path);
            lease = await OpenAsync(path, ct).ConfigureAwait(false);
            parent = outputDirectory is null ? Path.GetDirectoryName(path)! : Path.GetFullPath(outputDirectory);
            if (!Directory.Exists(parent)) throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_45C406D632F2"));
            temporary = Path.Combine(parent, ".orbit-image-chain-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary); progress?.Report(0);
            // A fixed leaf also keeps long user filenames and sequence-like %d tokens out of every native stage.
            var workingSource = Path.Combine(temporary, "input" + Path.GetExtension(path));
            await using (var snapshot = new FileStream(workingSource, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await lease.CopyToAsync(snapshot, ct).ConfigureAwait(false);
            var source = await _images.InspectAsync(workingSource, ct).ConfigureAwait(false); ValidateImage(source);
            var current = source; long cumulative = source.Bytes; ImageJobResult? result = null;
            for (var index = 0; index < steps.Count; index++)
            {
                ct.ThrowIfCancellationRequested(); ValidateImage(current); RecheckSource(path);
                var step = steps[index]; var (width, height) = ImageChainPolicy.StepSize(current.Width, current.Height, step);
                if (!ImageChainPolicy.Bounded(width, height, 1)) throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_43A575386130"));
                var stage = Path.Combine(temporary, "step-" + (index + 1)); Directory.CreateDirectory(stage);
                var stepIndex = index;
                var stageProgress = new InlineProgress(value => progress?.Report(Math.Clamp((stepIndex + value) / steps.Count * .95, 0, .95)));
                stageProgress.Report(0);
                result = step.Action switch
                {
                    ImageAction.ImageTransform => await _images.TransformAsync(current.Path, new ImageTransformOptions { OutputDirectory = stage,
                        RotationDegrees = step.RotationDegrees, FlipHorizontal = step.FlipHorizontal, FlipVertical = step.FlipVertical }, ct).ConfigureAwait(false),
                    ImageAction.RemoveImageMetadata => await _cleaner.CleanAsync(current.Path, new ImageMetadataCleanOptions { OutputDirectory = stage }, ct).ConfigureAwait(false),
                    _ => await _images.ExecuteAsync(current.Path, step with { OutputDirectory = stage }, stageProgress, ct).ConfigureAwait(false)
                };
                ct.ThrowIfCancellationRequested();
                var actualPath = Path.GetFullPath(result.OutputPath);
                if (!actualPath.StartsWith(Path.GetFullPath(stage) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_774DDDC64654"));
                current = await _images.InspectAsync(actualPath, ct).ConfigureAwait(false); ValidateImage(current);
                cumulative = checked(cumulative + current.Bytes);
                if (cumulative > ImageChainPolicy.MaximumIntermediateBytes) throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_D155D66939BA"));
                stageProgress.Report(1); StepCompleted?.Invoke(index, result); ct.ThrowIfCancellationRequested();
            }
            RecheckSource(path); ct.ThrowIfCancellationRequested();
            var extension = Path.GetExtension(current.Path);
            var target = Commit(current.Path, parent, path, extension, ct);
            progress?.Report(1);
            return new(target, current.Bytes, current.Width, current.Height, current.Format, lease.Length);
        }
        catch (OperationCanceledException ex) when (!callerToken.IsCancellationRequested && timeout.IsCancellationRequested)
        { throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_04577043ED47"), ex); }
        catch (UnauthorizedAccessException ex) { throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_EA1DC1FC4499"), ex); }
        catch (IOException ex) { throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_CCEF920C785C"), ex); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        { throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_28F6DD0BED68"), ex); }
        finally
        {
            try { await CleanupAsync(temporary).ConfigureAwait(false); }
            finally { lease?.Dispose(); }
        }
    }
    private static void ValidateImage(ImageMetadata source)
    {
        if (source.Frames != 1 || !ImageChainPolicy.Bounded(source.Width, source.Height, source.Bytes))
            throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_4155D66C2CAC"));
    }
    private static string SourcePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_9F0CF3C29F84"));
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal)
            || full.IndexOf(':', full.Length > 1 && full[1] == ':' ? 2 : 0) >= 0)
            throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_CBE1E8B0512C"));
        RecheckSource(full); return full;
    }
    private static void RecheckSource(string path)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_7F955BCE70CD"));
        for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_2FE3E9967238"));
    }
    private static async Task<FileStream> OpenAsync(string path, CancellationToken ct)
    {
        var opening = Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested(); RecheckSource(path);
            var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            try
            {
                RecheckSource(path); ct.ThrowIfCancellationRequested();
                if (input.Length is <= 0 or > ImageChainPolicy.MaximumFileBytes || (File.GetAttributes(input.SafeFileHandle) & FileAttributes.ReparsePoint) != 0)
                    throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_0B9C65963D2E"));
                return input;
            }
            catch { input.Dispose(); throw; }
        }, CancellationToken.None);
        try { return await opening.WaitAsync(ct).ConfigureAwait(false); }
        catch
        {
            _ = opening.ContinueWith(task => { if (task.IsCompletedSuccessfully) task.Result.Dispose(); else _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default); throw;
        }
    }
    private static string Commit(string file, string directory, string source, string extension, CancellationToken ct)
    {
        var stem = Path.GetFileNameWithoutExtension(source);
        if (stem.Length > 200) { stem = stem[..200]; if (char.IsHighSurrogate(stem[^1])) stem = stem[..^1]; }
        for (var index = 0; index < 10000; index++)
        {
            ct.ThrowIfCancellationRequested(); var target = Path.Combine(directory, stem + "_chain" + (index == 0 ? "" : "_" + index) + extension);
            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Move(file, target, overwrite: false); return target; }
            catch (IOException) when (File.Exists(target) || Directory.Exists(target)) { }
        }
        throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_F61DD6D4194D"));
    }
    private static async Task CleanupAsync(string? directory)
    {
        if (directory is null) return;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try { if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0) Directory.Delete(directory, true); return; }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (attempt < 7) await Task.Delay(Math.Min(250, 25 << attempt)).ConfigureAwait(false);
        }
    }
    private sealed class InlineProgress(Action<double> action) : IProgress<double> { public void Report(double value) => action(value); }
}
