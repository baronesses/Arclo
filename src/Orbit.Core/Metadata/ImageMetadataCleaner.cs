using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Orbit.Core.Metadata;

/// <summary>Removes supported EXIF/XMP/IPTC/profiles/comments after orienting and converting pixels to sRGB.
/// JPEG is re-encoded at quality95 and AVIF at quality100. This is not forensic anonymization.</summary>
public sealed class ImageMetadataCleaner
{
    public const long MaximumSourceBytes = 256L * 1024 * 1024;
    public const long MaximumSourcePixels = 40_000_000;
    public const int MaximumSourceDimension = 16000;
    private readonly ImageMagickEngine _images;
    private readonly TimeSpan _operationTimeout;

    public ImageMetadataCleaner(string? imageMagickExecutable = null, TimeSpan? operationTimeout = null)
    {
        _images = new ImageMagickEngine(imageMagickExecutable);
        _operationTimeout = operationTimeout ?? TimeSpan.FromMinutes(3);
        if (_operationTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
    }

    public string ExecutablePath => _images.ExecutablePath;

    public async Task<ImageJobResult> CleanAsync(string path, ImageMetadataCleanOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        var sourcePath = NormalizePath(path);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_operationTimeout);
        var token = timeout.Token;
        string? temporary = null;
        string? directory = null;
        try
        {
            // Keep the same file protected from writes/replacement during all inspection and decoding.
            await using var input = await OpenInputAsync(sourcePath, token).ConfigureAwait(false);
            var source = await _images.InspectAsync(sourcePath, token).ConfigureAwait(false);
            if (source.Frames != 1 || source.Width > MaximumSourceDimension || source.Height > MaximumSourceDimension
                || (long)source.Width * source.Height > MaximumSourcePixels)
                throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_F83E470F78BE"));
            var extension = Path.GetExtension(sourcePath);
            if (!MatchingExtension(source.Format, extension))
                throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_003AA1A1D80B"));
            var details = await SourceDetailsAsync(source, token).ConfigureAwait(false);
            // Some WebP decoders omit EXIF orientation during -ping. Once bounded metadata
            // has passed preflight, inspect the decoded frame before calculating displayed size.
            source = source with { Width = details.Width, Height = details.Height };
            if (source.Format is "PNG" or "TIFF" && details.Depth > 16)
                throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_F68D33CE3878"));
            directory = string.IsNullOrWhiteSpace(options.OutputDirectory) ? Path.GetDirectoryName(sourcePath)!
                : Path.GetFullPath(options.OutputDirectory);
            if (!Directory.Exists(directory)) throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_45C406D632F2"));
            temporary = Path.Combine(directory, ".orbit-metadata-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            var resultPath = Path.Combine(temporary, "result" + extension);
            var arguments = ResourceArguments();
            arguments.AddRange(["-define", "filename:literal=true", source.Format + ":" + sourcePath, "-auto-orient"]);
            var srgb = Path.Combine(Path.GetDirectoryName(ExecutablePath)!, "sRGB.icc");
            bool hasIcc = details.Profiles.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)
                .Any(profile => profile.Equals("icc", StringComparison.OrdinalIgnoreCase) || profile.Equals("icm", StringComparison.OrdinalIgnoreCase));
            if (File.Exists(srgb)) arguments.AddRange(["-profile", srgb]);
            else if (hasIcc) throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_25C39ABE4139"));
            arguments.AddRange(["-colorspace", "sRGB", "-strip", "+set", "*", "-orient", "TopLeft"]);
            switch (source.Format)
            {
                case "JPEG": arguments.AddRange(["-quality", "95", "-sampling-factor", "4:4:4"]);break;
                case "WEBP": arguments.AddRange(["-quality", "100", "-define", "webp:lossless=true", "-define", "webp:method=6"]);break;
                case "AVIF": arguments.AddRange(["-quality", "100", "-define", "heic:chroma=444", "-define", "heic:speed=6"]);break;
                case "PNG": arguments.AddRange(["-depth", (details.Depth > 8 ? 16 : 8).ToString(CultureInfo.InvariantCulture),
                    "-define", "png:exclude-chunks=all", "-define", "png:compression-level=9"]);break;
                case "TIFF": arguments.AddRange(["-depth", (details.Depth > 8 ? 16 : 8).ToString(CultureInfo.InvariantCulture), "-compress", "Zip"]);break;
            }
            arguments.Add(source.Format + ":" + resultPath);
            await RunAsync(arguments, token).ConfigureAwait(false);
            var output = await _images.InspectAsync(resultPath, token).ConfigureAwait(false);
            if (output.Format != source.Format || output.Width != source.Width || output.Height != source.Height || output.Bytes is <= 0 or > MaximumSourceBytes)
                throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_0D6D6C82E2F1"));
            await VerifyRemovedMetadataAsync(output, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var published = Commit(resultPath, directory, sourcePath, extension, token);
            return new ImageJobResult(published, output.Bytes, output.Width, output.Height, output.Format, source.Bytes);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_5EC708117665")); }
        catch (UnauthorizedAccessException exception)
        { throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_24AD77C9E1CF"), exception); }
        catch (IOException exception)
        { throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_601F9C404BDD"), exception); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or System.Security.SecurityException)
        { throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_2872F70F56EC"), exception); }
        finally { await CleanupAsync(temporary, directory).ConfigureAwait(false); }
    }

    private async Task<(string Profiles, int Depth, int Width, int Height)> SourceDetailsAsync(ImageMetadata source, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "identify" };arguments.AddRange(ResourceArguments());
        arguments.AddRange(["-define", "filename:literal=true", "-format", "%[profiles]|%z|%w|%h|%[orientation]", source.Format + ":" + source.Path]);
        var details = await RunAsync(arguments, cancellationToken).ConfigureAwait(false);
        var parts = details.Trim().Split('|');
        if (parts.Length != 5 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var depth) || depth is < 1 or > 64
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var width) || width is < 1 or > MaximumSourceDimension
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var height) || height is < 1 or > MaximumSourceDimension
            || (long)width * height > MaximumSourcePixels)
            throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_303FBB336FAA"));
        if (parts[4] is "LeftTop" or "RightTop" or "RightBottom" or "LeftBottom") (width, height) = (height, width);
        return (parts[0].Trim(), depth, width, height);
    }

    private async Task VerifyRemovedMetadataAsync(ImageMetadata output, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "identify" };arguments.AddRange(ResourceArguments());
        arguments.AddRange(["-ping", "-define", "filename:literal=true", "-format", "%[profiles]|%c|%[EXIF:*]|%[XMP:*]|%[IPTC:*]|%[orientation]", output.Format + ":" + output.Path]);
        var parts = (await RunAsync(arguments, cancellationToken).ConfigureAwait(false)).Split('|');
        if (parts.Length != 6 || parts.Take(5).Any(part => !string.IsNullOrWhiteSpace(part)) || parts[5].Trim() is not ("TopLeft" or "Undefined"))
            throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_94172BB15523"));
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_B655ABD185BD"));
        try
        {
            var normalized = Path.GetFullPath(path);
            if (!new[] { ".png", ".jpg", ".jpeg", ".webp", ".avif", ".bmp", ".tif", ".tiff" }.Contains(Path.GetExtension(normalized), StringComparer.OrdinalIgnoreCase))
                throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_44A160C3E05E"));
            return normalized;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_7F96D02D2135"), exception); }
    }

    private static bool MatchingExtension(string format, string extension) => format switch
    {
        "JPEG" => extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase),
        "TIFF" => extension.Equals(".tif", StringComparison.OrdinalIgnoreCase) || extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase),
        _ => extension.Equals("." + format, StringComparison.OrdinalIgnoreCase)
    };

    private static async Task<FileStream> OpenInputAsync(string path, CancellationToken cancellationToken)
    {
        var pending = Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stream.Length is <= 0 or > MaximumSourceBytes) throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_3369E24FE14F"));
                return stream;
            }
            catch { stream.Dispose();throw; }
        }, CancellationToken.None);
        try { return await pending.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            _ = pending.ContinueWith(completed => { if (completed.IsCompletedSuccessfully) completed.Result.Dispose();else _ = completed.Exception; },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            if (exception is OperationCanceledException) throw;
            throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_DC6E15DF0947"), exception);
        }
    }

    private static List<string> ResourceArguments() => ["-limit", "thread", "2", "-limit", "memory", "512MiB", "-limit", "map", "0", "-limit", "disk", "0"];

    private async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executable = ExecutablePath;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        var home = Path.GetDirectoryName(executable)!;
        if (Directory.Exists(Path.Combine(home, "modules", "coders")))
        {
            start.Environment["MAGICK_HOME"] = home;start.Environment["MAGICK_CONFIGURE_PATH"] = home;
            start.Environment["MAGICK_CODER_MODULE_PATH"] = Path.Combine(home, "modules", "coders");
            start.Environment["MAGICK_FILTER_MODULE_PATH"] = Path.Combine(home, "modules", "filters");
        }
        start.Environment["LC_ALL"] = "C";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { if (!process.Start()) throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_7AA623ED72A7")); }
        catch (System.ComponentModel.Win32Exception exception) { throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_3F6A4C5351D1"), exception); }
        var stdout = ReadBoundedAsync(process.StandardOutput);
        var stderr = ReadBoundedAsync(process.StandardError);
        try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (TimeoutException) { }
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (TimeoutException) { }
            throw;
        }
        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_B28059C7DD72") + error[..Math.Min(error.Length, 500)].Trim());
        if (output.Length > 65536) throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_C0C997241960"));
        return output;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var result = new StringBuilder();var buffer = new char[4096];int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
            if (result.Length <= 65536) result.Append(buffer, 0, Math.Min(count, 65537 - result.Length));
        return result.ToString();
    }

    private static string Commit(string temporaryPath, string directory, string sourcePath, string extension, CancellationToken cancellationToken)
    {
        var stem = Path.GetFileNameWithoutExtension(sourcePath) + "_metadata_cleaned";
        for (int index = 0; index < 10000; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = Path.Combine(directory, stem + (index == 0 ? "" : "_" + index.ToString(CultureInfo.InvariantCulture)) + extension);
            if (string.Equals(candidate, sourcePath, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Move(temporaryPath, candidate, overwrite: false);return candidate; }
            catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate)) { }
        }
        throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_06B72C6392FF"));
    }

    private static async Task CleanupAsync(string? temporary, string? directory)
    {
        if (temporary is null || directory is null || !temporary.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(temporary).StartsWith(".orbit-metadata-", StringComparison.Ordinal)) return;
        for (var attempt = 0; attempt < 11; attempt++)
        {
            try { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);return; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            if (attempt < 10) await Task.Delay(Math.Min(300, 25 << Math.Min(attempt, 4))).ConfigureAwait(false);
        }
    }
}
