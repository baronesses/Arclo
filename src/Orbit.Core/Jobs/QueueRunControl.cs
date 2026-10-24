namespace Orbit.Core.Jobs;

/// <summary>
/// Delays permission to start queued work. Work that has already started is unaffected.
/// </summary>
public sealed class QueueRunControl
{
    private readonly object _sync = new();
    private TaskCompletionSource? _resume;

    public bool IsPaused
    {
        get
        {
            lock (_sync)
                return _resume is not null;
        }
    }

    /// <returns>True when this call changed the state to paused.</returns>
    public bool Pause()
    {
        lock (_sync)
        {
            if (_resume is not null)
                return false;

            _resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }

    /// <returns>True when this call changed the state to running.</returns>
    public bool Resume()
    {
        TaskCompletionSource? resumed;
        lock (_sync)
        {
            resumed = _resume;
            if (resumed is null)
                return false;

            _resume = null;
        }

        resumed.TrySetResult();
        return true;
    }

    /// <summary>
    /// Waits asynchronously until starting work is permitted, or cancellation is requested.
    /// Callers must still use their normal concurrency limit and cancellation checks.
    /// </summary>
    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task? pending;
            lock (_sync)
                pending = _resume?.Task;

            if (pending is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
            // Resume may already have been followed by another Pause. Recheck the new epoch.
        }
    }
}
