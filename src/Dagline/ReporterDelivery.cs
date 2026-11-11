namespace Dagline;

/// <summary>
/// Hands a run's events to its reporters. Each reporter has a queue of its own, so every reporter receives
/// the same events in the same order, and one that is slow or fails holds up neither the run nor another reporter.
/// </summary>
internal sealed class ReporterDelivery
{
    private readonly ReporterQueue[] _queues;

    public ReporterDelivery(IReadOnlyList<IReporter> reporters)
    {
        // Nothing can stop a run or cut short the wait at its end, so no token is ever cancelled.
        var cancellationToken = CancellationToken.None;
        _queues = [.. reporters.Select(reporter => new ReporterQueue(reporter, cancellationToken))];
        Delivered = CollectErrorsAsync(cancellationToken);
    }

    /// <summary>
    /// Completes once <see cref="Complete"/> has been called and every reporter that was not dropped has handled every event,
    /// with the error of each reporter that was dropped, in the order the reporters were given. It never faults.
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

    public void Complete()
    {
        foreach (var queue in _queues)
        {
            queue.Complete();
        }
    }

    private async Task<IReadOnlyList<ReporterError>> CollectErrorsAsync(CancellationToken cancellationToken)
    {
        var errors = await Task.WhenAll(_queues.Select(queue => queue.Delivered)).WaitAsync(cancellationToken).ConfigureAwait(false);
        return [.. errors.OfType<ReporterError>()];
    }
}
