using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Orbit.ImageChainSmoke")]

namespace Orbit.Core.Chains;

/// <summary>Image processing parameters only. No source paths, output directory or automatic execution.</summary>
public sealed record ImageChainDefinition(string Id, string Name, IReadOnlyList<ImageJobOptions> Steps);

public sealed class ImageChainValidationException(string message) : Exception(message);
