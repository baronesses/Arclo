using System.Diagnostics.CodeAnalysis;

namespace Orbit.Core.Presets;

/// <summary>A source-independent recipe captured from a completed successful job.
/// It holds no source paths, result path or destination; each invocation binds the current selection and settings.</summary>
public sealed class RepeatRecipe
{
    private readonly ProcessingPreset _compatibilityPreset;
    public ImageJobOptions Options { get; }
    public string Description { get; }

    private RepeatRecipe(ImageJobOptions safeOptions)
    {
        Options = safeOptions;
        Description = PresetCatalog.DescribeOptions(safeOptions);
        _compatibilityPreset = new ProcessingPreset("repeat-last", global::Orbit.Core.Localization.L.Get("Text_B79CED42F6C2"), Description, safeOptions, false);
    }

    /// <param name="completedSuccessfully">The caller must require a finished job, succeeded status and a non-null result.
    /// Queued, running, failed, canceled or interrupted jobs must pass false.</param>
    public static bool TryCapture(ImageJobOptions? options, bool completedSuccessfully,
        [NotNullWhen(true)] out RepeatRecipe? recipe)
    {
        recipe = null;
        if (!completedSuccessfully) return false;
        try
        {
            recipe = new RepeatRecipe(PresetCatalog.SanitizeOptions(options!));
            return true;
        }
        catch (PresetValidationException) { return false; }
    }

    public bool CanApply(IReadOnlyList<ToolkitMetadata> currentSelection)
        => CanApply(currentSelection, out _);

    /// <summary>Uses the same type, stream and option constraints as a saved preset. Does not inspect files or execute work.</summary>
    public bool CanApply(IReadOnlyList<ToolkitMetadata> currentSelection, out string? reason)
        => PresetCatalog.CanApply(_compatibilityPreset, currentSelection, out reason);

    /// <summary>Rechecks the current metadata and returns fresh options using only the current output setting.
    /// The caller queues these options against the current paths; this method never replays history input paths.</summary>
    public bool TryBind(IReadOnlyList<ToolkitMetadata> currentSelection, string? currentOutputDirectory,
        [NotNullWhen(true)] out ImageJobOptions? options, out string? reason)
    {
        options = null;
        if (!CanApply(currentSelection, out reason)) return false;
        options = Options with { OutputDirectory = currentOutputDirectory };
        return true;
    }
}
