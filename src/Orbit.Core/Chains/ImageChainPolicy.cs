using System.Text;
using Orbit.Core.Presets;

namespace Orbit.Core.Chains;

public static class ImageChainPolicy
{
    public const int MinimumSteps = 2;
    public const int MaximumSteps = 4;
    public const int MaximumSavedChains = 16;
    public const int MaximumNameLength = 64;
    public const int MaximumDimension = 16000;
    public const long MaximumPixels = 40_000_000;
    public const long MaximumFileBytes = 256L * 1024 * 1024;
    public const long MaximumIntermediateBytes = 1024L * 1024 * 1024;

    public static bool IsAllowedAction(ImageAction action) => action is ImageAction.ImageTransform or ImageAction.Resize
        or ImageAction.RemoveImageMetadata or ImageAction.Jpg or ImageAction.Png or ImageAction.Bmp or ImageAction.Tiff or ImageAction.Gif or ImageAction.Ico or ImageAction.Svg or ImageAction.WebP or ImageAction.Avif or ImageAction.Compress;

    public static IReadOnlyList<ImageJobOptions> SanitizeSteps(IReadOnlyList<ImageJobOptions> steps)
    {
        if (steps is null || steps.Count is < MinimumSteps or > MaximumSteps)
            throw new ImageChainValidationException(global::Orbit.Core.Localization.L.Get("Text_67D0C94B2E0D"));
        var snapshot = steps.Take(MaximumSteps + 1).ToArray();
        if (snapshot.Length is < MinimumSteps or > MaximumSteps)
            throw new ImageChainValidationException(global::Orbit.Core.Localization.L.Get("Text_B9503DD93E64"));
        var result = new List<ImageJobOptions>(snapshot.Length);
        foreach (var step in snapshot)
        {
            if (step is null || !IsAllowedAction(step.Action) || step.ChainSteps is null || step.ChainSteps.Count != 0)
                throw new ImageChainValidationException(global::Orbit.Core.Localization.L.Get("Text_AB81732D09EA"));
            try { result.Add(PresetCatalog.SanitizeOptions(step)); }
            catch (PresetValidationException ex) { throw new ImageChainValidationException(ex.Message); }
        }
        return result.AsReadOnly();
    }

    public static ImageChainDefinition Create(string name, IReadOnlyList<ImageJobOptions> steps, string? id = null)
        => new(ValidateId(id ?? "chain-" + Guid.NewGuid().ToString("N")), NormalizeName(name), SanitizeSteps(steps));

    public static ImageChainDefinition Validate(ImageChainDefinition definition)
    {
        if (definition is null) throw new ImageChainValidationException(global::Orbit.Core.Localization.L.Get("Text_0C62ECE24158"));
        return Create(definition.Name, definition.Steps, ValidateId(definition.Id));
    }

    public static bool CanApply(ImageChainDefinition definition, IReadOnlyList<ToolkitMetadata> selection, out string? reason)
    {
        reason = null;
        IReadOnlyList<ImageJobOptions> steps;
        try { steps = Validate(definition).Steps; }
        catch (ImageChainValidationException ex) { reason = ex.Message; return false; }
        if (selection is null || selection.Count == 0) { reason = global::Orbit.Core.Localization.L.Get("Text_A1FD38E140BD"); return false; }
        foreach (var item in selection)
        {
            if (item is null || item.Kind != ToolkitKind.Image || !Bounded(item.Width, item.Height, item.Bytes))
            { reason = global::Orbit.Core.Localization.L.Get("Text_DCB4B236C912"); return false; }
            int width = item.Width, height = item.Height;
            foreach (var step in steps)
            {
                (width, height) = StepSize(width, height, step);
                if (!Bounded(width, height, 1)) { reason = global::Orbit.Core.Localization.L.Get("Text_7587872A8F93"); return false; }
            }
        }
        return true;
    }
    public static bool CanApply(ImageChainDefinition definition, IReadOnlyList<ToolkitMetadata> selection) => CanApply(definition, selection, out _);
    public static string DescribeSteps(IReadOnlyList<ImageJobOptions> steps)
        => string.Join(" → ", SanitizeSteps(steps).Select(PresetCatalog.DescribeOptions));

    internal static bool Bounded(int width, int height, long bytes)
        => width is > 0 and <= MaximumDimension && height is > 0 and <= MaximumDimension
            && (long)width * height <= MaximumPixels && bytes is > 0 and <= MaximumFileBytes;
    internal static (int Width, int Height) StepSize(int width, int height, ImageJobOptions step)
    {
        if (step.Action == ImageAction.ImageTransform && step.RotationDegrees is 90 or 270) return (height, width);
        if (step.Action == ImageAction.Ico) return (256, 256);
        if (step.Action != ImageAction.Resize) return (width, height);
        if (!step.MaintainAspectRatio) return (step.NoUpscale ? Math.Min(width, step.Width!.Value) : step.Width!.Value,
            step.NoUpscale ? Math.Min(height, step.Height!.Value) : step.Height!.Value);
        var scale = step.ResizePercent is{} percent?percent/100:Math.Min(step.Width is { } w ? (double)w / width : double.PositiveInfinity,
            step.Height is { } h ? (double)h / height : double.PositiveInfinity);
        if (step.NoUpscale) scale = Math.Min(1, scale);
        return (Math.Max(1, (int)Math.Round(width * scale, MidpointRounding.AwayFromZero)),
            Math.Max(1, (int)Math.Round(height * scale, MidpointRounding.AwayFromZero)));
    }
    private static string ValidateId(string id)
    {
        if (id is null || !id.StartsWith("chain-", StringComparison.Ordinal) || id.Length != 38 || !Guid.TryParseExact(id[6..], "N", out _))
            throw new ImageChainValidationException(global::Orbit.Core.Localization.L.Get("Text_445E1EF2257F"));
        return id.ToLowerInvariant();
    }
    private static string NormalizeName(string name)
    {
        if (name is null) throw new ImageChainValidationException(global::Orbit.Core.Localization.L.Get("Text_20EFF7E62734"));
        try { name = name.Trim().Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { throw new ImageChainValidationException(global::Orbit.Core.Localization.L.Get("Text_AE3B48B19AE0")); }
        if (name.Length is < 1 or > MaximumNameLength || name.Any(char.IsControl))
            throw new ImageChainValidationException(global::Orbit.Core.Localization.L.Get("Text_6AF5C18B959E"));
        return name;
    }
}
