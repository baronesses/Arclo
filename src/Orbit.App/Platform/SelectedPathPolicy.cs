using System;
using System.Collections.Generic;
using System.IO;

namespace Orbit.App.Platform;

public sealed record SelectedPathsResult(IReadOnlyList<string> Paths, string? Error, int RejectedCount)
{
    public bool Success => Paths.Count > 0;
}

/// <summary>Filesystem-only input normalization shared by Explorer, arguments and explicit file/folder pickers.</summary>
public static class SelectedPathPolicy
{
    public static SelectedPathsResult NormalizeExisting(IEnumerable<string?> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var accepted = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rejected = 0;
        string? firstError = null;
        foreach (var path in paths)
        {
            if (TryNormalizeExisting(path, out var normalizedPath, out var error))
            {
                if (seen.Add(normalizedPath)) accepted.Add(normalizedPath);
            }
            else
            {
                rejected++;
                firstError ??= error;
            }
        }

        return new SelectedPathsResult(accepted.AsReadOnly(), accepted.Count > 0 ? null
            : firstError ?? global::Orbit.Core.Localization.L.Get("Text_574D325172DC"), rejected);
    }

    public static bool TryNormalizeExisting(string? path, out string normalizedPath, out string? error)
    {
        normalizedPath = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = global::Orbit.Core.Localization.L.Get("Text_7971E7FAAEEE");
            return false;
        }

        // Reject shell aliases and device namespaces syntactically, before any filesystem query.
        if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || path.StartsWith("::", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\??\", StringComparison.Ordinal) || path.Contains('\0'))
        {
            error = global::Orbit.Core.Localization.L.Get("Text_7225D5E1D2BC");
            return false;
        }

        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                error = global::Orbit.Core.Localization.L.Get("Text_2DC96A011ADA");
                return false;
            }

            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (root is null || string.Equals(Path.TrimEndingDirectorySeparator(fullPath), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            {
                error = global::Orbit.Core.Localization.L.Get("Text_47E9252B26D6");
                return false;
            }

            // A normal drive prefix contains the only permitted colon. Named alternate streams are not selections.
            if (fullPath.AsSpan(Math.Min(2, fullPath.Length)).Contains(':'))
            {
                error = global::Orbit.Core.Localization.L.Get("Text_3C30D1D3B6BF");
                return false;
            }

            fullPath = Path.TrimEndingDirectorySeparator(fullPath);
            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                error = global::Orbit.Core.Localization.L.Get("Text_042621C0768A");
                return false;
            }

            normalizedPath = fullPath;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            error = global::Orbit.Core.Localization.L.Get("Text_C785E3952CFC");
            return false;
        }
    }
}
