using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace Orbit.App.Platform;

public sealed record RadialClipboardPayload(IReadOnlyList<string> Paths, string? Url, string? Error);

/// <summary>Read only after an explicit paste command. Parse permits isolated injected DataObjects.</summary>
public sealed class RadialClipboardReader
{
    public RadialClipboardPayload Read()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            return Failure(global::Orbit.Core.Localization.L.Get("Text_2141D34F0E47"));
        try { return Parse(Clipboard.GetDataObject()); }
        catch (Exception exception) when (exception is ExternalException or InvalidOperationException or System.Threading.ThreadStateException)
        { return Failure(global::Orbit.Core.Localization.L.Get("Text_EBCFDDA5E17A")); }
    }

    public static RadialClipboardPayload Parse(IDataObject? data)
    {
        if (data is null) return Failure(global::Orbit.Core.Localization.L.Get("Text_2674228DF157"));
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop, false))
            {
                if (data.GetData(DataFormats.FileDrop, false) is not string[] paths || paths.Length is 0 or > 100)
                    return Failure(global::Orbit.Core.Localization.L.Get("Text_155DFB8885A5"));
                var result = new List<string>();var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in paths)
                {
                    if (!Existing(path, out var normalized)) return Failure(global::Orbit.Core.Localization.L.Get("Text_17D21488DF09"));
                    if (seen.Add(normalized)) result.Add(normalized);
                }
                return new(result.AsReadOnly(), null, null);
            }
            var value = data.GetDataPresent(DataFormats.UnicodeText, false) ? data.GetData(DataFormats.UnicodeText, false)
                : data.GetDataPresent(DataFormats.Text, false) ? data.GetData(DataFormats.Text, false) : null;
            if (value is not string text || string.IsNullOrWhiteSpace(text)) return Failure(global::Orbit.Core.Localization.L.Get("Text_81E704B276E1"));
            text = text.Trim();
            if (text.Length > 8192 || text.Any(char.IsControl)) return Failure(global::Orbit.Core.Localization.L.Get("Text_E90EA93B700F"));
            if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                if (uri.Host.Length == 0 || uri.UserInfo.Length != 0 || text.Any(char.IsWhiteSpace) || uri.AbsoluteUri.Length > 8192)
                    return Failure(global::Orbit.Core.Localization.L.Get("Text_82BD6C1BB4DE"));
                return new(Array.Empty<string>(), uri.AbsoluteUri, null);
            }
            if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') text = text[1..^1];
            if (Existing(text, out var file)) return new(Array.AsReadOnly(new[] { file }), null, null);
            return Failure(global::Orbit.Core.Localization.L.Get("Text_50B36744719A"));
        }
        catch (Exception exception) when (exception is ExternalException or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        { return Failure(global::Orbit.Core.Localization.L.Get("Text_F73032EAB730")); }
    }

    private static bool Existing(string? path, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(path) || path.Length > 8192 || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path)
            || path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal)
            || full.Skip(Path.GetPathRoot(full)?.Length ?? 0).Contains(':')) return false;
        var root = Path.GetPathRoot(full);
        if (root is null || new DriveInfo(root).DriveType == DriveType.Network) return false;
        if (!File.Exists(full) && !Directory.Exists(full)) return false;
        normalized = Path.TrimEndingDirectorySeparator(full);return true;
    }
    private static RadialClipboardPayload Failure(string message) => new(Array.Empty<string>(), null, message);
}
