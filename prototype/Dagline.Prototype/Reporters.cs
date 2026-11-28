using System.Collections.Concurrent;

namespace Dagline.Prototype;

/// <summary>
/// Delivery of a run's events to its reporters. Each reporter has its own queue and receives events one at a time, in order.
/// The host pumps a queue: on the thread pool in an app, on the test's thread under a manual host.
/// </summary>
internal sealed class Reporters
{
    private readonly ReporterPump[] _pumps;
    private int _stillDelivering;
    private Action? _whenDone;

    public Reporters(IReadOnlyList<IReporter> reporters, IRunHost host)
    {
        _pumps = [.. reporters.Select(reporter => new ReporterPump(reporter, host, PumpDone))];
        _stillDelivering = _pumps.Length;
    }

    public void Publish(RunEvent runEvent, Snapshot snapshot)
    {
        foreach (var pump in _pumps)
        {
            pump.Publish(runEvent, snapshot);
        }
    }

    /// <summary>No more events will come. <paramref name="whenDone"/> is called once every reporter has handled its queue or been dropped.</summary>
    public void Complete(Action whenDone)
    {
        _whenDone = whenDone;
        if (_pumps.Length == 0)
        {
            whenDone();
            return;
        }

        foreach (var pump in _pumps)
        {
            pump.Complete();
        }
    }

    /// <summary>Drops every reporter still busy: the reporter wait ran out.</summary>
    public void DropTheBusy()
    {
        foreach (var pump in _pumps)
        {
            pump.DropIfBusy();
        }
    }

    public IReadOnlyList<ReporterError> Errors() => [.. _pumps.Select(pump => pump.Error).OfType<ReporterError>()];

    private void PumpDone()
    {
        if (Interlocked.Decrement(ref _stillDelivering) == 0)
        {
            _whenDone?.Invoke();
        }
    }
}

internal sealed class ReporterPump(IReporter reporter, IRunHost host, Action whenDone)
{
    private readonly ConcurrentQueue<(RunEvent Event, Snapshot Snapshot)> _queue = new();
    private readonly CancellationTokenSource _dropped = new();
    private readonly Lock _gate = new();
    private int _scheduled;
    private int _unhandled;
    private bool _completing;
    private bool _done;
    private RunEvent? _inCall;

    public ReporterError? Error { get; private set; }

    public void Publish(RunEvent runEvent, Snapshot snapshot)
    {
        if (Error is not null)
        {
            return;
        }

        Interlocked.Increment(ref _unhandled);
        _queue.Enqueue((runEvent, snapshot));
        Schedule();
    }

    public void Complete()
    {
        lock (_gate)
        {
            _completing = true;
        }

        DoneIfHandled();
    }

    public void DropIfBusy()
    {
        lock (_gate)
        {
            if (_done || Volatile.Read(ref _unhandled) == 0)
            {
                return;
            }

            var runEvent = _inCall ?? (_queue.TryPeek(out var next) ? next.Event : null);
            Error = new ReporterError(reporter, runEvent, new OperationCanceledException(_dropped.Token));
            _done = true;
        }

        host.SignalTasks(_dropped);
        whenDone();
    }

    private void Schedule()
    {
        if (Interlocked.CompareExchange(ref _scheduled, 1, 0) == 0)
        {
            host.PumpReporter(DeliverAsync);
        }
    }

    private async Task DeliverAsync()
    {
        while (true)
        {
            while (Error is null && _queue.TryDequeue(out var next))
            {
                _inCall = next.Event;
                try
                {
                    // No ConfigureAwait(false): on the pool there is no context to capture, and under a manual host the
                    // continuation must come back to the host's queue.
                    await reporter.OnEventAsync(next.Event, next.Snapshot, _dropped.Token);
                }
                catch (Exception thrown)
                {
                    Fail(new ReporterError(reporter, next.Event, thrown));
                    return;
                }

                _inCall = null;
                Interlocked.Decrement(ref _unhandled);
            }

            Volatile.Write(ref _scheduled, 0);
            if (Error is not null || _queue.IsEmpty || Interlocked.CompareExchange(ref _scheduled, 1, 0) != 0)
            {
                break;
            }
        }

        DoneIfHandled();
    }

    private void Fail(ReporterError error)
    {
        lock (_gate)
        {
            if (_done)
            {
                return;
            }

            Error = error;
            _done = true;
        }

        whenDone();
    }

    private void DoneIfHandled()
    {
        lock (_gate)
        {
            if (_done || !_completing || Volatile.Read(ref _unhandled) != 0)
            {
                return;
            }

            _done = true;
        }

        whenDone();
    }
}
