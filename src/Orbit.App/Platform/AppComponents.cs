using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Orbit.App.Platform;

internal sealed record ComponentLink(string Id, string Name, string Purpose, string Url, string Executable, string Variable);

internal static class AppComponents
{
    public static ComponentLink[] Catalog => new ComponentLink[] {
        new("imagemagick", "ImageMagick", global::Orbit.Core.Localization.L.Get("Text_A3E82D4C647A"), "https://imagemagick.org/script/download.php#windows", "magick.exe", "ORBIT_MAGICK"),
        new("ffmpeg", "FFmpeg", global::Orbit.Core.Localization.L.Get("Text_645FE04C829D"), "https://www.gyan.dev/ffmpeg/builds/", "ffmpeg.exe", "ORBIT_FFMPEG"),
        new("poppler", "Poppler", global::Orbit.Core.Localization.L.Get("Text_C52E79BC7B28"), "https://github.com/oschwartz10612/poppler-windows/releases", "pdfinfo.exe", "ORBIT_POPPLER"),
        new("qpdf", "qpdf", global::Orbit.Core.Localization.L.Get("Text_4B79C39D60D6"), "https://github.com/qpdf/qpdf/releases", "qpdf.exe", "ORBIT_QPDF"),
        new("tesseract", "Tesseract OCR", global::Orbit.Core.Localization.L.Get("Text_0CCF3060BA78"), "https://github.com/UB-Mannheim/tesseract/wiki", "tesseract.exe", "ORBIT_TESSERACT"),
        new("ytdlp", "yt-dlp", global::Orbit.Core.Localization.L.Get("Text_27315576EDF3"), "https://github.com/yt-dlp/yt-dlp/releases", "yt-dlp.exe", "ORBIT_YTDLP"),
        new("deno", "Deno", global::Orbit.Core.Localization.L.Get("Text_84DC3DBF5E73"), "https://github.com/denoland/deno/releases", "deno.exe", "ORBIT_DENO")
    };
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Arclo");
    public static string? BundledDirectory { get; private set; }
    private static string ConfigPath => Path.Combine(DataDirectory, "components.json");
    private static Dictionary<string, string> _selected = new(StringComparer.Ordinal);

    public static void Initialize(bool diagnostic)
    {
        if (!diagnostic)
        {
            Directory.CreateDirectory(DataDirectory);
            MigrateSettings();
            try { _selected = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ConfigPath)) ?? new(); }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { _selected = new(); }
            foreach (var component in Catalog)
                if (_selected.TryGetValue(component.Id, out var path) && File.Exists(path)) Apply(component, path);
        }
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("Arclo.BundledTools.zip");
        if (resource is null) return;
        using var memory = new MemoryStream(); resource.CopyTo(memory);
        string hash = Convert.ToHexString(SHA256.HashData(memory.ToArray())).ToLowerInvariant(); memory.Position = 0;
        string basePath = diagnostic ? Path.Combine(AppContext.BaseDirectory, "diagnostic-components") : Path.Combine(DataDirectory, "components");
        Directory.CreateDirectory(basePath);
        BundledDirectory = Path.Combine(basePath, "bundled-" + hash[..16]);
        using var zip = new ZipArchive(memory, ZipArchiveMode.Read);
        var manifestEntry = zip.GetEntry("manifest.json") ?? throw new IOException(global::Orbit.Core.Localization.L.Get("Text_5A1F60B029AB"));
        using var manifestStream = manifestEntry.Open(); using var manifest = JsonDocument.Parse(manifestStream);
        if (manifest.RootElement.GetProperty("schema").GetInt32() != 1) throw new IOException(global::Orbit.Core.Localization.L.Get("Text_7825EC0C732B"));
        Directory.CreateDirectory(BundledDirectory);
        if ((File.GetAttributes(BundledDirectory) & FileAttributes.ReparsePoint) != 0) throw new IOException(global::Orbit.Core.Localization.L.Get("Text_3FCE2D434F65"));
        foreach (var item in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            string relative = item.GetProperty("path").GetString()!;
            if (relative.Contains('\\') || relative.Contains(':') || relative.StartsWith('/') || relative.Split('/').Any(p => p is "" or "." or "..")) throw new IOException(global::Orbit.Core.Localization.L.Get("Text_074D0749C2D8"));
            string target = Path.GetFullPath(Path.Combine(BundledDirectory, relative));
            if (!target.StartsWith(BundledDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException(global::Orbit.Core.Localization.L.Get("Text_605B04C56BBD"));
            string expected = item.GetProperty("sha256").GetString()!; long bytes = item.GetProperty("bytes").GetInt64();
            if (bytes < 0 || bytes > 64 * 1024 * 1024) throw new IOException(global::Orbit.Core.Localization.L.Get("Text_0412BA8186E0"));
            bool Match() { if (!File.Exists(target) || new FileInfo(target).Length != bytes) return false; using var input = File.OpenRead(target); return Convert.ToHexString(SHA256.HashData(input)).Equals(expected, StringComparison.OrdinalIgnoreCase); }
            if (Match()) continue;
            var entry = zip.GetEntry(relative) ?? throw new IOException(global::Orbit.Core.Localization.L.Get("Text_52EC9E2071C8"));
            if (entry.Length != bytes) throw new IOException(global::Orbit.Core.Localization.L.Get("Text_123C2C942948"));
            string parent = Path.GetDirectoryName(target)!; Directory.CreateDirectory(parent);
            for (string? current = parent; current is not null && current.StartsWith(BundledDirectory, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException(global::Orbit.Core.Localization.L.Get("Text_F8A7268A3161"));
            string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var input = entry.Open()) using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
                using (var verified = File.OpenRead(temporary))
                    if (!Convert.ToHexString(SHA256.HashData(verified)).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException(global::Orbit.Core.Localization.L.Get("Text_362D8CF6D860"));
                try { File.Move(temporary, target, true); } catch (IOException) when (Match()) { }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        Environment.SetEnvironmentVariable("ORBIT_7ZIP", Path.Combine(BundledDirectory, "7zip", "7zr.exe"));
    }
    public static string? Selected(ComponentLink component) => _selected.TryGetValue(component.Id, out var value) && File.Exists(value) ? value : null;
    public static void Select(ComponentLink component, string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path) || !Path.GetFileName(path).Equals(component.Executable, StringComparison.OrdinalIgnoreCase)) throw new IOException(global::Orbit.Core.Localization.L.Get("Text_2877D37E284D") + component.Executable + ".");
        var selected = new Dictionary<string, string>(_selected) { [component.Id] = path };
        Directory.CreateDirectory(DataDirectory); string temporary = ConfigPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(selected)); File.Move(temporary, ConfigPath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        _selected = selected; Apply(component, path);
    }
    private static void Apply(ComponentLink component, string path) => Environment.SetEnvironmentVariable(component.Variable,
        component.Id is "poppler" or "tesseract" ? Path.GetDirectoryName(path) : path);
    private static void MigrateSettings()
    {
        string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrbitToolkit");
        foreach (string name in new[] { "settings.json", "history.json", "presets.json", "chain-presets.json", "personalization.json", "favorites.json" })
        {
            string source = Path.Combine(legacy, name), target = Path.Combine(DataDirectory, name);
            if (!File.Exists(source) || File.Exists(target)) continue;
            try { File.Copy(source, target, false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
