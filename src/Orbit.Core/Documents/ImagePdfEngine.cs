using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Orbit.Core.Documents;

/// <summary>Local image pages encoded as JPEG XObjects in a new PDF. No PDF delegate is required.</summary>
public sealed class ImagePdfEngine
{
    public const int MaximumPages = 100;
    public const long MaximumSingleSourceBytes = 256L * 1024 * 1024;
    public const long MaximumTotalSourceBytes = 512L * 1024 * 1024;
    public const long MaximumSingleSourcePixels = 40_000_000;
    public const long MaximumTotalSourcePixels = 500_000_000;
    public const int MaximumSourceDimension = 16000;
    public const long MaximumOutputBytes = 512L * 1024 * 1024;
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    { ".png", ".jpg", ".jpeg", ".webp", ".avif", ".bmp", ".tif", ".tiff" };
    private readonly ImageMagickEngine _images;

    public ImagePdfEngine(string? imageMagickExecutable = null) => _images = new ImageMagickEngine(imageMagickExecutable);

    public async Task<ImageJobResult> CreateAsync(IReadOnlyList<string> paths, ImagePdfOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths); ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (paths.Count is < 1 or > MaximumPages) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Format("Text_D7A4F632D67F",(object?)(MaximumPages)));
        if (!Enum.IsDefined(options.PageSize)) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_639B2141C004"));
        if (!double.IsFinite(options.MarginMillimeters) || options.MarginMillimeters is < 0 or > 40)
            throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_A524B93767B8"));
        if (options.JpegQuality is < 1 or > 100) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_C40DBAFBE6C7"));
        var requested = paths.ToArray();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        InputPlan? plan = null; string? temporary = null; string? directory = null;
        var pendingPlan = Task.Run(() => OpenPlan(requested, timeout.Token), CancellationToken.None);
        try
        {
            try { plan = await pendingPlan.WaitAsync(timeout.Token).ConfigureAwait(false); }
            catch
            {
                _ = pendingPlan.ContinueWith(task => { if (task.IsCompletedSuccessfully) task.Result.Dispose(); else _ = task.Exception; },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
            var metadata = new Dictionary<string, ImageMetadata>(StringComparer.OrdinalIgnoreCase);
            var pages = new List<ImageMetadata>(); long totalPixels = 0;
            foreach (var input in plan.Pages)
            {
                timeout.Token.ThrowIfCancellationRequested(); RecheckSource(input.Path, timeout.Token);
                if (!metadata.TryGetValue(input.Path, out var image))
                {
                    image = await _images.InspectAsync(input.Path, timeout.Token).ConfigureAwait(false);
                    if (image.Frames != 1) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_4AD7B43B91F9"));
                    if (image.Width > MaximumSourceDimension || image.Height > MaximumSourceDimension || (long)image.Width * image.Height > MaximumSingleSourcePixels)
                        throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_BAB66DC565A9"));
                    metadata.Add(input.Path, image);
                }
                totalPixels += (long)image.Width * image.Height;
                if (totalPixels > MaximumTotalSourcePixels) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_1B9C7E68FED1"));
                pages.Add(image);
            }
            directory = DocumentFiles.OutputDirectory(plan.Pages[0].Path, options.OutputDirectory);
            temporary = DocumentFiles.CreateTemporaryDirectory(directory);
            var prepared = Path.Combine(temporary, "document.pdf");
            await WritePdfAsync(prepared, temporary, pages, options, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            var bytes = new FileInfo(prepared).Length;
            if (bytes is <= 0 or > MaximumOutputBytes) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_92F6C7208456"));
            var name = Path.GetFileNameWithoutExtension(plan.Pages[0].Path) + "_images.pdf";
            var output = DocumentFiles.CommitFile(prepared, directory, name, timeout.Token);
            return new ImageJobResult(output, bytes, 0, 0, "PDF", plan.TotalBytes);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_58D8131835E1")); }
        catch (ImageProcessingException exception) { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_AA635D1FB25E") + exception.Message, exception); }
        catch (FileNotFoundException exception) { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_B7D07003E249"), exception); }
        catch (DirectoryNotFoundException exception) { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_F07DE094FE9B"), exception); }
        catch (UnauthorizedAccessException exception) { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_AF6E0E3440EE"), exception); }
        catch (IOException exception) { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_9AADEB8E0E6B"), exception); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_FA4D8ABCAB22"), exception); }
        finally
        {
            plan?.Dispose();
            if (directory is not null)
            {
                for (var attempt = 0; attempt < 11; attempt++)
                {
                    DocumentFiles.CleanupTemporaryDirectory(temporary, directory);
                    if (temporary is null || !Directory.Exists(temporary)) break;
                    if (attempt < 10) await Task.Delay(Math.Min(300, 25 << Math.Min(attempt, 4))).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task WritePdfAsync(string path, string temporary, IReadOnlyList<ImageMetadata> pages, ImagePdfOptions options, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        var offsets = new long[3 + pages.Count * 3];
        async Task Text(string value) => await file.WriteAsync(Encoding.ASCII.GetBytes(value), ct).ConfigureAwait(false);
        async Task Object(int id, string value) { offsets[id] = file.Position; await Text($"{id} 0 obj\n{value}\nendobj\n").ConfigureAwait(false); }
        await Text("%PDF-1.4\n% Arclo image pages\n").ConfigureAwait(false);
        await Object(1, "<< /Type /Catalog /Pages 2 0 R >>").ConfigureAwait(false);
        await Object(2, $"<< /Type /Pages /Count {pages.Count} /Kids [{string.Join(' ', Enumerable.Range(0, pages.Count).Select(index => $"{3 + index * 3} 0 R"))}] >>").ConfigureAwait(false);
        for (var index = 0; index < pages.Count; index++)
        {
            ct.ThrowIfCancellationRequested(); var image = pages[index]; RecheckSource(image.Path, ct);
            var normalized = Path.Combine(temporary, "page.png");
            // Existing JPG conversion intentionally preserves source profiles. Normalize explicitly
            // first so PDF's DeviceRGB always receives sRGB, including CMYK/wide-gamut input profiles.
            await NormalizeAsync(image, normalized, ct).ConfigureAwait(false);
            var jpeg = await _images.ExecuteAsync(normalized, new ImageJobOptions { Action = ImageAction.Jpg, Quality = options.JpegQuality, OutputDirectory = temporary }, cancellationToken: ct).ConfigureAwait(false);
            File.Delete(normalized); RecheckSource(image.Path, ct);
            var jpegInfo = JpegInfo(jpeg.OutputPath);
            // WebP may expose EXIF orientation only on full decode, after identify -ping.
            // Normalization already decodes and orients the pixels: its actual JPEG geometry
            // is authoritative for layout. Only a 90-degree swap may differ from bounded metadata.
            var sameGeometry = jpegInfo.Width == image.Width && jpegInfo.Height == image.Height;
            var rotatedGeometry = jpegInfo.Width == image.Height && jpegInfo.Height == image.Width;
            if ((!sameGeometry && !rotatedGeometry) || jpegInfo.Components is not (1 or 3))
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_AF1692CB99C4"));
            var pixelWidth = jpegInfo.Width; var pixelHeight = jpegInfo.Height;
            if (file.Position + jpeg.Bytes + 65536 > MaximumOutputBytes) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_92F6C7208456"));
            // PDF 1.4 pages are bounded at 14400 points (200 inches) on either side.
            var fitScale = Math.Min(1d, 14400d / Math.Max(pixelWidth, pixelHeight));
            var pageWidth = options.PageSize == ImagePdfPageSize.FitImage ? pixelWidth * fitScale : (options.PageSize == ImagePdfPageSize.A4 ? 210d : 297d) * 72 / 25.4;
            var pageHeight = options.PageSize == ImagePdfPageSize.FitImage ? pixelHeight * fitScale : (options.PageSize == ImagePdfPageSize.A4 ? 297d : 210d) * 72 / 25.4;
            var margin = options.PageSize == ImagePdfPageSize.FitImage ? 0 : options.MarginMillimeters * 72 / 25.4;
            var scale = Math.Min((pageWidth - 2 * margin) / pixelWidth, (pageHeight - 2 * margin) / pixelHeight);
            var width = pixelWidth * scale; var height = pixelHeight * scale; var x = (pageWidth - width) / 2; var y = (pageHeight - height) / 2;
            var pageId = 3 + index * 3; var imageId = pageId + 1; var contentId = pageId + 2;
            await Object(pageId, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Number(pageWidth)} {Number(pageHeight)}] /Resources << /XObject << /Im0 {imageId} 0 R >> >> /Contents {contentId} 0 R >>").ConfigureAwait(false);
            offsets[imageId] = file.Position;
            await Text($"{imageId} 0 obj\n<< /Type /XObject /Subtype /Image /Width {pixelWidth} /Height {pixelHeight} /ColorSpace /{(jpegInfo.Components == 1 ? "DeviceGray" : "DeviceRGB")} /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Bytes} >>\nstream\n").ConfigureAwait(false);
            await using (var input = new FileStream(jpeg.OutputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous))
                await input.CopyToAsync(file, ct).ConfigureAwait(false);
            await Text("\nendstream\nendobj\n").ConfigureAwait(false); File.Delete(jpeg.OutputPath);
            var content = $"1 1 1 rg 0 0 {Number(pageWidth)} {Number(pageHeight)} re f\nq {Number(width)} 0 0 {Number(height)} {Number(x)} {Number(y)} cm /Im0 Do Q\n";
            var contentBytes = Encoding.ASCII.GetByteCount(content); offsets[contentId] = file.Position;
            await Text($"{contentId} 0 obj\n<< /Length {contentBytes} >>\nstream\n{content}endstream\nendobj\n").ConfigureAwait(false);
        }
        var xref = file.Position;
        await Text($"xref\n0 {offsets.Length}\n0000000000 65535 f \n").ConfigureAwait(false);
        foreach (var offset in offsets.Skip(1)) await Text(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n").ConfigureAwait(false);
        await Text($"trailer\n<< /Size {offsets.Length} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n").ConfigureAwait(false);
        await file.FlushAsync(ct).ConfigureAwait(false); file.Flush(flushToDisk: true);
    }

    private async Task NormalizeAsync(ImageMetadata image, string output, CancellationToken ct)
    {
        var executable = _images.ExecutablePath; var directory = Path.GetDirectoryName(executable)!;
        var arguments = new List<string> { "-limit", "thread", "2", "-limit", "memory", "512MiB", "-limit", "map", "0", "-limit", "disk", "0",
            "-define", "filename:literal=true", image.Format + ":" + image.Path, "-auto-orient" };
        var profile = Path.Combine(directory, "sRGB.icc");
        if (File.Exists(profile)) arguments.AddRange(new[] { "-profile", profile });
        arguments.AddRange(new[] { "-colorspace", "sRGB", "+profile", "icc,icm", "-background", "white", "-alpha", "remove", "-alpha", "off", "-type", "TrueColor", "-depth", "8", "+repage", "+profile", "exif", "-orient", "TopLeft", "-define", "png:color-type=2", "PNG:" + output });
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Directory.Exists(Path.Combine(directory, "modules", "coders")))
        {
            start.Environment["MAGICK_HOME"] = directory; start.Environment["MAGICK_CONFIGURE_PATH"] = directory;
            start.Environment["MAGICK_CODER_MODULE_PATH"] = Path.Combine(directory, "modules", "coders"); start.Environment["MAGICK_FILTER_MODULE_PATH"] = Path.Combine(directory, "modules", "filters");
        }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { if (!process.Start()) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_663BD322CA92")); }
        catch (System.ComponentModel.Win32Exception exception) { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_577EA1F72872"), exception); }
        var stdout = ReadBoundedAsync(process.StandardOutput); var stderr = ReadBoundedAsync(process.StandardError);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); var error = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_E23D52E40CA4") + error.Trim()[..Math.Min(error.Trim().Length, 900)]);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_FBD280219969")); }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
                try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch (InvalidOperationException) { }
            }
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var output = new StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0) if (output.Length < 65536) output.Append(buffer, 0, Math.Min(count, 65536 - output.Length));
        return output.ToString();
    }

    private static InputPlan OpenPlan(IReadOnlyList<string> paths, CancellationToken ct)
    {
        var plan = new InputPlan(); var unique = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var input in paths)
            {
                ct.ThrowIfCancellationRequested(); ArgumentException.ThrowIfNullOrWhiteSpace(input);
                var path = Path.GetFullPath(input).Replace('/', '\\');
                if (!Extensions.Contains(Path.GetExtension(path)) || path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)
                    || path.IndexOf(':', path.Length > 1 && path[1] == ':' ? 2 : 0) >= 0)
                    throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_B187612A645F"));
                RecheckSource(path, ct);
                if (!unique.TryGetValue(path, out var stream))
                {
                    stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
                    plan.Handles.Add(stream); unique.Add(path, stream); RecheckSource(path, ct);
                    if ((File.GetAttributes(stream.SafeFileHandle) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                        throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_C4CAEACD153B"));
                }
                if (stream.Length is <= 0 or > MaximumSingleSourceBytes) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_847CB9EBFD65"));
                plan.TotalBytes += stream.Length;
                if (plan.TotalBytes > MaximumTotalSourceBytes) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_924A9E1DFDA4"));
                plan.Pages.Add(new SourcePage(path, stream.Length));
            }
            return plan;
        }
        catch { plan.Dispose(); throw; }
    }

    private static void RecheckSource(string path, CancellationToken ct)
    {
        string? current = path; var depth = 0;
        while (!string.IsNullOrEmpty(current))
        {
            ct.ThrowIfCancellationRequested(); if (++depth > 256) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_2A5F99D98B4F"));
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_6FB67F5DEA5A"));
            if (depth == 1 && (attributes & FileAttributes.Directory) != 0) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_A5B6FE90D5DD"));
            current = Path.GetDirectoryName(current);
        }
    }

    private static (int Width, int Height, int Components) JpegInfo(string path)
    {
        using var input = File.OpenRead(path);
        if (input.ReadByte() != 0xff || input.ReadByte() != 0xd8) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_AB5AD7C2FF58"));
        while (input.Position < input.Length && input.Position < 1024 * 1024)
        {
            if (input.ReadByte() != 0xff) continue; int marker; do { marker = input.ReadByte(); } while (marker == 0xff);
            if (marker is < 0 or 0xda or 0xd9) break;
            if (marker is 0x01 or >= 0xd0 and <= 0xd8) continue;
            var high = input.ReadByte(); var low = input.ReadByte(); if (high < 0 || low < 0) break; var length = high * 256 + low;
            if (length < 2 || input.Position + length - 2 > input.Length) break;
            if (marker is 0xc0 or 0xc1 or 0xc2)
            {
                var precision = input.ReadByte(); var height = input.ReadByte() * 256 + input.ReadByte(); var width = input.ReadByte() * 256 + input.ReadByte(); var components = input.ReadByte();
                if (precision == 8 && width > 0 && height > 0) return (width, height, components); break;
            }
            input.Seek(length - 2, SeekOrigin.Current);
        }
        throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_4FE60EE657BD"));
    }

    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    private sealed record SourcePage(string Path, long Bytes);
    private sealed class InputPlan : IDisposable
    {
        public List<SourcePage> Pages { get; } = new(); public List<FileStream> Handles { get; } = new(); public long TotalBytes { get; set; }
        public void Dispose() { foreach (var handle in Handles) handle.Dispose(); }
    }
}
