namespace Dagline.TestSupport;

/// <summary>
/// A reporter that keeps every event it is handed, with the snapshot handed with it, in the order received.
/// A test runs a graph with one of these and asserts on what it recorded once the run has ended.
/// </summary>
public sealed class RecordingReporter : IReporter
{
    private readonly Lock _gate = new();
    private readonly List<RunEvent> _events = [];
    private readonly List<Snapshot> _snapshots = [];
    private readonly TaskCompletionSource<ISnapshotSource> _source = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes once the reporter has been handed <see cref="RunStarted"/>, with the snapshot source that event carries.
    /// A test reads <see cref="ISnapshotSource.Current"/> from it while task bodies are paused.
    /// </summary>
    public Task<ISnapshotSource> Source => _source.Task;

    /// <summary>
    /// Completes once the reporter has been handed <see cref="RunEnded"/>, the last event of a run.
    /// A test awaits it to know the run has ended while <see cref="Runner.RunAsync"/> has not yet returned.
    /// </summary>
    public Task Ended => _ended.Task;

    /// <summary>The events received so far, in the order received.</summary>
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

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="runEvent"/> or <paramref name="snapshot"/> is <see langword="null"/>.</exception>
    public ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runEvent);
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_gate)
        {
            _events.Add(runEvent);
            _snapshots.Add(snapshot);
        }

        if (runEvent is RunStarted started)
        {
            _source.TrySetResult(started.Source);
        }

        if (runEvent is RunEnded)
        {
            _ended.TrySetResult();
        }

        return ValueTask.CompletedTask;
    }
}
