using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orbit.Core;
using Orbit.Core.Chains;

namespace Orbit.App.Chains;

public sealed record ImageChainLoadResult(IReadOnlyList<ImageChainDefinition> Chains, string? Warning);
public sealed record ImageChainMutationResult(IReadOnlyList<ImageChainDefinition> Chains, ImageChainDefinition? Chain, string? Error)
{ public bool Success => Error is null; }

/// <summary>Local named image recipes only. Reads never execute steps, access source/output paths, or repair corrupt JSON.</summary>
public sealed class ImageChainStore
{
    public const int MaximumFileBytes = 256 * 1024;
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true, WriteIndented = true, MaxDepth = 16,
        Converters = { new JsonStringEnumConverter<ImageAction>(allowIntegerValues: false) } };
    private readonly object _gate;
    public ImageChainStore(string? path = null)
    {
        FilePath = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Arclo", "chain-presets.json");
        string key; try { key = Path.GetFullPath(FilePath); } catch (Exception ex) when (Expected(ex)) { key = FilePath; }
        _gate = Gates.GetOrAdd(key, _ => new object());
    }
    public string FilePath { get; }
    public ImageChainLoadResult Load() { lock (_gate) return LoadCore(); }
    public ImageChainMutationResult Add(string name, IReadOnlyList<ImageJobOptions> steps)
        => Mutate(items => { var chain = ImageChainPolicy.Create(name, steps); items.Add(chain); return chain; });
    public ImageChainMutationResult Update(string id, string name, IReadOnlyList<ImageJobOptions> steps)
        => Mutate(items => {
            int index = items.FindIndex(chain => string.Equals(chain.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new ImageChainValidationException(global::Orbit.Core.Localization.L.Get("Text_495C0A8FF39A"));
            var chain = ImageChainPolicy.Create(name, steps, items[index].Id); items[index] = chain; return chain;
        });
    public ImageChainMutationResult Delete(string id)
        => Mutate(items => {
            int index = items.FindIndex(chain => string.Equals(chain.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new ImageChainValidationException(global::Orbit.Core.Localization.L.Get("Text_495C0A8FF39A"));
            items.RemoveAt(index); return null;
        });
    public ImageChainMutationResult Save(IReadOnlyList<ImageChainDefinition> chains)
        => Mutate(items => {
            if (chains is null || chains.Count > ImageChainPolicy.MaximumSavedChains) throw new ImageChainValidationException(global::Orbit.Core.Localization.L.Get("Text_DED99211928A"));
            var snapshot = chains.Take(ImageChainPolicy.MaximumSavedChains + 1).ToArray();
            if (snapshot.Length > ImageChainPolicy.MaximumSavedChains) throw new ImageChainValidationException(global::Orbit.Core.Localization.L.Get("Text_DED99211928A"));
            items.Clear(); items.AddRange(snapshot); return null;
        });
    /// <summary>Explicit recovery. Replaces only this metadata catalog with an empty one.</summary>
    public ImageChainMutationResult Reset()
    {
        lock (_gate)
        {
            try { var empty = Array.Empty<ImageChainDefinition>(); WriteAtomic(empty); return new(empty, null, null); }
            catch (Exception ex) when (Expected(ex)) { return new(Array.Empty<ImageChainDefinition>(), null, global::Orbit.Core.Localization.L.Get("Text_09AC1139290D") + ex.Message); }
        }
    }
    private ImageChainMutationResult Mutate(Func<List<ImageChainDefinition>, ImageChainDefinition?> change)
    {
        lock (_gate)
        {
            var loaded = LoadCore();
            if (loaded.Warning is not null) return new(loaded.Chains, null, global::Orbit.Core.Localization.L.Get("Text_655FBDC021B9") + loaded.Warning + global::Orbit.Core.Localization.L.Get("Text_D6D63F700592"));
            try
            {
                var items = loaded.Chains.ToList(); var changed = change(items); var normalized = ValidateList(items); WriteAtomic(normalized);
                return new(normalized, changed is null ? null : normalized.Single(chain => chain.Id == changed.Id), null);
            }
            catch (Exception ex) when (Expected(ex)) { return new(loaded.Chains, null, global::Orbit.Core.Localization.L.Get("Text_60B7638E589E") + ex.Message); }
        }
    }
    private ImageChainLoadResult LoadCore()
    {
        try
        {
            byte[] bytes;
            using (var input = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                if (input.Length is <= 0 or > MaximumFileBytes) throw new ImageChainValidationException(global::Orbit.Core.Localization.L.Get("Text_124053A54178"));
                bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
            }
            var offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
            using (var schema = JsonDocument.Parse(bytes.AsMemory(offset), new JsonDocumentOptions { MaxDepth = 16 })) ValidateSchema(schema.RootElement);
            var document = JsonSerializer.Deserialize<ChainFile>(bytes.AsSpan(offset), JsonOptions);
            if (document is null || document.Version != 1 || document.Chains is null) throw new ImageChainValidationException(global::Orbit.Core.Localization.L.Get("Text_F039C884AC89"));
            return new(ValidateList(document.Chains), null);
        }
        catch (FileNotFoundException) { return new(Array.Empty<ImageChainDefinition>(), null); }
        catch (DirectoryNotFoundException) { return new(Array.Empty<ImageChainDefinition>(), null); }
        catch (Exception ex) when (Expected(ex)) { return new(Array.Empty<ImageChainDefinition>(), global::Orbit.Core.Localization.L.Get("Text_202036D64907") + ex.Message); }
    }
    private static IReadOnlyList<ImageChainDefinition> ValidateList(IReadOnlyList<ImageChainDefinition> chains)
    {
        if (chains.Count > ImageChainPolicy.MaximumSavedChains) throw new ImageChainValidationException(global::Orbit.Core.Localization.L.Get("Text_CCD21630E7AF"));
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ImageChainDefinition>();
        foreach (var item in chains.Take(ImageChainPolicy.MaximumSavedChains + 1))
        {
            if (result.Count >= ImageChainPolicy.MaximumSavedChains) throw new ImageChainValidationException(global::Orbit.Core.Localization.L.Get("Text_CCD21630E7AF"));
            var chain = ImageChainPolicy.Validate(item);
            if (!ids.Add(chain.Id) || !names.Add(chain.Name)) throw new ImageChainValidationException(global::Orbit.Core.Localization.L.Get("Text_8F8B2EA64A18"));
            result.Add(chain);
        }
        return result.AsReadOnly();
    }
    private static void ValidateSchema(JsonElement root)
    {
        void Unique(JsonElement item)
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in item.EnumerateObject()) { if (!keys.Add(property.Name)) throw new JsonException("Duplicate property."); Unique(property.Value); }
            }
            else if (item.ValueKind == JsonValueKind.Array) foreach (var child in item.EnumerateArray()) Unique(child);
        }
        static JsonElement Property(JsonElement item, string name)
        {
            if (item.ValueKind == JsonValueKind.Object)
                foreach (var property in item.EnumerateObject())
                    if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
            return default;
        }
        Unique(root);
        var version = Property(root, "version"); var chains = Property(root, "chains");
        if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1
            || chains.ValueKind != JsonValueKind.Array || chains.GetArrayLength() > ImageChainPolicy.MaximumSavedChains) throw new JsonException("Invalid chain catalog.");
        foreach (var chain in chains.EnumerateArray())
        {
            var steps = Property(chain, "steps");
            if (Property(chain, "id").ValueKind != JsonValueKind.String || Property(chain, "name").ValueKind != JsonValueKind.String
                || steps.ValueKind != JsonValueKind.Array || steps.GetArrayLength() is < ImageChainPolicy.MinimumSteps or > ImageChainPolicy.MaximumSteps)
                throw new JsonException("Missing chain identity or bounded steps.");
            foreach (var step in steps.EnumerateArray())
            {
                var action = Property(step, "action");
                if (action.ValueKind != JsonValueKind.String || !Enum.GetNames<ImageAction>().Contains(action.GetString(), StringComparer.OrdinalIgnoreCase))
                    throw new JsonException("A step requires one explicit known named action.");
            }
        }
    }
    private void WriteAtomic(IReadOnlyList<ImageChainDefinition> chains)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new ChainFile(1, chains), JsonOptions);
        if (bytes.Length > MaximumFileBytes) throw new ImageChainValidationException(global::Orbit.Core.Localization.L.Get("Text_0419EDF60082"));
        var target = Path.GetFullPath(FilePath); var parent = Path.GetDirectoryName(target)!; string? temporary = null;
        try
        {
            Directory.CreateDirectory(parent); temporary = Path.Combine(parent, ".orbit-chains-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { output.Write(bytes); output.Flush(flushToDisk: true); }
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (temporary is not null) try { File.Delete(temporary); } catch (Exception ex) when (Expected(ex)) { } }
    }
    private static bool Expected(Exception ex) => ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException
        or NotSupportedException or JsonException or ImageChainValidationException;
    private sealed record ChainFile(int Version, IReadOnlyList<ImageChainDefinition> Chains);
}
