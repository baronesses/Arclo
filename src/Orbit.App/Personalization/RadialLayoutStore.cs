using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json;
using System.Threading;
using Orbit.Core;

namespace Orbit.App.Personalization;

public sealed record RadialLayoutLoadResult(RadialLayout Layout, string? Warning)
{
    public bool RecoveryRequired => Warning is not null;
}

public sealed record RadialLayoutMutationResult(RadialLayout Layout, string? Error)
{
    public bool Success => Error is null;
}

/// <summary>Explicit local radial preferences. Construction has no filesystem I/O.
/// Invalid JSON is preserved and mutations stay blocked until an explicit successful Reset.</summary>
public sealed class RadialLayoutStore
{
    public const int MaximumFileBytes = 32 * 1024;
    public const int MaximumContexts = RadialLayout.MaximumContexts;
    public const int MaximumActions = RadialLayout.MaximumActions;
    private const int MaximumInputContexts = 64;
    private const int MaximumInputActions = 128;
    private static readonly ConcurrentDictionary<string, PathState> States = new(StringComparer.OrdinalIgnoreCase);
    private readonly PathState _state;

    public RadialLayoutStore(string? path = null)
    {
        FilePath = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Arclo", "personalization.json");
        // Bind relative paths at construction without opening or creating anything.
        try { FilePath = Path.GetFullPath(FilePath); } catch (Exception exception) when (ExpectedFailure(exception)) { }
        _state = States.GetOrAdd(FilePath, _ => new PathState());
    }

    public string FilePath { get; }

    public RadialLayoutLoadResult Load() { lock (_state.Gate) return LoadCore(); }

    public RadialLayoutMutationResult SetOrder(string context, IReadOnlyList<ImageAction> preferredOrder)
    {
        if (!RadialLayout.TryCanonicalContext(context, out var canonical))
            return new(RadialLayout.Empty, global::Orbit.Core.Localization.L.Get("Text_31550474A32F"));
        if (preferredOrder is null) return new(RadialLayout.Empty, global::Orbit.Core.Localization.L.Get("Text_E5B3D35DE8D0"));
        return Mutate(layout =>
        {
            var orders = layout.Orders.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            var actions = RadialLayout.NormalizeActions(preferredOrder);
            if (actions.Count == 0) orders.Remove(canonical);else orders[canonical] = actions;
            return new RadialLayout(orders);
        });
    }

    public RadialLayoutMutationResult ClearOrder(string context) => SetOrder(context, Array.Empty<ImageAction>());

    public RadialLayoutMutationResult Save(RadialLayout layout)
    {
        if (layout is null) return new(RadialLayout.Empty, global::Orbit.Core.Localization.L.Get("Text_3D94CE62B754"));
        return Mutate(_ => layout);
    }

    /// <summary>User-requested recovery or reset of all preferred orders. No source/result files are accessed.</summary>
    public RadialLayoutMutationResult Reset()
    {
        lock (_state.Gate)
        {
            try
            {
                WriteAtomic(RadialLayout.Empty);
                _state.RecoveryWarning = null;
                return new(RadialLayout.Empty, null);
            }
            catch (Exception exception) when (ExpectedFailure(exception))
            { return new(RadialLayout.Empty, global::Orbit.Core.Localization.L.Get("Text_D94AFBB87B26") + FailureCaption(exception)); }
        }
    }

    private RadialLayoutMutationResult Mutate(Func<RadialLayout, RadialLayout> change)
    {
        lock (_state.Gate)
        {
            var loaded = LoadCore();
            if (loaded.RecoveryRequired)
                return new(loaded.Layout, global::Orbit.Core.Localization.L.Get("Text_E30B045A0FD0") + loaded.Warning + global::Orbit.Core.Localization.L.Get("Text_B5782C1FCE93"));
            try
            {
                var layout = change(loaded.Layout);
                WriteAtomic(layout);
                return new(layout, null);
            }
            catch (Exception exception) when (ExpectedFailure(exception))
            { return new(loaded.Layout, global::Orbit.Core.Localization.L.Get("Text_077227875BAA") + FailureCaption(exception)); }
        }
    }

