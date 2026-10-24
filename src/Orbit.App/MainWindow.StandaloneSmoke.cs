using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Orbit.App.Controls;
using Orbit.App.Platform;
using Orbit.Core;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private async Task RunStandaloneSmokeAsync()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "standalone-smoke");
        Directory.CreateDirectory(directory); string run = Path.Combine(directory, "run-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(run);
        var checks = new List<string>();
        void Require(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks.Add(label); }
        try
        {
            File.Delete(Path.Combine(directory, "error.txt")); File.Delete(Path.Combine(directory, "passed.json"));
            Require(IsDiagnosticRun && _bridge is null && !ProductivityIoAllowed && !PersonalizationIoAllowed, "Standalone mode is isolated from settings, global keys, clipboard and tray");
            Require(ImageMagickEngine.PreferManagedCodecs && ImageMagickEngine.ManagedFallback is Codecs.WindowsImageCodec, "Windows codecs are preferred without native image executables");
            Require(!SystemsDescendants<System.Windows.Controls.Image>(_header).Any() && SystemsDescendants<TextBlock>(_header).Any(t => t.Text == "Arclo"), "Header contains plain Arclo text and no graphical logo");
            var pixels = new byte[48 * 32 * 4];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 40; pixels[i + 1] = 90; pixels[i + 2] = 160; pixels[i + 3] = (byte)((i / 4) % 48 < 24 ? 255 : 0); }
            var bitmap = BitmapSource.Create(48, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 48 * 4);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string source = Path.Combine(run, "sample.png"); using (var stream = File.Create(source)) encoder.Save(stream);
            string sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
            foreach (var format in new[] { ImageAction.Jpg, ImageAction.Png, ImageAction.Bmp, ImageAction.Tiff, ImageAction.Gif, ImageAction.Ico, ImageAction.Svg })
            {
                var result = await _engine.ExecuteAsync(new[]{source}, new ImageJobOptions { Action = format, OutputDirectory = run });
                Require(File.Exists(result.OutputPath) && result.Bytes > 0 && result.OutputPath != source, "Built-in export creates a separate " + format + " file");
                var metadata = await _engine.InspectAsync(result.OutputPath);
                Require(metadata.Kind == ToolkitKind.Image && metadata.Width > 0 && metadata.Height > 0, "Built-in result reopens: " + format);
            }
            var preview = await _engine.CreatePreviewAsync(source);
            Require(preview is {Length:>20}, "Built-in PNG preview works");
            Require(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))) == sourceHash, "Conversions preserve original bytes");
            var payload = new DataObject(); payload.SetData(DataFormats.FileDrop, new[] { source });
            await ApplyClipboardPayloadAsync(RadialClipboardReader.Parse(payload));
            await RunRadialCommandAsync(RadialCommand.Convert);
            var formats = _radial.Children.OfType<SectorButton>().Where(b => b.CommandKind == RadialCommand.None).Select(b => b.Action).ToArray();
            Require(formats.SequenceEqual(new[] { ImageAction.Jpg, ImageAction.Png, ImageAction.Svg }), "Current-file conversion prioritizes JPG, PNG and SVG");
            await Dispatcher.InvokeAsync(UpdateLayout); Capture(Path.Combine(directory, "01-formats.png"));
            _radialConversionMenu = false; RenderRadialFile(); await Dispatcher.InvokeAsync(UpdateLayout); Capture(Path.Combine(directory, "02-circle.png"));
            SetCompactMode(false); ShowComponents(); await Dispatcher.InvokeAsync(UpdateLayout); Capture(Path.Combine(directory, "03-components.png"));
            Require(SystemsDescendants<Button>(_panelContent).Count(b => Equals(b.Content, "Скачать…")) == AppComponents.Catalog.Length, "Each optional component has an official download link");
            Require(AppComponents.Catalog.All(c => c.Url.StartsWith("https://", StringComparison.Ordinal)), "Dependency links use HTTPS");
            ClosePanel(); ClearSelection(); await Dispatcher.InvokeAsync(UpdateLayout); Capture(Path.Combine(directory, "04-main.png"));
            Require(AppComponents.BundledDirectory is not null && File.Exists(Path.Combine(AppComponents.BundledDirectory, "7zip", "7zr.exe")), "Single EXE contains and extracts the public-domain 7zr tool");
            Require(File.Exists(Path.Combine(AppComponents.BundledDirectory!, "licenses", "INCLUDED-COMPONENTS.txt")), "Embedded license notices are present");
            string folder = Path.Combine(run, "files"); Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "sample.txt"), "Arclo single EXE");
            var archive = await _engine.ExecuteAsync(new[]{folder}, new ImageJobOptions { Action = ImageAction.Archive7z, OutputDirectory = run });
            Require(File.Exists(archive.OutputPath) && archive.Bytes > 0, "Extracted built-in 7zr creates a real archive");
            File.WriteAllText(Path.Combine(directory, "passed.json"), JsonSerializer.Serialize(new { checks, run, singleFile = string.IsNullOrEmpty(typeof(App).Assembly.Location), bundled = AppComponents.BundledDirectory }));
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(directory, "error.txt"), ex.ToString()); Environment.ExitCode = 1; }
        finally { Quit(); }
    }
}
