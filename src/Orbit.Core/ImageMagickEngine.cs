using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Orbit.Core;

/// <summary>Local image operations. Originals are opened for reading and are never replaced.</summary>
public sealed class ImageMagickEngine
{
    public static IImageCodecFallback? ManagedFallback { get; set; }
    public static bool PreferManagedCodecs { get; set; }
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    { ".jpg", ".jpeg", ".png", ".webp", ".avif", ".bmp", ".tif", ".tiff", ".gif", ".ico", ".svg" };
    private static readonly HashSet<string> Formats = new(StringComparer.OrdinalIgnoreCase)
    { "JPEG", "PNG", "WEBP", "AVIF", "BMP", "BMP2", "BMP3", "TIFF", "GIF", "ICO", "SVG", "MSVG" };
    private readonly string? _configuredExecutable;
    private readonly TimeSpan _operationTimeout;

    public ImageMagickEngine(string? executablePath = null, TimeSpan? operationTimeout = null)
    {
        _configuredExecutable = executablePath;
        _operationTimeout = operationTimeout ?? TimeSpan.FromMinutes(3);
        if (_operationTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
    }

    public string ExecutablePath => ResolveExecutable();

    public async Task<ImageMetadata> InspectAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SelectManaged(path) is { } managed)
            return await managed.InspectAsync(path, cancellationToken).ConfigureAwait(false);
        var fullPath = ValidateInputPath(path);
        var signature = await DetectSignatureAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if (signature == "MSVG" && await SvgImagePolicy.ReadContainerAsync(fullPath, cancellationToken).ConfigureAwait(false) is { } png)
            return await WithEmbeddedPngAsync(png, fullPath, async staged =>
            {
                var image = await InspectAsync(staged, cancellationToken).ConfigureAwait(false);
                return image with { Path = fullPath, Format = "SVG", Bytes = new FileInfo(fullPath).Length };
            }, cancellationToken).ConfigureAwait(false);
        return await IdentifyAsync(fullPath, signature, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns a static, auto-oriented PNG thumbnail, including for AVIF and WebP sources.</summary>
    public async Task<byte[]> CreatePreviewAsync(string path, int maxSize = 640, CancellationToken cancellationToken = default)
    {
        if (maxSize is < 1 or > 2048) throw new ArgumentOutOfRangeException(nameof(maxSize), global::Orbit.Core.Localization.L.Get("Text_CDCDFCF1AC8B"));
        if (SelectManaged(path) is { } managed)
            return await managed.CreatePreviewAsync(path, maxSize, cancellationToken).ConfigureAwait(false);
        var source = await InspectAsync(path, cancellationToken).ConfigureAwait(false);
        if (source.Format == "SVG" && await SvgImagePolicy.ReadContainerAsync(source.Path, cancellationToken).ConfigureAwait(false) is { } png)
            return await WithEmbeddedPngAsync(png, source.Path, staged => CreatePreviewAsync(staged, maxSize, cancellationToken), cancellationToken).ConfigureAwait(false);
        var args = ResourceArguments();
        args.AddRange(["-define", "filename:literal=true", InputCoder(source.Format) + ":" + source.Path,
            "-auto-orient", "-thumbnail", FormattableString.Invariant($"{maxSize}x{maxSize}>")]);
        AddSrgbArguments(args);
        args.AddRange(["-depth", "8", "PNG:-"]);
        return await RunBytesAsync(args, TimeSpan.FromSeconds(30), cancellationToken, 32 * 1024 * 1024).ConfigureAwait(false);
    }

    public async Task<ImageJobResult> ExecuteAsync(string path, ImageJobOptions options,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (SelectManaged(path, options) is { } managed)
            return await managed.ExecuteAsync(path, options, progress, cancellationToken).ConfigureAwait(false);
        Report(progress, 0);
        var sourcePath = ValidateInputPath(path);
        await using var sourceLease = await OpenReadLeaseAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var source = await InspectAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        ValidateOptions(options, source);
        if (source.Format == "SVG" && await SvgImagePolicy.ReadContainerAsync(source.Path, cancellationToken).ConfigureAwait(false) is { } png)
            return await WithEmbeddedPngAsync(png, source.Path, async staged =>
            {
                var result = await ExecuteAsync(staged, options with { OutputDirectory = options.OutputDirectory ?? System.IO.Path.GetDirectoryName(source.Path) }, progress, cancellationToken).ConfigureAwait(false);
                return result with { SourceBytes = source.Bytes };
            }, cancellationToken).ConfigureAwait(false);
        Report(progress, 0.12);
        var targetFormat = TargetFormat(source.Format, options);
        var directory = string.IsNullOrWhiteSpace(options.OutputDirectory)
            ? System.IO.Path.GetDirectoryName(source.Path)!
            : System.IO.Path.GetFullPath(options.OutputDirectory);
        if (!Directory.Exists(directory))
            throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_45C406D632F2"));

        var extension = FormatExtension(targetFormat);
        // A private, randomly named directory keeps competing jobs' temporary files separate.
        var temporaryDirectory = System.IO.Path.Combine(directory, ".orbit-" + Guid.NewGuid().ToString("N"));
        var temporaryPath = System.IO.Path.Combine(temporaryDirectory, "result." + extension);
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            var arguments = ResourceArguments();
            arguments.AddRange(["-define", "filename:literal=true", InputCoder(source.Format) + ":" + source.Path, "-auto-orient"]);
            AddOperationArguments(arguments, source, options);
            var encodedFormat = targetFormat == "SVG" ? "PNG" : targetFormat;
            AddEncodingArguments(arguments, source.Format, encodedFormat, options);
            var encodedPath = targetFormat == "SVG" ? System.IO.Path.Combine(temporaryDirectory, "embedded.png") : temporaryPath;
            arguments.Add(encodedFormat + ":" + encodedPath);
            Report(progress, 0.2);
            await RunAsync(arguments, _operationTimeout, cancellationToken).ConfigureAwait(false);
            if (targetFormat == "SVG")
                await SvgImagePolicy.WriteContainerAsync(encodedPath, temporaryPath, cancellationToken).ConfigureAwait(false);
            Report(progress, 0.85);
            if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length == 0)
                throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_E77B0E76F7E5"));
            var output = await IdentifyAsync(encodedPath, encodedFormat, cancellationToken).ConfigureAwait(false);
            if (targetFormat == "SVG") output = output with { Path = temporaryPath, Format = "SVG", Bytes = new FileInfo(temporaryPath).Length };
            if (output.Width <= 0 || output.Height <= 0)
                throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_DDEEE4BC00DE"));
            cancellationToken.ThrowIfCancellationRequested();
            var finalPath = MoveToUniquePath(temporaryPath, directory, source.Path, options.Action, extension);
            Report(progress, 1);
            return new ImageJobResult(finalPath, output.Bytes, output.Width, output.Height, output.Format, source.Bytes);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_78FA12B4FBB7"), exception);
        }
        catch (IOException exception)
        {
            throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_32285DD256F7"), exception);
        }
        finally
        {
            // The directory is generated here and contains only this job's temporary output.
            try { if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Transforms a static image into a new lossless PNG; the input is never replaced.</summary>
    public async Task<ImageJobResult> TransformAsync(string path, ImageTransformOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (SelectManaged(path, new ImageJobOptions { Action = ImageAction.ImageTransform }) is { } managed)
            return await managed.TransformAsync(path, options, cancellationToken).ConfigureAwait(false);
        if (options.RotationDegrees is not (0 or 90 or 180 or 270))
            throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_A2C99C7C1D50"));
        var source = await InspectAsync(path, cancellationToken).ConfigureAwait(false);
        if (source.Format == "SVG" && await SvgImagePolicy.ReadContainerAsync(source.Path, cancellationToken).ConfigureAwait(false) is { } png)
            return await WithEmbeddedPngAsync(png, source.Path, async staged =>
            {
                var result = await TransformAsync(staged, options with { OutputDirectory = options.OutputDirectory ?? System.IO.Path.GetDirectoryName(source.Path) }, cancellationToken).ConfigureAwait(false);
                return result with { SourceBytes = source.Bytes };
            }, cancellationToken).ConfigureAwait(false);
        var directory = string.IsNullOrWhiteSpace(options.OutputDirectory)
            ? System.IO.Path.GetDirectoryName(source.Path)!
            : System.IO.Path.GetFullPath(options.OutputDirectory);
        if (!Directory.Exists(directory))
            throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_45C406D632F2"));

        var temporaryDirectory = System.IO.Path.Combine(directory, ".orbit-" + Guid.NewGuid().ToString("N"));
        var temporaryPath = System.IO.Path.Combine(temporaryDirectory, "result.png");
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            var arguments = ResourceArguments();
            arguments.AddRange(["-define", "filename:literal=true", InputCoder(source.Format) + ":" + source.Path, "-auto-orient"]);
            if (options.RotationDegrees != 0)
                arguments.AddRange(["-rotate", options.RotationDegrees.ToString(CultureInfo.InvariantCulture)]);
            if (options.FlipHorizontal) arguments.Add("-flop");
            if (options.FlipVertical) arguments.Add("-flip");
            arguments.AddRange(["+repage", "+profile", "exif", "-orient", "TopLeft"]);
            // Retain PNG sample depth/profile. Other inputs use the same ICC-aware sRGB path as PNG conversion.
            if (source.Format != "PNG") AddSrgbArguments(arguments);
            arguments.AddRange(["-define", "png:color-type=6", "-define", "png:compression-level=9",
                "-define", "png:compression-filter=5", "PNG:" + temporaryPath]);
            await RunAsync(arguments, _operationTimeout, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length == 0)
                throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_E77B0E76F7E5"));
            var output = await IdentifyAsync(temporaryPath, "PNG", cancellationToken).ConfigureAwait(false);
            var swapAxes = options.RotationDegrees is 90 or 270;
            if (output.Format != "PNG" || output.Width != (swapAxes ? source.Height : source.Width)
                || output.Height != (swapAxes ? source.Width : source.Height))
                throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_C1450BA3B030"));
            cancellationToken.ThrowIfCancellationRequested();
            var finalPath = MoveToUniquePath(temporaryPath, directory, source.Path, "transformed", "png");
            return new ImageJobResult(finalPath, output.Bytes, output.Width, output.Height, output.Format, source.Bytes);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_78FA12B4FBB7"), exception);
        }
        catch (IOException exception)
        {
            throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_32285DD256F7"), exception);
        }
        finally
        {
            try { if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<ImageMetadata> IdentifyAsync(string path, string coder, CancellationToken cancellationToken)
    {
        var args = new List<string> { "identify" };
        args.AddRange(ResourceArguments());
        args.AddRange(["-ping", "-define", "filename:literal=true", "-format", "%m|%w|%h|%n|%[orientation]\\n", coder + ":" + path]);
        var raw = await RunAsync(args, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        var pingLines = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (coder.Equals("WEBP", StringComparison.OrdinalIgnoreCase) && pingLines.Length == 1
            && pingLines[0].Split('|') is { Length: 5 } pingParts && pingParts[3] == "1")
        {
            // The WebP ping reader can omit EXIF orientation. A resource-limited actual
            // decode reports the orientation that -auto-orient will use during resizing.
            var decoded = new List<string>(args); decoded.Remove("-ping");
            raw = await RunAsync(decoded, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
        var lines = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_E22C28325BD9"));
        var parts = lines[0].Split('|');
        if (parts.Length != 5 || !Formats.Contains(parts[0]) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var height) ||
            !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var frames) || width <= 0 || height <= 0)
            throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_BFF4E2D5481C"));
        if (frames != 1 || lines.Length != 1)
            throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_A59D7481F8F3"));
        if (parts[4] is "LeftTop" or "RightTop" or "RightBottom" or "LeftBottom")
            (width, height) = (height, width);
        return new ImageMetadata(path, NormalizeFormat(parts[0]), width, height, frames, new FileInfo(path).Length);
    }

    private static void ValidateOptions(ImageJobOptions options, ImageMetadata source)
    {
        if (options.Action is not (ImageAction.Jpg or ImageAction.WebP or ImageAction.Avif or ImageAction.Png or ImageAction.Bmp or ImageAction.Tiff or ImageAction.Gif or ImageAction.Ico or ImageAction.Svg or ImageAction.Crop or ImageAction.Resize or ImageAction.Compress))
            throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_214476C19692"));
        if (options.Quality is < 1 or > 100) throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_7E60EBA10B44"));
        if (options.ResizeFilter is not ("Auto" or "Lanczos" or "Mitchell" or "Point"))
            throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_D5DBCDA296C4"));
        if (!Regex.IsMatch(options.JpegBackgroundColor ?? "", @"\A#[0-9A-Fa-f]{6}\z", RegexOptions.CultureInvariant))
            throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_066D51270AB8"));
        if (options.Action == ImageAction.Resize)
        {
            if (options.ResizePercent is { } percent)
            {
                if (!double.IsFinite(percent) || percent is < 1 or > 400)
                    throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_138DB4BEA298"));
                if (options.Width is not null || options.Height is not null)
                    throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_E5FD265858AE"));
                if (!options.MaintainAspectRatio)
                    throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_36760B0F8423"));
                _ = PercentDimensions(source, options);
            }
            else if (options.Width is null && options.Height is null)
                throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_459E5909BD19"));
            if (options.Width is <= 0 or > 100000 || options.Height is <= 0 or > 100000)
                throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_0A7EA44757F9"));
            if (!options.MaintainAspectRatio && (options.Width is null || options.Height is null))
                throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_6ADA79264802"));
        }
        if (options.Action == ImageAction.Crop)
        {
            if (options.CropX < 0 || options.CropY < 0 || options.CropWidth is null or <= 0 || options.CropHeight is null or <= 0)
                throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_0EB81C2994A0"));
            if ((long)options.CropX + options.CropWidth > source.Width || (long)options.CropY + options.CropHeight > source.Height)
                throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_759710D89CE6"));
        }
    }

    private static void AddOperationArguments(List<string> args, ImageMetadata source, ImageJobOptions options)
    {
        if (options.Action == ImageAction.Crop)
            args.AddRange(["-crop", FormattableString.Invariant($"{options.CropWidth}x{options.CropHeight}+{options.CropX}+{options.CropY}"), "+repage"]);
        if (options.Action != ImageAction.Resize) return;
        if (options.ResizeFilter != "Auto") args.AddRange(["-filter", options.ResizeFilter]);
        if (options.ResizePercent is not null)
        {
            var (width, height) = PercentDimensions(source, options);
            // Resolve both displayed axes ourselves so midpoint rounding is deterministic.
            args.AddRange(["-resize", FormattableString.Invariant($"{width}x{height}!")]);
            return;
        }
        if (options.MaintainAspectRatio)
        {
            var geometry = options.Width?.ToString(CultureInfo.InvariantCulture) + "x" + options.Height?.ToString(CultureInfo.InvariantCulture);
            args.AddRange(["-resize", geometry + (options.NoUpscale ? ">" : "")]);
        }
        else
        {
            var width = options.NoUpscale ? Math.Min(options.Width!.Value, source.Width) : options.Width!.Value;
            var height = options.NoUpscale ? Math.Min(options.Height!.Value, source.Height) : options.Height!.Value;
            args.AddRange(["-resize", FormattableString.Invariant($"{width}x{height}!")]);
        }
    }

    private void AddEncodingArguments(List<string> args, string sourceFormat, string format, ImageJobOptions options)
    {
        // PNG optimization is lossless; the quality slider applies only to lossy formats.
        // Retain color profiles: stripping ICC data can change appearance even when PNG pixels are unchanged.
        if (format is "WEBP" or "AVIF" or "BMP" or "GIF" or "ICO" || format == "PNG" && sourceFormat != "PNG") AddSrgbArguments(args);
        switch (format)
        {
            case "JPEG":
                args.AddRange(["-background", options.JpegBackgroundColor.ToUpperInvariant(), "-alpha", "remove", "-alpha", "off", "-quality", options.Quality.ToString(CultureInfo.InvariantCulture), "-interlace", "Plane"]);
                break;
            case "WEBP":
                args.AddRange(["-quality", options.Quality.ToString(CultureInfo.InvariantCulture), "-define", "webp:method=6"]);
                break;
            case "AVIF":
                args.AddRange(["-quality", options.Quality.ToString(CultureInfo.InvariantCulture), "-define", "heic:speed=6"]);
                break;
            case "PNG":
                args.AddRange(["-define", "png:compression-level=9", "-define", "png:compression-filter=5"]);
                break;
            case "TIFF":
                // Indexed PNG can report a 1/2/4-bit palette depth. Reusing that
                // sample depth for TIFF quantizes its actual RGB palette values.
                args.AddRange(["-type", "TrueColorAlpha", "-depth", "16", "-compress", "Zip"]);
                break;
            case "BMP":
                args.AddRange(["-background", options.JpegBackgroundColor.ToUpperInvariant(), "-alpha", "remove", "-alpha", "off", "-type", "TrueColor"]);
                break;
            case "GIF":
                args.AddRange(["-alpha", "on", "-channel", "A", "-threshold", "50%", "+channel", "-colors", "256"]);
                break;
            case "ICO":
                args.AddRange(["-resize", "256x256>", "-background", "none", "-gravity", "Center", "-extent", "256x256", "-depth", "8"]);
                break;
        }
    }

    private static (int Width, int Height) PercentDimensions(ImageMetadata source, ImageJobOptions options)
    {
        var scale = options.ResizePercent!.Value / 100d;
        if (options.NoUpscale) scale = Math.Min(scale, 1);
        var width = Math.Max(1, Math.Round(source.Width * scale, MidpointRounding.AwayFromZero));
        var height = Math.Max(1, Math.Round(source.Height * scale, MidpointRounding.AwayFromZero));
        // Validate as doubles before casting: a large source times400% can exceed Int32.
        if (!double.IsFinite(width) || !double.IsFinite(height) || width > 100000 || height > 100000)
            throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_F608142C2D2A"));
        return ((int)width, (int)height);
    }

    private static async Task<FileStream> OpenReadLeaseAsync(string path, CancellationToken token)
    {
        var opening = Task.Run(() =>
        {
            token.ThrowIfCancellationRequested(); var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            try { token.ThrowIfCancellationRequested(); return input; } catch { input.Dispose(); throw; }
        }, CancellationToken.None);
        try { return await opening.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or IOException or UnauthorizedAccessException)
        {
            _ = opening.ContinueWith(task => { if (task.IsCompletedSuccessfully) task.Result.Dispose(); else _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            if (exception is OperationCanceledException) throw;
            throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_8FBBDCC0863A"), exception);
        }
    }

    private void AddSrgbArguments(List<string> args)
    {
        var profile = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ResolveExecutable())!, "sRGB.icc");
        if (File.Exists(profile))
        {
            // A real ICC transform is required before dropping a wide-gamut/CMYK profile.
            args.AddRange(["-profile", profile, "-colorspace", "sRGB", "+profile", "icc,icm"]);
        }
        else
        {
            args.AddRange(["-colorspace", "sRGB"]);
        }
    }

    private static string TargetFormat(string sourceFormat, ImageJobOptions options) => options.Action switch
    {
        ImageAction.Jpg => "JPEG",
        ImageAction.WebP => "WEBP",
        ImageAction.Avif => "AVIF",
        ImageAction.Png => "PNG", ImageAction.Bmp => "BMP", ImageAction.Tiff => "TIFF",
        ImageAction.Gif => "GIF", ImageAction.Ico => "ICO", ImageAction.Svg => "SVG",
        ImageAction.Compress when !string.IsNullOrWhiteSpace(options.CompressionFormat) => NormalizeCompressionFormat(options.CompressionFormat),
        _ => sourceFormat == "SVG" ? "PNG" : sourceFormat
    };

    private static string NormalizeCompressionFormat(string format) => format.Trim().TrimStart('.').ToLowerInvariant() switch
    {
        "jpg" or "jpeg" => "JPEG",
        "webp" => "WEBP",
        "avif" => "AVIF",
        "png" => "PNG",
        _ => throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_2378D54BDE48"))
    };

    private static string NormalizeFormat(string format) => format.ToUpperInvariant() switch
    { "BMP2" or "BMP3" => "BMP", "MSVG" => "SVG", var value => value };

    private static string InputCoder(string format) => format == "SVG" ? "MSVG" : format;

    private static string FormatExtension(string format) => format switch
    { "JPEG" => "jpg", "TIFF" => "tif", _ => format.ToLowerInvariant() };

    private static string MoveToUniquePath(string temporaryPath, string directory, string sourcePath, ImageAction action, string extension)
    {
        var label = action switch { ImageAction.Crop => "crop", ImageAction.Resize => "resize", ImageAction.Compress => "compressed", _ => "converted" };
        return MoveToUniquePath(temporaryPath, directory, sourcePath, label, extension);
    }

    private static string MoveToUniquePath(string temporaryPath, string directory, string sourcePath, string label, string extension)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(sourcePath) + "_" + label;
        for (var index = 0; index < 10000; index++)
        {
            var candidate = System.IO.Path.Combine(directory, stem + (index == 0 ? "" : "_" + index.ToString(CultureInfo.InvariantCulture)) + "." + extension);
            if (string.Equals(candidate, sourcePath, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Move(temporaryPath, candidate, overwrite: false); return candidate; }
            catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate)) { }
        }
        throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_5B1ACE5A02E9"));
    }

    private static string ValidateInputPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_633AD966C74E"));
        string fullPath;
        try { fullPath = System.IO.Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_9A3B4A4374C9"), exception); }
        if (!Extensions.Contains(System.IO.Path.GetExtension(fullPath)))
            throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_235C614F284C"));
        if (!File.Exists(fullPath)) throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_9D96D1202997"));
        return fullPath;
    }

    private static async Task<string> DetectSignatureAsync(string path, CancellationToken cancellationToken)
    {
        if (System.IO.Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase))
        {
            await SvgImagePolicy.ValidateAsync(path, cancellationToken).ConfigureAwait(false);
            return "MSVG";
        }
        try
        {
            var header = new byte[256];
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            var length = await file.ReadAsync(header, cancellationToken).ConfigureAwait(false);
            if (length >= 3 && header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff) return "JPEG";
            if (length >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            {
                // The PNG coder can silently show only frame zero of APNG. Inspect chunk headers before decoding.
                file.Position = 8;
                var chunkHeader = new byte[8];
                while (file.Position + 8 <= file.Length)
                {
                    await file.ReadExactlyAsync(chunkHeader, cancellationToken).ConfigureAwait(false);
                    var chunkLength = (long)chunkHeader[0] << 24 | (long)chunkHeader[1] << 16 | (long)chunkHeader[2] << 8 | chunkHeader[3];
                    var chunkType = Encoding.ASCII.GetString(chunkHeader, 4, 4);
                    if (chunkType == "acTL")
                        throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_A59D7481F8F3"));
                    if (chunkType is "IDAT" or "IEND" || file.Position + chunkLength + 4 > file.Length) break;
                    file.Seek(chunkLength + 4, SeekOrigin.Current);
                }
                return "PNG";
            }
            if (length >= 12 && Encoding.ASCII.GetString(header, 0, 4) == "RIFF" && Encoding.ASCII.GetString(header, 8, 4) == "WEBP") return "WEBP";
            if (length >= 2 && header[0] == 'B' && header[1] == 'M') return "BMP";
            if (length >= 6 && (Encoding.ASCII.GetString(header, 0, 6) is "GIF87a" or "GIF89a")) return "GIF";
            if (length >= 6 && header[0] == 0 && header[1] == 0 && header[2] == 1 && header[3] == 0) return "ICO";
            if (length >= 4 && (header.AsSpan(0, 4).SequenceEqual(new byte[] { 73, 73, 42, 0 }) || header.AsSpan(0, 4).SequenceEqual(new byte[] { 77, 77, 0, 42 }) ||
                header.AsSpan(0, 4).SequenceEqual(new byte[] { 73, 73, 43, 0 }) || header.AsSpan(0, 4).SequenceEqual(new byte[] { 77, 77, 0, 43 }))) return "TIFF";
            if (length >= 16 && Encoding.ASCII.GetString(header, 4, 4) == "ftyp")
            {
                var boxLength = (long)header[0] << 24 | (long)header[1] << 16 | (long)header[2] << 8 | header[3];
                var boxEnd = (int)Math.Min(length, boxLength);
                var hasAvif = false;
                for (var index = 8; index + 4 <= boxEnd; index += 4)
                {
                    if (index == 12) continue;
                    var brand = Encoding.ASCII.GetString(header, index, 4);
                    if (brand == "avis") throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_EAD7A192FB83"));
                    if (brand == "avif") hasAvif = true;
                }
                if (hasAvif) return "AVIF";
            }
        }
        catch (UnauthorizedAccessException exception) { throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_A24DAB0FCCDC"), exception); }
        catch (IOException exception) { throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_CD732EAFFE9D"), exception); }
        throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_E82F47D233FF"));
    }

    private string ResolveExecutable()
    {
        var candidates = new[]
        {
            _configuredExecutable,
            System.IO.Path.Combine(AppContext.BaseDirectory, "tools", "imagemagick", "magick.exe"),
            Environment.GetEnvironmentVariable("ARCLO_MAGICK"),
            Environment.GetEnvironmentVariable("ORBIT_MAGICK"),
            @"C:\Program Files\ImageMagick-7.1.2-Q16-HDRI\magick.exe"
        };
        foreach (var candidate in candidates)
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate)) return System.IO.Path.GetFullPath(candidate);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator))
        {
            try
            {
                var candidate = System.IO.Path.Combine(directory.Trim('"'), OperatingSystem.IsWindows() ? "magick.exe" : "magick");
                if (File.Exists(candidate)) return System.IO.Path.GetFullPath(candidate);
            }
            catch (ArgumentException) { }
        }
        throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_2BA6821B02A2"));
    }

    private IImageCodecFallback? SelectManaged(string path, ImageJobOptions? options = null)
    {
        var managed = ManagedFallback;
        if (managed is null || !managed.CanRead(path) || options is not null && !managed.CanExecute(options)) return null;
        if (PreferManagedCodecs) return managed;
        try { _ = ResolveExecutable(); return null; }
        catch (ImageProcessingException) { return managed; }
    }

    private static async Task<T> WithEmbeddedPngAsync<T>(byte[] png, string source, Func<string, Task<T>> action, CancellationToken cancellationToken)
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Arclo-svg-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, System.IO.Path.GetFileNameWithoutExtension(source) + ".png");
            await File.WriteAllBytesAsync(path, png, cancellationToken).ConfigureAwait(false);
            return await action(path).ConfigureAwait(false);
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static List<string> ResourceArguments() => ["-limit", "thread", "2", "-limit", "memory", "512MiB", "-limit", "map", "0", "-limit", "disk", "0"];

