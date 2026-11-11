using System.Threading.Channels;

namespace Dagline;

/// <summary>
/// One reporter's own unbounded queue of events, and the loop that hands them to it in the order published, one call at a time.
/// A reporter that fails, or is still busy when the drop token is cancelled, is dropped: it is handed nothing more,
/// the events still queued for it are let go, and its error is the result of <see cref="Delivered"/>.
/// </summary>
internal sealed class ReporterQueue
{
    private readonly Channel<(RunEvent Event, Snapshot Snapshot)> _queue =
        Channel.CreateUnbounded<(RunEvent, Snapshot)>(new UnboundedChannelOptions { SingleReader = true });

    private readonly IReporter _reporter;

    /// <param name="reporter">The reporter this queue hands events to.</param>
    /// <param name="dropped">
    /// Passed to every call of the reporter. Once it is cancelled the reporter is dropped: a call that has not come back
    /// is no longer waited for, and no further call is made.
    /// </param>
    public ReporterQueue(IReporter reporter, CancellationToken dropped)
    {
        _reporter = reporter;

        // Not started with the token: a cancelled token must drop the reporter with an error, never cancel the loop itself.
        Delivered = Task.Run(() => DeliverAsync(dropped), CancellationToken.None);
    }

    /// <summary>
    /// Completes with <see langword="null"/> once <see cref="Complete"/> has been called and the reporter has handled every event,
    /// or with the reporter's error as soon as it fails or is dropped by the drop token. It never faults.
    /// </summary>
    public Task<ReporterError?> Delivered { get; }

    /// <summary>
    /// Queues an event and returns at once: the caller never waits for the reporter.
    /// An event published after the reporter was dropped is discarded.
    /// </summary>
    public void Publish(RunEvent runEvent, Snapshot snapshot) => _queue.Writer.TryWrite((runEvent, snapshot));

    public void Complete() => _queue.Writer.TryComplete();

    private async Task<ReporterError?> DeliverAsync(CancellationToken dropped)
    {
        // Read without the token: the events still queued when it is cancelled are dropped below, as for a reporter that failed.
        await foreach (var (runEvent, snapshot) in _queue.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            try
            {
                dropped.ThrowIfCancellationRequested();
                await HandAsync(runEvent, snapshot, dropped).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // A reporter is consumer code, so whatever it throws is recorded and never reaches the run.
                // Closing the queue is what drops it: nothing published from here on is kept for it.
                Complete();
                Discard();
                return new ReporterError(_reporter, runEvent, exception);
            }
        }

        return null;
    }

    /// <summary>
    /// Hands one event to the reporter and waits for the call to come back, or for <paramref name="dropped"/>,
    /// whichever is first. A call no longer waited for may still fail later, and that failure is observed and dropped.
    /// </summary>
    private async Task HandAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken dropped)
    {
        var handed = _reporter.OnEventAsync(runEvent, snapshot, dropped);
        if (handed.IsCompleted)
        {
            await handed.ConfigureAwait(false);
            return;
        }

        var call = handed.AsTask();
        _ = call.ContinueWith(
            static ended => _ = ended.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        await call.WaitAsync(dropped).ConfigureAwait(false);
    }

    /// <summary>
    /// Lets go of the events a dropped reporter had not been handed yet. The queue lives as long as the run does,
    /// and a reporter that keeps the snapshot source keeps the run, so they would otherwise stay in memory with their snapshots.
    /// </summary>
    private void Discard()
    {
        while (_queue.Reader.TryRead(out _))
        {
        }
    }
}
