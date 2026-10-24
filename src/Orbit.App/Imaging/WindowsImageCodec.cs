using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;
using Orbit.Core;
using ImageMetadata = Orbit.Core.ImageMetadata;

namespace Orbit.App.Codecs;

/// <summary>Built-in Windows codecs keep everyday image work independent of native tool downloads.</summary>
internal sealed class WindowsImageCodec : IImageCodecFallback
{
    public bool CanRead(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension != ".svg") return extension is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff" or ".gif" or ".ico";
        try { using var input = new StreamReader(path); char[] start = new char[4096]; int count = input.Read(start, 0, start.Length); return new string(start, 0, count).Contains("data:image/png;base64,", StringComparison.Ordinal); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    public bool CanExecute(ImageJobOptions options) => options.Action is ImageAction.Jpg or ImageAction.Png or ImageAction.Bmp or ImageAction.Tiff or ImageAction.Gif or ImageAction.Ico or ImageAction.Svg or ImageAction.Crop or ImageAction.Resize or ImageAction.ImageTransform
        || options.Action == ImageAction.Compress && (options.CompressionFormat is null or "jpg" or "jpeg" or "png");

    public Task<ImageMetadata> InspectAsync(string path, CancellationToken ct = default) => OnSta(() =>
    {
        using var stream = Open(path); var image = Decode(stream, path, out string format);
        return new ImageMetadata(Path.GetFullPath(path), format, image.PixelWidth, image.PixelHeight, 1, stream.Length);
    }, ct);

    public Task<byte[]> CreatePreviewAsync(string path, int maxSize = 640, CancellationToken ct = default) => OnSta(() =>
    {
        if (maxSize is < 1 or > 2048) throw new ArgumentOutOfRangeException(nameof(maxSize));
        using var source = Open(path); var bitmap = Decode(source, path, out _);
        double scale = Math.Min(1, (double)maxSize / Math.Max(bitmap.PixelWidth, bitmap.PixelHeight));
        if (scale < 1) bitmap = Scaled(bitmap, Math.Max(1, (int)Math.Round(bitmap.PixelWidth * scale)), Math.Max(1, (int)Math.Round(bitmap.PixelHeight * scale)));
        return Encode(bitmap, "PNG", 85);
    }, ct);

    public Task<ImageJobResult> TransformAsync(string path, ImageTransformOptions options, CancellationToken ct = default) =>
        ExecuteAsync(path, new ImageJobOptions { Action = ImageAction.ImageTransform, RotationDegrees = options.RotationDegrees,
            FlipHorizontal = options.FlipHorizontal, FlipVertical = options.FlipVertical, OutputDirectory = options.OutputDirectory }, null, ct);

    public Task<ImageJobResult> ExecuteAsync(string path, ImageJobOptions options, IProgress<double>? progress = null, CancellationToken ct = default) => OnSta(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (!CanExecute(options)) throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_F169235D69B5"));
        if (options.Quality is < 1 or > 100) throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_7E60EBA10B44"));
        using var source = Open(path); var bitmap = Decode(source, path, out string sourceFormat); long sourceBytes = source.Length;
        progress?.Report(.1);
        if (options.Action == ImageAction.Crop)
        {
            int width = options.CropWidth ?? bitmap.PixelWidth, height = options.CropHeight ?? bitmap.PixelHeight;
            if (options.CropX < 0 || options.CropY < 0 || width < 1 || height < 1 || (long)options.CropX + width > bitmap.PixelWidth || (long)options.CropY + height > bitmap.PixelHeight)
                throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_B6E4C5C826CD"));
            bitmap = new CroppedBitmap(bitmap, new Int32Rect(options.CropX, options.CropY, width, height)); bitmap.Freeze();
        }
        if (options.Action == ImageAction.Resize)
        {
            int width, height;
            if (options.ResizePercent is { } percent)
            {
                if (!double.IsFinite(percent) || percent <= 0 || percent > 1000) throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_368149F78D8F"));
                if (options.NoUpscale) percent = Math.Min(100, percent);
                width = Math.Max(1, (int)Math.Round(bitmap.PixelWidth * percent / 100)); height = Math.Max(1, (int)Math.Round(bitmap.PixelHeight * percent / 100));
            }
            else
            {
                width = options.Width ?? 0; height = options.Height ?? 0;
                if (width <= 0 && height <= 0) throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_90C72FBE0BCD"));
                if (options.MaintainAspectRatio)
                {
                    double scale = width > 0 && height > 0 ? Math.Min((double)width / bitmap.PixelWidth, (double)height / bitmap.PixelHeight)
                        : width > 0 ? (double)width / bitmap.PixelWidth : (double)height / bitmap.PixelHeight;
                    if (options.NoUpscale) scale = Math.Min(1, scale);
                    width = Math.Max(1, (int)Math.Round(bitmap.PixelWidth * scale)); height = Math.Max(1, (int)Math.Round(bitmap.PixelHeight * scale));
                }
                else { if (width <= 0) width = bitmap.PixelWidth; if (height <= 0) height = bitmap.PixelHeight;
                    if (options.NoUpscale) { width = Math.Min(width, bitmap.PixelWidth); height = Math.Min(height, bitmap.PixelHeight); } }
            }
            ValidateSize(width, height); bitmap = Scaled(bitmap, width, height);
        }
        if (options.Action == ImageAction.ImageTransform)
        {
            if (options.RotationDegrees is not (0 or 90 or 180 or 270)) throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_0C163151E8A2"));
            var transforms = new TransformGroup();
            transforms.Children.Add(new RotateTransform(options.RotationDegrees));
            transforms.Children.Add(new ScaleTransform(options.FlipHorizontal ? -1 : 1, options.FlipVertical ? -1 : 1));
            bitmap = new TransformedBitmap(bitmap, transforms); bitmap.Freeze();
        }
        string format = options.Action switch
        {
            ImageAction.Jpg => "JPEG", ImageAction.Png => "PNG", ImageAction.Bmp => "BMP", ImageAction.Tiff => "TIFF",
            ImageAction.Gif => "GIF", ImageAction.Ico => "ICO", ImageAction.Svg => "SVG",
            ImageAction.Compress when options.CompressionFormat is "jpg" or "jpeg" => "JPEG",
            ImageAction.Compress when options.CompressionFormat == "png" => "PNG", _ => sourceFormat == "JPEG" ? "JPEG" : "PNG"
        };
        if (format is "JPEG" or "BMP") bitmap = Render(bitmap, bitmap.PixelWidth, bitmap.PixelHeight, (Color)ColorConverter.ConvertFromString(options.JpegBackgroundColor));
        if (format == "ICO") bitmap = Render(bitmap, 256, 256, Colors.Transparent, true);
        ct.ThrowIfCancellationRequested(); progress?.Report(.6);
        byte[] bytes = format switch
        {
            "ICO" => Icon(Encode(bitmap, "PNG", options.Quality), bitmap.PixelWidth, bitmap.PixelHeight),
            "SVG" => Encoding.UTF8.GetBytes($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{bitmap.PixelWidth}\" height=\"{bitmap.PixelHeight}\" viewBox=\"0 0 {bitmap.PixelWidth} {bitmap.PixelHeight}\"><image width=\"{bitmap.PixelWidth}\" height=\"{bitmap.PixelHeight}\" href=\"data:image/png;base64,{Convert.ToBase64String(Encode(bitmap, "PNG", options.Quality))}\"/></svg>"),
            _ => Encode(bitmap, format, options.Quality)
        };
        string directory = string.IsNullOrWhiteSpace(options.OutputDirectory) ? Path.GetDirectoryName(Path.GetFullPath(path))! : Path.GetFullPath(options.OutputDirectory);
        if (!Directory.Exists(directory)) throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_F5D6DF7FEC9A"));
        string extension = format switch { "JPEG" => "jpg", "TIFF" => "tiff", _ => format.ToLowerInvariant() };
        string temporary = Path.Combine(directory, ".arclo-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes); ct.ThrowIfCancellationRequested();
            string stem = Path.GetFileNameWithoutExtension(path) + "_" + (options.Action is ImageAction.Crop ? "cropped" : options.Action is ImageAction.Resize ? "resized" : "converted");
            for (int number = 0; ; number++)
            {
                string output = Path.Combine(directory, stem + (number == 0 ? "" : "_" + number) + "." + extension);
                try { File.Move(temporary, output, false); progress?.Report(1); return new ImageJobResult(output, bytes.LongLength, bitmap.PixelWidth, bitmap.PixelHeight, format, sourceBytes); }
                catch (IOException) when (File.Exists(output)) { ct.ThrowIfCancellationRequested(); }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }, ct);

    private static FileStream Open(string path)
    {
        var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > 128 * 1024 * 1024) { stream.Dispose(); throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_F97D0227FCBF")); }
        return stream;
    }
    private static BitmapSource Decode(Stream stream, string path, out string format)
    {
        if (Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 48 * 1024 * 1024, CloseInput = false });
            var document = XDocument.Load(reader); XNamespace ns = "http://www.w3.org/2000/svg";
            var root = document.Root; var images = root?.Elements(ns + "image").ToArray();
            if (root?.Name != ns + "svg" || images?.Length != 1 || root.Elements().Count() != 1 || root.Descendants().Any(e => e.Name != ns + "image")
                || root.DescendantsAndSelf().Attributes().Any(a => !a.IsNamespaceDeclaration && a.Name.LocalName is not ("width" or "height" or "viewBox" or "href")))
                throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_29B981303F1D"));
            string? data = images[0].Attribute("href")?.Value;
            const string prefix = "data:image/png;base64,";
            if (data is null || !data.StartsWith(prefix, StringComparison.Ordinal) || data.Length > 48 * 1024 * 1024) throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_77A64C95ECD8"));
            using var png = new MemoryStream(Convert.FromBase64String(data[prefix.Length..]));
            var bitmap = Decode(png, "image.png", out _); format = "SVG"; return bitmap;
        }
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1) throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_1BE5EE5FC647"));
        BitmapSource frame = decoder.Frames[0]; ValidateSize(frame.PixelWidth, frame.PixelHeight);
        format = decoder switch { JpegBitmapDecoder => "JPEG", PngBitmapDecoder => "PNG", BmpBitmapDecoder => "BMP", TiffBitmapDecoder => "TIFF", GifBitmapDecoder => "GIF", IconBitmapDecoder => "ICO", _ => throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_659783110960")) };
        if (format == "JPEG" && frame.Metadata is BitmapMetadata metadata)
        {
            try
            {
                int orientation = Convert.ToInt32(metadata.GetQuery("/app1/ifd/{ushort=274}") ?? 1);
                Transform? transform = orientation switch
                {
                    2 => new ScaleTransform(-1, 1), 3 => new RotateTransform(180), 4 => new ScaleTransform(1, -1),
                    5 => new TransformGroup { Children = { new RotateTransform(90), new ScaleTransform(-1, 1) } },
                    6 => new RotateTransform(90), 7 => new TransformGroup { Children = { new RotateTransform(90), new ScaleTransform(1, -1) } },
                    8 => new RotateTransform(270), _ => null
                };
                if (transform is not null) frame = new TransformedBitmap(frame, transform);
            }
            catch (NotSupportedException) { }
        }
        frame.Freeze(); return frame;
    }
    private static void ValidateSize(int width, int height) { if (width < 1 || height < 1 || width > 16000 || height > 16000 || (long)width * height > 64_000_000) throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_6F2FF2D447FA")); }
    private static BitmapSource Scaled(BitmapSource source, int width, int height) => Render(source, width, height, Colors.Transparent);
    private static BitmapSource Render(BitmapSource source, int width, int height, Color background, bool contain = false)
    {
        var visual = new DrawingVisual(); using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, width, height));
            double scale = Math.Min((double)width / source.PixelWidth, (double)height / source.PixelHeight);
            var bounds = contain ? new Rect((width - source.PixelWidth * scale) / 2, (height - source.PixelHeight * scale) / 2, source.PixelWidth * scale, source.PixelHeight * scale) : new Rect(0, 0, width, height);
            drawing.DrawImage(source, bounds);
        }
        var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); result.Render(visual); result.Freeze(); return result;
    }
    private static byte[] Encode(BitmapSource image, string format, int quality)
    {
        BitmapEncoder encoder = format switch { "JPEG" => new JpegBitmapEncoder { QualityLevel = quality }, "BMP" => new BmpBitmapEncoder(), "TIFF" => new TiffBitmapEncoder { Compression = TiffCompressOption.Zip }, "GIF" => new GifBitmapEncoder(), _ => new PngBitmapEncoder() };
        encoder.Frames.Add(BitmapFrame.Create(image)); using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
    }
    private static byte[] Icon(byte[] png, int width, int height)
    {
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1); writer.Write((byte)(width == 256 ? 0 : width)); writer.Write((byte)(height == 256 ? 0 : height));
        writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(png.Length); writer.Write(22); writer.Write(png); return output.ToArray();
    }
    private static Task<T> OnSta<T>(Func<T> work, CancellationToken ct)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { ct.ThrowIfCancellationRequested(); result.TrySetResult(work()); } catch (OperationCanceledException) { result.TrySetCanceled(ct); } catch (Exception ex) { result.TrySetException(ex); } }) { IsBackground = true, Name = "Arclo image codec" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return result.Task;
    }
}
