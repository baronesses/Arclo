namespace Orbit.Core.Presets;

/// <summary>A reusable processing recipe; it never owns source paths or an output directory.</summary>
public sealed record ProcessingPreset(string Id, string Name, string Description, ImageJobOptions Options, bool IsBuiltIn);

public sealed class PresetValidationException(string message) : Exception(message);
