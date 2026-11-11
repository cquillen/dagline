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
    public ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _events.Add(runEvent);
            _snapshots.Add(snapshot);
        }

        return ValueTask.CompletedTask;
    }
}
