using System.Threading.Channels;

namespace Dagline;

/// <summary>
/// One reporter's own unbounded queue of events, and the loop that hands them to it in the order published, one call at a time.
/// A reporter that fails is dropped: it is handed nothing more, the events still queued for it are let go,
/// and its error is the result of <see cref="Delivered"/>.
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

    /// <summary>
    /// Completes with <see langword="null"/> once <see cref="Complete"/> has been called and the reporter has handled every event,
    /// or with the reporter's error as soon as it fails. It never faults.
    /// </summary>
    public Task<ReporterError?> Delivered { get; }

    /// <summary>
    /// Queues an event and returns at once: the caller never waits for the reporter.
    /// An event published after the reporter was dropped is discarded.
    /// </summary>
    public void Publish(RunEvent runEvent, Snapshot snapshot) => _queue.Writer.TryWrite((runEvent, snapshot));

    public void Complete() => _queue.Writer.TryComplete();

    private async Task<ReporterError?> DeliverAsync(CancellationToken cancellationToken)
    {
        await foreach (var (runEvent, snapshot) in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await _reporter.OnEventAsync(runEvent, snapshot, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // A reporter is consumer code, so whatever it throws is recorded and never reaches the run.
                // Closing the queue is what drops it: nothing published from here on is kept for it.
                Complete();
                Discard();
                return new ReporterError(_reporter, runEvent, exception);
            }
        }

        return null;
    }

    /// <summary>
    /// Lets go of the events a dropped reporter had not been handed yet. The queue lives as long as the run does,
    /// and a reporter that keeps the snapshot source keeps the run, so they would otherwise stay in memory with their snapshots.
    /// </summary>
    private void Discard()
    {
        while (_queue.Reader.TryRead(out _))
        {
        }
    }
}
