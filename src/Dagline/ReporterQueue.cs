using System.Threading.Channels;

namespace Dagline;

/// <summary>
/// One reporter's own unbounded queue of events, and the loop that hands them to it in the order published, one call at a time.
/// </summary>
internal sealed class ReporterQueue
{
    private readonly Channel<(RunEvent Event, Snapshot Snapshot)> _queue =
        Channel.CreateUnbounded<(RunEvent, Snapshot)>(new UnboundedChannelOptions { SingleReader = true });

    private readonly IReporter _reporter;

    public ReporterQueue(IReporter reporter, CancellationToken cancellationToken)
    {
        _reporter = reporter;
        Delivered = Task.Run(() => DeliverAsync(cancellationToken), cancellationToken);
    }

    /// <summary>Completes once <see cref="Complete"/> has been called and the reporter has handled every event.</summary>
    public Task Delivered { get; }

    /// <summary>Queues an event and returns at once: the caller never waits for the reporter.</summary>
    public void Publish(RunEvent runEvent, Snapshot snapshot) => _queue.Writer.TryWrite((runEvent, snapshot));

    public void Complete() => _queue.Writer.TryComplete();

    private async Task DeliverAsync(CancellationToken cancellationToken)
    {
        await foreach (var (runEvent, snapshot) in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await _reporter.OnEventAsync(runEvent, snapshot, cancellationToken).ConfigureAwait(false);
        }
    }
}
