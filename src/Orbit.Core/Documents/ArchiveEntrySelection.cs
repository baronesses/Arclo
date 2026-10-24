using System.Collections.Frozen;

namespace Orbit.Core.Documents;

public sealed record ArchiveSelectionEntry(string RelativeName, bool IsDirectory);

/// <summary>Selection over an already safety-validated archive catalog. Names are never rewritten.</summary>
public static class ArchiveEntrySelection
{
    public const int MaximumSelectedNames = 1000;
    public const int MaximumSelectedCharacters = 131072;

    public static IReadOnlyList<string> Snapshot(IReadOnlyList<string> selectedNames)
    {
        ArgumentNullException.ThrowIfNull(selectedNames);
        if (selectedNames.Count is < 1 or > MaximumSelectedNames) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_5D64E2A64D62"));
        var snapshot = selectedNames.ToArray(); long characters = 0;
        if (snapshot.Length is < 1 or > MaximumSelectedNames) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_5D64E2A64D62"));
        foreach (var name in snapshot)
        {
            if (string.IsNullOrEmpty(name) || (characters += name.Length) > MaximumSelectedCharacters)
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_9F1DEC0F9530"));
        }
        return Array.AsReadOnly(snapshot);
    }

    public static IReadOnlySet<string> Create(IReadOnlyList<string> selectedNames, IReadOnlyList<ArchiveSelectionEntry> entries)
    {
        var selected = Snapshot(selectedNames); ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > 10000) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_AF27880D435F"));
        var catalog = new Dictionary<string, ArchiveSelectionEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
            if (entry is null || string.IsNullOrEmpty(entry.RelativeName) || !catalog.TryAdd(entry.RelativeName, entry))
                throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_67EEF7246ADD"));
        var exact = new HashSet<string>(StringComparer.Ordinal); var directories = new List<string>();
        foreach (var name in selected)
        {
            if (!catalog.TryGetValue(name, out var entry)) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_AF80BFBBD3CF"));
            if (!exact.Add(name)) continue;
            if (entry.IsDirectory) directories.Add(name.TrimEnd('/') + "/");
        }
        // Reduce overlaps, then use disjoint sorted prefixes. No every-entry ancestor expansion.
        var prefixes = new List<string>();
        foreach (var directory in directories.Order(StringComparer.Ordinal))
            if (prefixes.Count == 0 || !directory.StartsWith(prefixes[^1], StringComparison.Ordinal)) prefixes.Add(directory);
        var orderedPrefixes = prefixes.ToArray();
        foreach (var entry in entries)
        {
            var index = Array.BinarySearch(orderedPrefixes, entry.RelativeName, StringComparer.Ordinal);
            if (index < 0) index = ~index - 1;
            if (index >= 0 && entry.RelativeName.StartsWith(orderedPrefixes[index], StringComparison.Ordinal)) exact.Add(entry.RelativeName);
        }
        return exact.ToFrozenSet(StringComparer.Ordinal);
    }
}
