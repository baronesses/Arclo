namespace Orbit.Core.Comparison;

/// <summary>Read-only static image comparison previews using the app's existing codec and color pipeline.</summary>
public sealed class ImageComparisonEngine
{
    public const long MaximumSourceBytes = 256L * 1024 * 1024;
    public const long MaximumSourcePixels = 40_000_000;
    public const int MaximumSourceDimension = 16000;
    public const int MaximumPreviewSize = 1400;
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    { ".png", ".jpg", ".jpeg", ".webp", ".avif", ".bmp", ".tif", ".tiff" };
    private readonly ImageMagickEngine _images;

    public ImageComparisonEngine(string? imageMagickExecutable = null) => _images = new ImageMagickEngine(imageMagickExecutable);

    /// <summary>Returns PNG previews with alpha and EXIF orientation applied. AspectCompatible compares
    /// oriented original metadata ratios with relative tolerance 1e-5; preview rounding can differ, so a
    /// renderer should also compare its actual bitmap dimensions before using an overlay.</summary>
    public async Task<ImageComparisonResult> LoadAsync(string beforePath, string afterPath, int maxSize = 1400,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maxSize is < 1 or > MaximumPreviewSize)
            throw new ImageProcessingException(global::Orbit.Core.Localization.L.Format("Text_37C0AE37358F",(object?)(MaximumPreviewSize)));
        var before = NormalizePath(beforePath, global::Orbit.Core.Localization.L.Get("Text_44A2B1F65AE7"));
        var after = NormalizePath(afterPath, global::Orbit.Core.Localization.L.Get("Text_4997A5143D9F"));
        // Read sharing permits the native decoder's independent readers while preventing a rename,
        // replacement or write between metadata inspection and PNG decoding on Windows.
        await using var beforeFile = await OpenInputAsync(before, global::Orbit.Core.Localization.L.Get("Text_44A2B1F65AE7"), cancellationToken).ConfigureAwait(false);
        await using var afterFile = await OpenInputAsync(after, global::Orbit.Core.Localization.L.Get("Text_4997A5143D9F"), cancellationToken).ConfigureAwait(false);
        var original = await InspectAsync(before, global::Orbit.Core.Localization.L.Get("Text_44A2B1F65AE7"), cancellationToken).ConfigureAwait(false);
        var result = await InspectAsync(after, global::Orbit.Core.Localization.L.Get("Text_4997A5143D9F"), cancellationToken).ConfigureAwait(false);
        // Render sequentially: each native decoder already has its own bounded memory limit.
        var beforePng = await PreviewAsync(before, global::Orbit.Core.Localization.L.Get("Text_44A2B1F65AE7"), maxSize, cancellationToken).ConfigureAwait(false);
        var afterPng = await PreviewAsync(after, global::Orbit.Core.Localization.L.Get("Text_4997A5143D9F"), maxSize, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var first = (long)original.Width * result.Height;
        var second = (long)result.Width * original.Height;
        var compatible = Math.Abs(first - second) * 100000L <= Math.Max(first, second);
        return new ImageComparisonResult(original, result, beforePng, afterPng, compatible);
    }

    private async Task<ImageMetadata> InspectAsync(string path, string caption, CancellationToken cancellationToken)
    {
        try
        {
            var metadata = await _images.InspectAsync(path, cancellationToken).ConfigureAwait(false);
            if (metadata.Frames != 1) throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_4E9F187EA849"));
            if (metadata.Bytes > MaximumSourceBytes) throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_FBC4FD84D795"));
            if (metadata.Width > MaximumSourceDimension || metadata.Height > MaximumSourceDimension || (long)metadata.Width * metadata.Height > MaximumSourcePixels)
                throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_BF58E030FDAE"));
            return metadata;
        }
        catch (ImageProcessingException exception) { throw new ImageProcessingException(caption + ": " + exception.Message, exception); }
    }

    private async Task<byte[]> PreviewAsync(string path, string caption, int maxSize, CancellationToken cancellationToken)
    {
        try { return await _images.CreatePreviewAsync(path, maxSize, cancellationToken).ConfigureAwait(false); }
        catch (ImageProcessingException exception) { throw new ImageProcessingException(caption + ": " + exception.Message, exception); }
    }

    private static string NormalizePath(string path, string caption)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ImageProcessingException(caption + global::Orbit.Core.Localization.L.Get("Text_943DDAE5305C"));
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!Extensions.Contains(Path.GetExtension(fullPath)))
                throw new ImageProcessingException(caption + global::Orbit.Core.Localization.L.Get("Text_D57888DCCF24"));
            return fullPath;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        { throw new ImageProcessingException(caption + global::Orbit.Core.Localization.L.Get("Text_9B100B2F657C"), exception); }
    }

    private static async Task<FileStream> OpenInputAsync(string path, string caption, CancellationToken cancellationToken)
    {
        // File.Open can block on an unavailable network path. Keep it off the UI thread, bound the
        // caller's wait, and dispose any late handle even after cancellation/timeout wins the race.
        var open = Task.Run(() => OpenInput(path, caption, cancellationToken), CancellationToken.None);
        try { return await open.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            _ = open.ContinueWith(task =>
            {
                if (task.IsCompletedSuccessfully) task.Result.Dispose();
                else _ = task.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            if (exception is OperationCanceledException) throw;
            throw new ImageProcessingException(caption + global::Orbit.Core.Localization.L.Get("Text_078FD987A8E9"), exception);
        }
    }

    private static FileStream OpenInput(string path, string caption, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.Length > MaximumSourceBytes) throw new ImageProcessingException(caption + global::Orbit.Core.Localization.L.Get("Text_D1621FD326C0"));
                if (file.Length == 0) throw new ImageProcessingException(caption + global::Orbit.Core.Localization.L.Get("Text_AB79D9F185B4"));
                return file;
            }
            catch { file.Dispose(); throw; }
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        { throw new ImageProcessingException(caption + global::Orbit.Core.Localization.L.Get("Text_0277CFE2DA4A"), exception); }
        catch (UnauthorizedAccessException exception)
        { throw new ImageProcessingException(caption + global::Orbit.Core.Localization.L.Get("Text_A0B8168BD6EA"), exception); }
        catch (IOException exception)
        { throw new ImageProcessingException(caption + global::Orbit.Core.Localization.L.Get("Text_9915E5B16171"), exception); }
    }
}
