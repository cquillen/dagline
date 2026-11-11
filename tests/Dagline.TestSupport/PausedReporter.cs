namespace Dagline.TestSupport;

/// <summary>
/// A reporter that pauses inside <see cref="OnEventAsync"/> until the test releases it,
/// so that a test can let a run move on while this reporter falls behind, without sleeping.
/// It keeps every event it is handed, with the snapshot handed with it, in the order received.
/// </summary>
public sealed class PausedReporter : IReporter
{
    private readonly Lock _gate = new();
    private readonly List<RunEvent> _events = [];
    private readonly List<Snapshot> _snapshots = [];
    private readonly TaskCompletionSource _paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes once the reporter has been handed its first event and is holding the call open.
    /// It stays inside that call until <see cref="Release"/>.
    /// </summary>
    public Task Paused => _paused.Task;

    /// <summary>The events received so far, in the order received, including the one the reporter is paused in.</summary>
    public IReadOnlyList<RunEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    /// <summary>The snapshot handed with each event: <c>Snapshots[i]</c> came with <c>Events[i]</c>.</summary>
    public IReadOnlyList<Snapshot> Snapshots
    {
        get
        {
            lock (_gate)
            {
                return [.. _snapshots];
            }
        }
    }

    /// <summary>Each event received so far as one line of text, as <see cref="EventLine.Describe"/> writes it.</summary>
    public IReadOnlyList<string> Lines => [.. Events.Select(EventLine.Describe)];

    /// <summary>Lets the held call return. Every later call returns as soon as it has recorded its event.</summary>
    public void Release() => _released.TrySetResult();

    /// <inheritdoc />
    public async ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _events.Add(runEvent);
            _snapshots.Add(snapshot);
        }

        _paused.TrySetResult();
        await _released.Task.WaitAsync(cancellationToken);
    }
}
