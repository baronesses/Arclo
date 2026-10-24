namespace Orbit.Core.Documents;

internal static class DocumentFiles
{
    public static string ExistingFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_2C5C4E871C4C"));
        return fullPath;
    }

    public static string OutputDirectory(string sourcePath, string? requested)
    {
        var output = string.IsNullOrWhiteSpace(requested)
            ? Path.GetDirectoryName(sourcePath)!
            : Path.GetFullPath(requested);
        if (!Directory.Exists(output)) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_45C406D632F2"));
        return output;
    }

    public static string CreateTemporaryDirectory(string parent)
    {
        var temporary = Path.Combine(parent, ".orbit-document-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        return temporary;
    }

    public static string CommitFile(string temporaryFile, string directory, string name, CancellationToken cancellationToken)
    {
        for (var index = 1; index <= 10000; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var suffix = index == 1 ? string.Empty : $" ({index})";
            var target = Path.Combine(directory, Path.GetFileNameWithoutExtension(name) + suffix + Path.GetExtension(name));
            if (File.Exists(target) || Directory.Exists(target)) continue;
            try { File.Move(temporaryFile, target, overwrite: false); return target; }
            catch (IOException) when (File.Exists(target) || Directory.Exists(target)) { }
        }
        throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_7D0DAD0A78C1"));
    }

    public static string CommitDirectory(string temporary, string directory, string name, CancellationToken cancellationToken)
    {
        for (var index = 1; index <= 10000; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var suffix = index == 1 ? string.Empty : $" ({index})";
            var target = Path.Combine(directory, name + suffix);
            if (File.Exists(target) || Directory.Exists(target)) continue;
            try { Directory.Move(temporary, target); return target; }
            catch (IOException) when (File.Exists(target) || Directory.Exists(target)) { }
        }
        throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_27A36EDC8D06"));
    }

    public static void CleanupTemporaryDirectory(string? temporary, string parent)
    {
        if (temporary is null) return;
        // Only delete the exact generated child directory belonging to this operation.
        var resolved = Path.GetFullPath(temporary);
        var expectedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        if (!string.Equals(Path.GetDirectoryName(resolved), expectedParent, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith(".orbit-document-", StringComparison.Ordinal)) return;
        try
        {
            if (Directory.Exists(resolved) && (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(resolved, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