    private async Task<string> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        => Encoding.UTF8.GetString(await RunBytesAsync(arguments, timeout, cancellationToken, 65536).ConfigureAwait(false));

    private async Task<byte[]> RunBytesAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken, int maxOutputBytes)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executablePath = ResolveExecutable();
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        // Relocated Windows builds must use their own configuration and coder DLLs, rather than registry paths.
        var executableDirectory = System.IO.Path.GetDirectoryName(executablePath)!;
        var coderDirectory = System.IO.Path.Combine(executableDirectory, "modules", "coders");
        if (Directory.Exists(coderDirectory))
        {
            startInfo.Environment["MAGICK_HOME"] = executableDirectory;
            startInfo.Environment["MAGICK_CONFIGURE_PATH"] = executableDirectory;
            startInfo.Environment["MAGICK_CODER_MODULE_PATH"] = coderDirectory;
            startInfo.Environment["MAGICK_FILTER_MODULE_PATH"] = System.IO.Path.Combine(executableDirectory, "modules", "filters");
        }
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_4E4DE7AFB676"));
        }
        catch (System.ComponentModel.Win32Exception exception)
        { throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_A5EA6B1D9D07"), exception); }
        var stdoutTask = ReadLimitedBytesAsync(process.StandardOutput.BaseStream, maxOutputBytes);
        var stderrTask = ReadLimitedAsync(process.StandardError);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_222CB0E5D108"));
        }
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            var detail = stderr.Length > 900 ? stderr[..900] : stderr;
            throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_FE1BFDB17C51") + detail.Trim());
        }
        if (stdout.Length > maxOutputBytes) throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_1030A1075DAA"));
        return stdout;
    }

    private static async Task<byte[]> ReadLimitedBytesAsync(Stream stream, int maximumBytes)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer).ConfigureAwait(false)) != 0)
            if (output.Length <= maximumBytes) output.Write(buffer, 0, (int)Math.Min(count, maximumBytes + 1L - output.Length));
        return output.ToArray();
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
            if (output.Length < 65536) output.Append(buffer, 0, Math.Min(count, 65536 - output.Length));
        return output.ToString();
    }

    private static void Report(IProgress<double>? progress, double value) => progress?.Report(value);
}
