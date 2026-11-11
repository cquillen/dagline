using System.Threading.Channels;

namespace Dagline;

/// <summary>
/// Hands a run's events to its reporters, in the order published and one call at a time,
/// on a thread that never holds the run's state.
/// </summary>
internal sealed class ReporterDelivery
{
    private readonly Channel<(RunEvent Event, Snapshot Snapshot)> _queue =
        Channel.CreateUnbounded<(RunEvent, Snapshot)>(new UnboundedChannelOptions { SingleReader = true });

    private readonly IReadOnlyList<IReporter> _reporters;

    public ReporterDelivery(IReadOnlyList<IReporter> reporters)
    {
        _reporters = reporters;

        // Nothing can stop a run or cut short the wait at its end, so no token is ever cancelled.
        Delivered = Task.Run(() => DeliverAsync(CancellationToken.None));
    }

    /// <summary>Completes once <see cref="Complete"/> has been called and every reporter has handled every event.</summary>
    public Task Delivered { get; }

    /// <summary>Queues an event and returns at once: the caller never waits for a reporter.</summary>
    public void Publish(RunEvent runEvent, Snapshot snapshot) => _queue.Writer.TryWrite((runEvent, snapshot));

    public void Complete() => _queue.Writer.Complete();

    private async Task DeliverAsync(CancellationToken cancellationToken)
    {
        await foreach (var (runEvent, snapshot) in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var reporter in _reporters)
            {
                await reporter.OnEventAsync(runEvent, snapshot, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
