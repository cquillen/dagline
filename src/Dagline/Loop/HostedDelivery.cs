using System.Diagnostics.CodeAnalysis;

namespace Dagline;

/// <summary>
/// Hands a run's events to its reporters through the run's host. Each reporter has a queue of its own, so
/// every reporter receives the same events in the same order, and one that is slow or throws holds up neither the loop nor another
/// reporter. A reporter that throws, or that is still busy when the run drops the busy ones at the end of the reporter wait, is
/// dropped: it is handed nothing more, its token is cancelled, and its error is kept for the run's result.
/// </summary>
/// <remarks>
/// The loop alone publishes and completes. Once it has completed, the delivery says when every reporter has handled every event
/// or been dropped, through the callback it was given, which posts that to the loop's inbox: the loop awaits nothing but its inbox.
/// </remarks>
internal sealed class HostedDelivery
{
    private readonly HostedReporter[] _reporters;

    // One for each reporter that has not finished, and one for the completion itself, so that no reporter that finishes early
    // can say every reporter is done before the loop has completed the delivery.
    private int _unfinished;
    private Action? _done;

    public HostedDelivery(IReadOnlyList<IReporter> reporters, IRunHost host)
    {
        _reporters = [.. reporters.Select(reporter => new HostedReporter(reporter, host, () => Finished()))];
        _unfinished = _reporters.Length + 1;
    }

    /// <summary>
    /// The error of each reporter that was dropped, in the order the reporters were given. Read it once the delivery has said that
    /// every reporter is done.
    /// </summary>
    public IReadOnlyList<ReporterError> Errors => [.. _reporters.Select(reporter => reporter.Error).OfType<ReporterError>()];

    /// <summary>Queues an event for every reporter and returns at once: the loop never waits for a reporter.</summary>
    public void Publish(RunEvent runEvent, Snapshot snapshot)
    {
        foreach (var reporter in _reporters)
        {
            reporter.Publish(runEvent, snapshot);
        }
    }

    /// <summary>
    /// Says that no event will follow, and calls <paramref name="done"/>, once, when every reporter has handled every event or has
    /// been dropped: at once, when every reporter already has.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when every reporter already had, and <paramref name="done"/> was called within this call: there is
    /// nothing to wait for.
    /// </returns>
    public bool Complete(Action done)
    {
        _done = done;
        foreach (var reporter in _reporters)
        {
            reporter.Complete();
        }

        return Finished();
    }

    /// <summary>
    /// Drops every reporter that has not handled every event: the reporter wait has run out. Each is dropped as
    /// <see cref="HostedReporter.DropIfBusy"/> says, and the delivery then says every reporter is done, without waiting for a call
    /// still in progress. A reporter that had handled every event is left alone.
    /// </summary>
    /// <param name="cause">
    /// Why the wait ran out before its time, kept as the inner exception of each dropped reporter's error, or
    /// <see langword="null"/> when it ran its length.
    /// </param>
    public void DropTheBusy(Exception? cause)
    {
        foreach (var reporter in _reporters)
        {
            reporter.DropIfBusy(cause);
        }
    }

    private bool Finished()
    {
        if (Interlocked.Decrement(ref _unfinished) != 0)
        {
            return false;
        }

        _done!();
        return true;
    }
}

