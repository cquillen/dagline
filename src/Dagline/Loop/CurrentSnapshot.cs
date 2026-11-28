namespace Dagline;

/// <summary>
/// The current snapshot of a run on the new engine: written by the run's loop alone, as it publishes each event, and read by anyone,
/// from any thread. <see cref="RunStarted"/> hands it to reporters as their source of snapshots, so a reporter reads the run and
/// never holds the run itself (ADR 0010, ADR 0016).
/// </summary>
/// <remarks>
/// A snapshot is made the current one before its event is handed to reporters, so a reporter never reads a current snapshot older
/// than an event it holds.
/// </remarks>
internal sealed class CurrentSnapshot : ISnapshotSource
{
    private volatile Snapshot? _published;

    // The loop publishes the run as it is at the start before the run is handed to anyone, so a read always finds a snapshot.
    public Snapshot Current => _published!;

    /// <summary>Makes <paramref name="snapshot"/> the current one. For the loop alone.</summary>
    public void Publish(Snapshot snapshot) => _published = snapshot;
}
