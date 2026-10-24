using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Orbit.Core;

namespace Orbit.App.Personalization;

/// <summary>Immutable preferred order only. Actual context actions remain the caller's authority.</summary>
public sealed class RadialLayout
{
    public const int MaximumContexts = 9;
    public const int MaximumActions = 6;
    public static IReadOnlyList<string> Contexts { get; } = Array.AsReadOnly(new[] { "Image", "Video", "Audio", "Pdf", "Zip", "Other", "Folder", "Tar", "SevenZip" });
    public static RadialLayout Empty { get; } = new();

    public RadialLayout(IReadOnlyDictionary<string, IReadOnlyList<ImageAction>>? orders = null)
    {
        var normalized = new Dictionary<string, IReadOnlyList<ImageAction>>(StringComparer.OrdinalIgnoreCase);
        if (orders is not null)
            foreach (var pair in orders)
            {
                if (!TryCanonicalContext(pair.Key, out var context) || pair.Value is null) continue;
                var actions = NormalizeActions(pair.Value);
                if (actions.Count == 0 || normalized.ContainsKey(context)) continue;
                normalized.Add(context, actions);
            }
        // Canonical context order makes equivalent layouts serialize identically.
        Orders = new ReadOnlyDictionary<string, IReadOnlyList<ImageAction>>(Contexts.Where(normalized.ContainsKey)
            .ToDictionary(context => context, context => normalized[context], StringComparer.OrdinalIgnoreCase));
    }

    public IReadOnlyDictionary<string, IReadOnlyList<ImageAction>> Orders { get; }

    public IReadOnlyList<ImageAction> GetOrder(string? context) => TryCanonicalContext(context, out var canonical)
        && Orders.TryGetValue(canonical, out var order) ? order : Array.Empty<ImageAction>();

    /// <summary>Reorders current elements, preserving every item and stable order within equal/unlisted actions.
    /// Preferred actions absent from current are ignored; no sector can be added or removed.</summary>
    public IReadOnlyList<T> Apply<T>(string? context, IReadOnlyList<T> current, Func<T, ImageAction> actionSelector)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(actionSelector);
        var order = GetOrder(context);
        if (order.Count == 0) return Array.AsReadOnly(current.ToArray());
        var ranks = order.Select((action, index) => (action, index)).ToDictionary(pair => pair.action, pair => pair.index);
        return Array.AsReadOnly(current.Select(item => (Item: item, Action: actionSelector(item)))
            .OrderBy(pair => ranks.TryGetValue(pair.Action, out var rank) ? rank : int.MaxValue)
            .Select(pair => pair.Item).ToArray());
    }

    public static bool TryCanonicalContext(string? context, out string canonical)
    {
        canonical = "";
        if (string.IsNullOrWhiteSpace(context)) return false;
        var candidate = context.Trim();
        if (candidate.Equals("Mixed", StringComparison.OrdinalIgnoreCase)) candidate = "Other";
        var known = Contexts.FirstOrDefault(value => value.Equals(candidate, StringComparison.OrdinalIgnoreCase));
        if (known is null) return false;
        canonical = known;return true;
    }

    internal static IReadOnlyList<ImageAction> NormalizeActions(IReadOnlyList<ImageAction> actions)
    {
        var seen = new HashSet<ImageAction>();var result = new List<ImageAction>();
        foreach (var action in actions)
        {
            if (!Enum.IsDefined(action) || !seen.Add(action)) continue;
            result.Add(action);if (result.Count == MaximumActions) break;
        }
        return result.AsReadOnly();
    }
}
