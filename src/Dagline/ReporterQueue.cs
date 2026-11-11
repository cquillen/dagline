using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace Dagline;

/// <summary>
/// One reporter's own unbounded queue of events, and the loop that hands them to it in the order published, one call at a time.
/// A reporter that fails, or is busy when <see cref="DropIfBusy"/> is called, is dropped: it is handed nothing more,
/// the events still queued for it are let go, and its error is the result of <see cref="Delivered"/>.
/// </summary>
[SuppressMessage(
    "Reliability",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The source has no timer and no wait handle is ever taken from it, and a reporter call that was dropped may still hold its token.")]
internal sealed class ReporterQueue
{
    // Not a single reader: a drop lets go of the queued events from whichever thread asks for it, while the loop may be in a call.
    private readonly Channel<(RunEvent Event, Snapshot Snapshot)> _queue = Channel.CreateUnbounded<(RunEvent, Snapshot)>();

    private readonly Lock _gate = new();
    private readonly IReporter _reporter;
    private readonly CancellationTokenSource _dropped = new();
    private readonly TaskCompletionSource<ReporterError?> _delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Events published and not yet handled, the one in a call included: more than none is what makes the reporter busy.
    private int _unhandled;
    private RunEvent? _inCall;

    /// <param name="reporter">The reporter this queue hands events to.</param>
    public ReporterQueue(IReporter reporter)
    {
        _reporter = reporter;

        // The loop is not what Delivered waits on: a call that blocks its thread must not keep a drop from completing it.
        _ = Task.Run(DeliverAsync, CancellationToken.None);
    }

    /// <summary>
    /// Completes with <see langword="null"/> once <see cref="Complete"/> has been called and the reporter has handled every event,
    /// or with the reporter's error as soon as it fails or is dropped by <see cref="DropIfBusy"/>, without waiting for a call
    /// in progress to come back. It never faults.
    /// </summary>
    public Task<ReporterError?> Delivered => _delivered.Task;

    /// <summary>
    /// Queues an event and returns at once: the caller never waits for the reporter.
    /// An event published after the reporter was dropped is discarded.
    /// </summary>
    public void Publish(RunEvent runEvent, Snapshot snapshot)
    {
        lock (_gate)
        {
            if (_queue.Writer.TryWrite((runEvent, snapshot)))
            {
                _unhandled++;
            }
        }
    }

    public void Complete() => _queue.Writer.TryComplete();

    /// <summary>
    /// Drops the reporter if it has not handled every event published so far: the token passed to its calls is cancelled,
    /// its queued events are let go, and <see cref="Delivered"/> completes with an <see cref="OperationCanceledException"/>
    /// naming the event in the call, or the next event when no call is in progress. The token is cancelled before
    /// <see cref="Delivered"/> completes, and the callbacks registered on it run on the thread pool, not on the caller's thread.
    /// A reporter that is not busy is left alone, and its token is never cancelled.
    /// </summary>
    public void DropIfBusy()
    {
        lock (_gate)
        {
            if (_delivered.Task.IsCompleted || _unhandled == 0)
            {
                return;
            }

            var runEvent = _inCall ?? (_queue.Reader.TryPeek(out var next) ? next.Event : null);

            // Signals the token before Stop completes Delivered, so a run that has returned has always cancelled it,
            // while the reporter's callbacks run on the thread pool and never under the gate. A callback that throws is the
            // reporter's affair, and it is dropped regardless, so the fault is observed and let go.
            _ = _dropped.CancelAsync().ContinueWith(
                static cancelled => _ = cancelled.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            Stop(new ReporterError(_reporter, runEvent, new OperationCanceledException(_dropped.Token)));
        }
    }

    private async Task DeliverAsync()
    {
        // Read without a token: a drop closes the queue, which is what ends this loop.
        while (await _queue.Reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            (RunEvent Event, Snapshot Snapshot) next;
            lock (_gate)
            {
                if (_delivered.Task.IsCompleted || !_queue.Reader.TryRead(out next))
                {
                    continue;
                }

                _inCall = next.Event;
            }

            try
            {
                await _reporter.OnEventAsync(next.Event, next.Snapshot, _dropped.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // A reporter is consumer code, so whatever it throws is recorded and never reaches the run.
                // After a drop the error is already set, and this one, likely the cancellation, is let go.
                lock (_gate)
                {
                    Stop(new ReporterError(_reporter, next.Event, exception));
                }

                return;
            }

            lock (_gate)
            {
                _inCall = null;
                _unhandled--;
            }
        }

        _delivered.TrySetResult(null);
    }

    /// <summary>
    /// Drops the reporter with <paramref name="error"/>, unless it was already dropped. Closing the queue is what drops it:
    /// nothing published from here on is kept for it. The events it had not been handed yet are let go too: the queue lives
    /// as long as the run does, and a reporter that keeps the snapshot source keeps the run, so they would otherwise stay
    /// in memory with their snapshots. Called under the gate.
    /// </summary>
    private void Stop(ReporterError error)
    {
        if (!_delivered.TrySetResult(error))
        {
            return;
        }

        _queue.Writer.TryComplete();
        while (_queue.Reader.TryRead(out _))
        {
        }
    }
}
