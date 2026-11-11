namespace Dagline;

/// <summary>
/// Hands a run's events to its reporters. Each reporter has a queue of its own, so every reporter receives
/// the same events in the same order, and one that is slow holds up neither the run nor another reporter.
/// </summary>
internal sealed class ReporterDelivery
{
    private readonly ReporterQueue[] _queues;

    public ReporterDelivery(IReadOnlyList<IReporter> reporters)
    {
        // Nothing can stop a run or cut short the wait at its end, so no token is ever cancelled.
        _queues = [.. reporters.Select(reporter => new ReporterQueue(reporter, CancellationToken.None))];
        Delivered = Task.WhenAll(_queues.Select(queue => queue.Delivered));
    }

    /// <summary>Completes once <see cref="Complete"/> has been called and every reporter has handled every event.</summary>
    public Task Delivered { get; }

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
}