    private RadialLayoutLoadResult LoadCore()
    {
        if (_state.RecoveryWarning is not null) return new(RadialLayout.Empty, _state.RecoveryWarning);
        try
        {
            byte[] bytes;
            using (var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                if (stream.Length is <= 0 or > MaximumFileBytes) throw new LayoutValidationException(global::Orbit.Core.Localization.L.Get("Text_0BA05F130CA3"));
                bytes = new byte[(int)stream.Length];stream.ReadExactly(bytes);
            }
            var offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
            using var document = JsonDocument.Parse(bytes.AsMemory(offset), new JsonDocumentOptions { MaxDepth = 8 });
            var root = Properties(document.RootElement);
            if (!root.TryGetValue("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1
                || !root.TryGetValue("orders", out var orders) || orders.ValueKind != JsonValueKind.Object)
                throw new LayoutValidationException(global::Orbit.Core.Localization.L.Get("Text_6983D1A00738"));
            var contexts = Properties(orders);
            if (contexts.Count > MaximumInputContexts) throw new LayoutValidationException(global::Orbit.Core.Localization.L.Get("Text_310268E4D503"));
            var result = new Dictionary<string, IReadOnlyList<ImageAction>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in contexts)
            {
                if (!RadialLayout.TryCanonicalContext(pair.Key, out var context)) continue;
                if (result.ContainsKey(context)) throw new LayoutValidationException(global::Orbit.Core.Localization.L.Get("Text_252CFAD6DD55"));
                if (pair.Value.ValueKind != JsonValueKind.Array || pair.Value.GetArrayLength() > MaximumInputActions)
                    throw new LayoutValidationException(global::Orbit.Core.Localization.L.Get("Text_D02FA8574961"));
                var actions = new List<ImageAction>();
                foreach (var action in pair.Value.EnumerateArray())
                {
                    if (action.ValueKind != JsonValueKind.String) throw new LayoutValidationException(global::Orbit.Core.Localization.L.Get("Text_3310FF52B47D"));
                    var name = action.GetString()?.Trim();
                    if (Enum.TryParse<ImageAction>(name, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
                        && string.Equals(Enum.GetName(parsed), name, StringComparison.OrdinalIgnoreCase)) actions.Add(parsed);
                }
                result.Add(context, RadialLayout.NormalizeActions(actions));
            }
            return new(new RadialLayout(result), null);
        }
        catch (FileNotFoundException) { return new(RadialLayout.Empty, null); }
        catch (DirectoryNotFoundException) { return new(RadialLayout.Empty, null); }
        catch (Exception exception) when (ExpectedFailure(exception))
        {
            var warning = global::Orbit.Core.Localization.L.Get("Text_7AF5796142CF") + FailureCaption(exception) + global::Orbit.Core.Localization.L.Get("Text_CC3D903812D1");
            // A temporary reader/AV lock is not corrupt JSON: allow a later retry once
            // access returns. Malformed/unsupported data requires explicit recovery.
            if (exception is JsonException or LayoutValidationException) _state.RecoveryWarning = warning;
            return new(RadialLayout.Empty, warning);
        }
    }

    private static Dictionary<string, JsonElement> Properties(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new LayoutValidationException(global::Orbit.Core.Localization.L.Get("Text_C2305C71558A"));
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in item.EnumerateObject())
            if (!result.TryAdd(property.Name, property.Value)) throw new LayoutValidationException(global::Orbit.Core.Localization.L.Get("Text_700EB6145600"));
        return result;
    }

    private void WriteAtomic(RadialLayout layout)
    {
        var target = Path.GetFullPath(FilePath);var directory = Path.GetDirectoryName(target)!;string? temporary = null;
        try
        {
            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                json.WriteStartObject();json.WriteNumber("version", 1);json.WriteStartObject("orders");
                foreach (var pair in layout.Orders)
                {
                    json.WriteStartArray(pair.Key);
                    foreach (var action in pair.Value) json.WriteStringValue(action.ToString());
                    json.WriteEndArray();
                }
                json.WriteEndObject();json.WriteEndObject();json.Flush();
            }
            if (buffer.Length > MaximumFileBytes) throw new LayoutValidationException(global::Orbit.Core.Localization.L.Get("Text_368D69F67BA2"));
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, ".orbit-personalization-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { buffer.Position = 0;buffer.CopyTo(stream);stream.Flush(flushToDisk: true); }
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(temporary, target, overwrite: true);break; }
                catch (Exception exception) when (attempt < 10 && TransientReplacementFailure(exception))
                { Thread.Sleep(Math.Min(300, 25 << Math.Min(attempt, 4))); }
            }
        }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (Exception exception) when (ExpectedFailure(exception)) { }
        }
    }

    private static bool TransientReplacementFailure(Exception exception) => exception is UnauthorizedAccessException
        || exception is IOException && (exception.HResult & 0xffff) is 5 or 32 or 33 or 303;
    private static bool ExpectedFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or SecurityException
        or JsonException or ArgumentException or NotSupportedException or LayoutValidationException;
    private static string FailureCaption(Exception exception) => exception switch
    {
        LayoutValidationException => exception.Message,
        JsonException => global::Orbit.Core.Localization.L.Get("Text_14850C458BE8"),
        UnauthorizedAccessException or SecurityException => global::Orbit.Core.Localization.L.Get("Text_DC59AF344F47"),
        ArgumentException or NotSupportedException => global::Orbit.Core.Localization.L.Get("Text_1F73D303A66C"),
        _ => global::Orbit.Core.Localization.L.Get("Text_5BC52C74F7ED")
    };
    private sealed class PathState { public object Gate { get; } = new();public string? RecoveryWarning { get; set; } }
    private sealed class LayoutValidationException(string message) : Exception(message);
}
