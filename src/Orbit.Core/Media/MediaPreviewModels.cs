using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Orbit.MediaPreviewSmoke")]

namespace Orbit.Core.Media;

public sealed record MediaPreviewOptions
{
    public double StartSeconds { get; init; }
    public double DurationSeconds { get; init; } = 3;
    /// <summary>Parent for a private temporary directory, never a public result directory.</summary>
    public string? OutputDirectory { get; init; }
}

/// <summary>Owns one private preview. Close any player before disposing this result.</summary>
public sealed class MediaPreviewResult : IAsyncDisposable
{
    private readonly string _directory;
    private readonly string _parent;
    private readonly object _gate = new();
    private Task<bool>? _cleanup;

    internal MediaPreviewResult(string path, string directory, string parent, bool isVideo, double durationSeconds, double sourceStartSeconds)
    { Path = path;_directory = directory;_parent = parent;IsVideo = isVideo;DurationSeconds = durationSeconds;SourceStartSeconds = sourceStartSeconds; }

    public string Path { get; }
    public bool IsVideo { get; }
    public double DurationSeconds { get; }
    public double SourceStartSeconds { get; }

    public async ValueTask DisposeAsync()
    {
        Task<bool> cleanup;
        lock (_gate)
        {
            // Retry a previous unsuccessful cleanup if the player/file was still held open.
            if (_cleanup is null || _cleanup.IsCompletedSuccessfully && !_cleanup.Result)
                _cleanup = MediaPreviewFiles.StartCleanup(_directory, _parent);
            cleanup = _cleanup;
        }
        await MediaPreviewFiles.AwaitCleanupAsync(cleanup).ConfigureAwait(false);
    }
}

internal static class MediaPreviewFiles
{
    internal const string Prefix = ".orbit-media-preview-";

    internal static Task<bool> StartCleanup(string directory, string parent)
    {
        if (!string.Equals(System.IO.Path.GetDirectoryName(directory), System.IO.Path.TrimEndingDirectorySeparator(parent), StringComparison.OrdinalIgnoreCase)
            || !System.IO.Path.GetFileName(directory).StartsWith(Prefix, StringComparison.Ordinal)) return Task.FromResult(false);
        return Task.Run(async () =>
        {
            // Most WPF decoder/antivirus handles release quickly. Continue privately after
            // the caller's bounded wait if a native player needs a little longer to close.
            for (var attempt = 0; attempt < 24; attempt++)
            {
                try
                {
                    if (!Directory.Exists(directory)) return true;
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return false;
                    Directory.Delete(directory, recursive: true);return true;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
                if (attempt < 23) await Task.Delay(Math.Min(400, 25 << Math.Min(attempt, 4))).ConfigureAwait(false);
            }
            return false;
        }, CancellationToken.None);
    }

    internal static async Task AwaitCleanupAsync(Task<bool> cleanup)
    {
        try { await cleanup.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (TimeoutException)
        { _ = cleanup.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default); }
    }
}
