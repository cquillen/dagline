namespace Dagline.Testing;

/// <summary>
/// Rung each time something is queued for a scripted run's pump, or a timer is set on its clock, so that a pump waiting in real
/// time for work it cannot see, such as a body's real I/O, wakes as soon as there is something to pump.
/// </summary>
/// <remarks>
/// A waiter reads <see cref="Rings"/>, checks there is nothing to do, and then waits for a ring after the count it read. The work
/// is queued before the ring, so work queued before the check is seen by the check, and work queued after it rings after the count.
/// </remarks>
internal sealed class Doorbell
{
    // An object, not a Lock: the sync wait uses Monitor.Wait, which a Lock does not support.
    private readonly object _gate = new();
    private long _rings;
    private TaskCompletionSource? _nextRing;

    public long Rings
    {
        get
        {
            lock (_gate)
            {
                return _rings;
            }
        }
    }

    public void Ring()
    {
        TaskCompletionSource? waiting;
        lock (_gate)
        {
            _rings++;
            waiting = _nextRing;
            _nextRing = null;
            Monitor.PulseAll(_gate);
        }

        waiting?.TrySetResult();
    }

    /// <summary>Waits on the calling thread for a ring after <paramref name="seen"/>, for at most <paramref name="limit"/> of real time.</summary>
    /// <returns><see langword="false"/> when the limit ran out first.</returns>
    public bool WaitAfter(long seen, TimeSpan limit)
    {
        var started = TimeProvider.System.GetTimestamp();
        lock (_gate)
        {
            while (_rings == seen)
            {
                var left = limit == Timeout.InfiniteTimeSpan ? Timeout.InfiniteTimeSpan : limit - TimeProvider.System.GetElapsedTime(started);
                if (left != Timeout.InfiniteTimeSpan && left <= TimeSpan.Zero)
                {
                    return false;
                }

                Monitor.Wait(_gate, left);
            }
        }

        return true;
    }

    /// <summary>Completes on a ring after <paramref name="seen"/>, on the thread pool.</summary>
    public Task AfterAsync(long seen)
    {
        lock (_gate)
        {
            if (_rings != seen)
            {
                return Task.CompletedTask;
            }

            _nextRing ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _nextRing.Task;
        }
    }
}
