namespace Dagline;

/// <summary>
/// Hands a run's events to its reporters. Each reporter has a queue of its own, so every reporter receives
/// the same events in the same order, and one that is slow or fails holds up neither the run nor another reporter.
/// The app's token, cancelled once the run has ended without applying a stop, cuts short the wait for reporters still busy.
/// </summary>
internal sealed class ReporterDelivery
{
    private readonly ReporterQueue[] _queues;

    public ReporterDelivery(IReadOnlyList<IReporter> reporters)
    {
        _queues = [.. reporters.Select(reporter => new ReporterQueue(reporter))];
        Delivered = CollectErrorsAsync();
    }

    /// <summary>
    /// Completes once <see cref="Complete"/> has been called and every reporter that was not dropped has handled every event,
    /// with the error of each reporter that was dropped, in the order the reporters were given. Once
    /// <see cref="ListenToTheStop"/> has been called, the app's token cancelled drops every reporter still busy, so it completes
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
    /// Begins the wait for reporters at the end of a run that applied no stop: from now on, the app's token cancelled
    /// drops every reporter still busy, including a token already cancelled that the run did not hear before it ended. The run
    /// calls it as it ends, once it has published <see cref="RunEnded"/>, so a cancellation is either a stop the run applied or one
    /// that ends this wait. Listening only once the last event is queued means a cancellation is never heard while every reporter
    /// has handled every event so far but one more is still to come.
    /// </summary>
    /// <param name="appToken">The app's token, which <see cref="Runner.RunAsync"/> was given.</param>
    public void ListenToTheStop(CancellationToken appToken)
    {
        if (!appToken.CanBeCanceled)
        {
            return;
        }

        var listening = appToken.OnCancelled(DropTheBusyReporters);

        // The token outlives the run, so the registration goes once every reporter is done, lest the token keep them.
        _ = Delivered.ContinueWith(
            static (_, registration) => ((CancellationTokenRegistration)registration!).Unregister(),
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
