namespace Dagline;

/// <summary>
/// Hands a run's events to its reporters through the run's host, on the new engine. Each reporter has a queue of its own, so
/// every reporter receives the same events in the same order, and one that is slow or throws holds up neither the loop nor another
/// reporter. A reporter that throws is dropped: it is handed nothing more, and its error is kept for the run's result.
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
        _reporters = [.. reporters.Select(reporter => new HostedReporter(reporter, host, Finished))];
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
    public void Complete(Action done)
    {
        _done = done;
        foreach (var reporter in _reporters)
        {
            reporter.Complete();
        }

        Finished();
    }

    private void Finished()
    {
        if (Interlocked.Decrement(ref _unfinished) == 0)
        {
            _done!();
        }
    }
}

/// <summary>
/// One reporter's queue of events on the new engine, and the delivery that hands them to it in the order published, one call at a
/// time. The delivery is scheduled through the host when an event finds none scheduled, and runs until the queue is empty.
/// </summary>
/// <param name="reporter">The reporter this queue hands events to.</param>
/// <param name="host">The run's host, which runs each delivery.</param>
/// <param name="finished">Called once, when the reporter has handled every event after the queue was completed, or was dropped.</param>
internal sealed class HostedReporter(IReporter reporter, IRunHost host, Action finished)
{
    private readonly Lock _gate = new();
    private readonly Queue<(RunEvent Event, Snapshot Snapshot)> _queue = new();

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
                // No token is cancelled as yet: dropping a reporter still busy when the reporter wait runs out is still to come.
                // On the context the host delivers on, so that a delivery under a host a test pumps stays on the pump.
                await reporter.OnEventAsync(next.Event, next.Snapshot, CancellationToken.None).ConfigureAwait(true);
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
            if (_queue.TryDequeue(out var next))
            {
                return next;
            }

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

    private void Drop(ReporterError error)
    {
        lock (_gate)
        {
            Error = error;
            _queue.Clear();
            _scheduled = false;
            if (_finished)
            {
                return;
            }

            _finished = true;
        }

        finished();
    }
}
