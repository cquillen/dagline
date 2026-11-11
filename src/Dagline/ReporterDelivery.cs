namespace Dagline;

/// <summary>
/// Hands a run's events to its reporters. Each reporter has a queue of its own, so every reporter receives
/// the same events in the same order, and one that is slow or fails holds up neither the run nor another reporter.
/// A stop request the run did not apply cuts short the wait for reporters still busy at the end of the run.
/// </summary>

internal sealed class ReporterDelivery
{
    private readonly ReporterQueue[] _queues;
    private readonly StopSignal? _signal;


    public ReporterDelivery(IReadOnlyList<IReporter> reporters, StopSignal? signal)
    {
        _signal = signal;
        _queues = [.. reporters.Select(reporter => new ReporterQueue(reporter))];
        Delivered = CollectErrorsAsync();
    }

    /// <summary>
    /// Completes once <see cref="Complete"/> has been called and every reporter that was not dropped has handled every event,
    /// with the error of each reporter that was dropped, in the order the reporters were given. A stop request the run
    /// did not apply drops every reporter still busy once <see cref="ListenBeyond"/> has been called, so it completes
    /// without waiting for their calls. It never faults.
    /// </summary>
    public Task<IReadOnlyList<ReporterError>> Delivered { get; }

    /// <summary>Queues an event for every reporter and returns at once: the caller never waits for a reporter.</summary>
    public void Publish(RunEvent runEvent, Snapshot snapshot)
    {
        foreach (var queue in _queues)
        {
            queue.Publish(runEvent, snapshot);
        }
    }

    /// <summary>
    /// Begins the wait for reporters at the end of the run: from now on, any request on the signal beyond the
    /// <paramref name="requestsApplied"/> the run applied drops every reporter still busy, including a request already made.
    /// The run calls it as it ends, once it has published <see cref="RunEnded"/> and with the count of requests it applied,
    /// so a request is either one the run applied or one that ends this wait. Listening only once the last event is queued
    /// means a request is never heard while every reporter has handled every event so far but one more is still to come:
    /// such a request, made before this call, is heard by the call the signal makes at once for a request already made.
    /// </summary>
    public void ListenBeyond(int requestsApplied)
    {
        if (_signal is not { } signal)
        {
            return;
        }

        var listening = signal.OnRequest(() =>
        {
            if (signal.Requests > requestsApplied)
            {
                DropTheBusyReporters();
            }
        });

        // The signal outlives the run, so the registration goes once every reporter is done, lest the signal keep them.
        _ = Delivered.ContinueWith(
            static (_, registration) => ((IDisposable)registration!).Dispose(),
            listening,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Says that no event will follow.</summary>
    public void Complete()
    {
        foreach (var queue in _queues)
        {
            queue.Complete();
        }
    }

    private void DropTheBusyReporters()
    {
        foreach (var queue in _queues)
        {
            queue.DropIfBusy();
        }
    }

    private async Task<IReadOnlyList<ReporterError>> CollectErrorsAsync()
    {
        var errors = await Task.WhenAll(_queues.Select(queue => queue.Delivered)).ConfigureAwait(false);
        return [.. errors.OfType<ReporterError>()];
    }
}