/// <summary>
/// One reporter's queue of events, and the delivery that hands them to it in the order published, one call at a
/// time. The delivery is scheduled through the host when an event finds none scheduled, and runs until the queue is empty.
/// Every call is handed the same token, which is cancelled only when the reporter is dropped: when it throws, or when it is still
/// busy as the reporter wait runs out.
/// </summary>
/// <param name="reporter">The reporter this queue hands events to.</param>
/// <param name="host">The run's host, which runs each delivery.</param>
/// <param name="finished">Called once, when the reporter has handled every event after the queue was completed, or was dropped.</param>
[SuppressMessage(
    "Reliability",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The source has no timer and no wait handle is ever taken from it, and a reporter call that was dropped may still hold its token.")]
internal sealed class HostedReporter(IReporter reporter, IRunHost host, Action finished)
{
    private readonly Lock _gate = new();
    private readonly Queue<(RunEvent Event, Snapshot Snapshot)> _queue = new();
    private readonly CancellationTokenSource _dropped = new();

    private RunEvent? _inCall;

    private bool _scheduled;
    private bool _completed;
    private bool _finished;

    /// <summary>The reporter's error once it was dropped, or <see langword="null"/>.</summary>
    public ReporterError? Error { get; private set; }

    /// <summary>Queues an event, and schedules a delivery unless one is under way. An event for a reporter that was dropped is let go.</summary>
    public void Publish(RunEvent runEvent, Snapshot snapshot)
    {
        lock (_gate)
        {
            if (_finished)
            {
                return;
            }

            _queue.Enqueue((runEvent, snapshot));
            if (_scheduled)
            {
                return;
            }

            _scheduled = true;
        }

        host.DeliverToReporter(DeliverAsync);
    }

    /// <summary>Says that no event will follow: the reporter finishes once it has handled those queued, or at once when it has.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            if (_scheduled || _finished)
            {
                return;
            }

            _finished = true;
        }

        finished();
    }

    private async Task DeliverAsync()
    {
        while (TakeNext() is { } next)
        {
            try
            {
                // On the context the host delivers on, so that a delivery under a host a test pumps stays on the pump.
                await reporter.OnEventAsync(next.Event, next.Snapshot, _dropped.Token).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                // A reporter is consumer code, so whatever it throws is recorded and never reaches the run.
                Drop(new ReporterError(reporter, next.Event, exception));
                return;
            }
        }
    }

    /// <summary>The next event to hand over, or <see langword="null"/> once the queue is empty, which ends the delivery.</summary>
    private (RunEvent Event, Snapshot Snapshot)? TakeNext()
    {
        lock (_gate)
        {
            if (!_finished && _queue.TryDequeue(out var next))
            {
                _inCall = next.Event;
                return next;
            }

            _inCall = null;
            _scheduled = false;
            if (!_completed || _finished)
            {
                return null;
            }

            _finished = true;
        }

        finished();
        return null;
    }

    /// <summary>
    /// Drops the reporter if it has not handled every event: its error is an <see cref="OperationCanceledException"/> naming the
    /// event in the call, or the next event when no call is under way, with <paramref name="cause"/> as its inner exception, its
    /// queued events are let go, and its token is cancelled,
    /// through the host, before it is said to be finished, so a run that has returned has always cancelled it. A call under way is
    /// not waited for, and whatever it does once it comes back changes nothing. A reporter that is not busy is left alone.
    /// </summary>
    public void DropIfBusy(Exception? cause)
    {
        lock (_gate)
        {
            if (_finished)
            {
                return;
            }

            var runEvent = _inCall ?? (_queue.TryPeek(out var next) ? next.Event : null);
            LetGo(new ReporterError(reporter, runEvent, Cancelled(cause)));
        }

        SignalAndFinish();
    }

    private OperationCanceledException Cancelled(Exception? cause)
        => cause is null
            ? new OperationCanceledException(_dropped.Token)
            : new OperationCanceledException("The reporter wait ran out at once: the run's clock refused to make its timer.", cause, _dropped.Token);

    private void Drop(ReporterError error)
    {
        lock (_gate)
        {
            if (_finished)
            {
                // Dropped already, at the end of the reporter wait: that error stands, and this one, likely the cancellation, is let go.
                return;
            }

            LetGo(error);
        }

        SignalAndFinish();
    }

    /// <summary>
    /// Cancels the dropped reporter's token, through the host, then says it is finished, so a run that has returned has always
    /// cancelled it. Outside the gate: the host may run the token's callbacks, which are the reporter's code, on this thread.
    /// </summary>
    private void SignalAndFinish()
    {
        host.Signal(_dropped);
        finished();
    }

    /// <summary>Keeps the reporter's error and lets go of its queued events: it is finished. Called under the gate.</summary>
    private void LetGo(ReporterError error)
    {
        Error = error;
        _queue.Clear();
        _inCall = null;
        _finished = true;
    }
}
