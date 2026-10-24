namespace Orbit.Core;

/// <summary>Optional host-native image codecs, without a dependency on a desktop UI framework in Core.</summary>
public interface IImageCodecFallback
{
    bool CanRead(string path);
    bool CanExecute(ImageJobOptions options);
    Task<ImageMetadata> InspectAsync(string path, CancellationToken cancellationToken = default);
    Task<byte[]> CreatePreviewAsync(string path, int maxSize = 640, CancellationToken cancellationToken = default);
    Task<ImageJobResult> ExecuteAsync(string path, ImageJobOptions options,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    Task<ImageJobResult> TransformAsync(string path, ImageTransformOptions options, CancellationToken cancellationToken = default);
}
