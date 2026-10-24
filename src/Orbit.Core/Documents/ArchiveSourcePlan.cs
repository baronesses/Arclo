namespace Orbit.Core.Documents;

public sealed record ArchiveSourceItem(string Path, string EntryName, bool IsDirectory, long Bytes);
public sealed record ArchiveSourcePlan(IReadOnlyList<ArchiveSourceItem> Items, long TotalBytes, IReadOnlyList<string> Roots);

/// <summary>A bounded snapshot shared by ZIP creation and folder reports. No directories or outputs are created.</summary>
public static class ArchiveSourcePlanner
{
    public const int MaximumDepth = 128;
    private const int MaximumAncestorDepth = 256;

    public static ArchiveSourcePlan Create(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        cancellationToken.ThrowIfCancellationRequested();
        if (paths.Count == 0) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_50F110E6C58B"));
        try
        {
            var candidates = new List<SourceRoot>();
            var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentException.ThrowIfNullOrWhiteSpace(path);
                var fullPath = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
                if (!selected.Add(fullPath)) continue;
                if (selected.Count > ArchiveEngine.MaximumEntries)
                    throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Format("Text_DFC0636836A1",(object?)(ArchiveEngine.MaximumEntries)));
                var attributes = SafeAttributes(fullPath, cancellationToken);
                bool directory = (attributes & FileAttributes.Directory) != 0;
                if (directory && string.Equals(fullPath, System.IO.Path.GetPathRoot(fullPath), StringComparison.OrdinalIgnoreCase))
                    throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_35EA80E8FD72"));
                candidates.Add(new SourceRoot(fullPath, directory));
            }

            // Walk lexical parent segments rather than matching string prefixes or comparing every pair of roots.
            var selectedDirectories = new HashSet<string>(candidates.Where(root => root.IsDirectory).Select(root => root.Path), StringComparer.OrdinalIgnoreCase);
            var roots = new List<SourceRoot>();
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool covered = false;
                for (var ancestor = System.IO.Path.GetDirectoryName(candidate.Path); !string.IsNullOrEmpty(ancestor); ancestor = System.IO.Path.GetDirectoryName(ancestor))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (selectedDirectories.Contains(ancestor)) { covered = true; break; }
                }
                if (!covered) roots.Add(candidate);
            }
            var items = new List<ArchiveSourceItem>();
            var entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalBytes = 0;
            int scheduled = roots.Count;
            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stack = new Stack<PendingSource>();
                stack.Push(new PendingSource(root.Path, System.IO.Path.GetFileName(root.Path), root.IsDirectory, 0));
                while (stack.Count != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var pending = stack.Pop();
                    if (pending.Depth > MaximumDepth)
                        throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Format("Text_C5B2673B6B8B",(object?)(MaximumDepth)));
                    var attributes = SafeAttributes(pending.Path, cancellationToken);
                    bool directory = (attributes & FileAttributes.Directory) != 0;
                    if (directory != pending.IsDirectory) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_8FE304C5B52E"));
                    var name = ArchiveEngine.NormalizeEntryName(pending.EntryName, out _);
                    if (!entryNames.Add(name))
                        throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_2C0CB840C483"));
                    long bytes = directory ? 0 : new FileInfo(pending.Path).Length;
                    if (bytes > ArchiveEngine.MaximumSingleEntryBytes || (totalBytes = checked(totalBytes + bytes)) > ArchiveEngine.MaximumExpandedBytes)
                        throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_50D26F86710E"));
                    items.Add(new ArchiveSourceItem(pending.Path, directory ? name + "/" : name, directory, bytes));
                    if (!directory) continue;

                    var children = new List<PendingSource>();
                    foreach (var child in Directory.EnumerateFileSystemEntries(pending.Path))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (++scheduled > ArchiveEngine.MaximumEntries)
                            throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Format("Text_D0C6493938B3",(object?)(ArchiveEngine.MaximumEntries)));
                        if (pending.Depth == MaximumDepth)
                            throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Format("Text_C5B2673B6B8B",(object?)(MaximumDepth)));
                        var childPath = System.IO.Path.GetFullPath(child);
                        var childAttributes = SafeAttributes(childPath, cancellationToken);
                        children.Add(new PendingSource(childPath, name + "/" + System.IO.Path.GetFileName(childPath),
                            (childAttributes & FileAttributes.Directory) != 0, pending.Depth + 1));
                    }
                    // The bounded list makes reports deterministic without materializing an unbounded directory tree.
                    children.Sort((left, right) => StringComparer.Ordinal.Compare(left.EntryName, right.EntryName));
                    for (int index = children.Count - 1; index >= 0; index--) stack.Push(children[index]);
                }
            }
            return new ArchiveSourcePlan(items.AsReadOnly(), totalBytes, roots.Select(root => root.Path).ToList().AsReadOnly());
        }
        catch (InvalidDataException exception)
        { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_622E42BFAFF0") + exception.Message, exception); }
        catch (UnauthorizedAccessException exception)
        { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_3A67DA2884C8"), exception); }
        catch (IOException exception)
        { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_FE977DAE6567"), exception); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        { throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_C13BE58F13C3"), exception); }
    }

    /// <summary>Checks the selected entry and its lexical ancestors; selecting a descendant of a junction is also rejected.</summary>
    internal static FileAttributes SafeAttributes(string path, CancellationToken cancellationToken)
    {
        FileAttributes result = default;
        string? current = path;
        int depth = 0;
        while (!string.IsNullOrEmpty(current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++depth > MaximumAncestorDepth)
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Format("Text_5BE1D66B3A9C",(object?)(MaximumAncestorDepth)));
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_300F9CA34C9F"));
            if (depth == 1) result = attributes;
            current = System.IO.Path.GetDirectoryName(current);
        }
        return result;
    }

    internal static void Recheck(ArchiveSourceItem item, CancellationToken cancellationToken)
    {
        var attributes = SafeAttributes(item.Path, cancellationToken);
        if (((attributes & FileAttributes.Directory) != 0) != item.IsDirectory)
            throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_687A1048235C"));
    }

    private sealed record SourceRoot(string Path, bool IsDirectory);
    private sealed record PendingSource(string Path, string EntryName, bool IsDirectory, int Depth);
}
