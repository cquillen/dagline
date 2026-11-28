using System.Runtime.InteropServices;

namespace Dagline;

/// <summary>
/// The current snapshot of a run on the new engine: written by the run's loop alone, as it publishes each event, and read by anyone,
/// from any thread. <see cref="RunStarted"/> hands it to reporters as their source of snapshots, so a reporter reads the run and
/// never holds the run itself (ADR 0010, ADR 0016).
/// </summary>
/// <remarks>
/// <para>
/// A snapshot is made the current one before its event is handed to reporters, so a reporter never reads a current snapshot older
/// than an event it holds.
/// </para>
/// <para>
/// Progress is a sample, never posted to the run (ADR 0015), so a read lays the progress of each task running in the published
/// snapshot over it, as sampled at the read: progress shows on the next read. Nothing else is laid over it, a task's counts beyond
/// the kept included, which only the loop applies, so that a task's warning count and its group's always agree. A read that finds
/// no progress changed since the last read of the same published snapshot gives the same snapshot as that read.
/// </para>
/// </remarks>
/// <param name="samples">The tasks' samples, which their contexts write.</param>
internal sealed class CurrentSnapshot(TaskSamples samples) : ISnapshotSource
{
    private volatile Snapshot? _published;
    private volatile int[] _sampled = [];
    private volatile Laid? _lastRead;

    /// <summary>The published snapshot with the progress of its running tasks laid over it as it is now.</summary>
    public Snapshot Current
    {
        get
        {
            // The loop publishes the run as it is at the start before the run is handed to anyone, so a read always finds one.
            var published = _published!;
            var sampled = _sampled;
            if (sampled.Length == 0)
            {
                return published;
            }

            long versions = 0;
            foreach (var position in sampled)
            {
                versions += samples[position].Version;
            }

            if (_lastRead is { } last && ReferenceEquals(last.Published, published) && last.Versions == versions)
            {
                return last.Snapshot;
            }

            var read = LayProgressOver(published, sampled);
            _lastRead = new Laid(published, versions, read);
            return read;
        }
    }

    /// <summary>
    /// The snapshot as of the last event the loop published, or as of the last rebuild, with no progress laid over it since. For
    /// the tasks' contexts, which read only what progress never changes.
    /// </summary>
    public Snapshot Published => _published!;

    /// <summary>
    /// Makes <paramref name="snapshot"/> the current one, with <paramref name="sampled"/> the positions of the tasks running in it.
    /// For the loop alone.
    /// </summary>
    /// <remarks>
    /// The two are written apart, the positions first, so a read may pair a snapshot with the positions of the next. That is
    /// harmless: only a task running in the snapshot read is laid over it, and one that has stopped running keeps the progress the
    /// loop laid over it as it was published.
    /// </remarks>
    public void Publish(Snapshot snapshot, int[] sampled)
    {
        _sampled = sampled;
        _published = snapshot;
    }

    private Snapshot LayProgressOver(Snapshot published, int[] sampled)
    {
        List<(int Index, NodeSnapshot Element)>? laid = null;
        foreach (var position in sampled)
        {
            var node = published.Nodes[position];
            if (node.Status is not NodeStatus.Running)
            {
                continue;
            }

            var withProgress = node.WithProgress(samples[position].Read());
            if (!ReferenceEquals(withProgress, node))
            {
                (laid ??= []).Add((position, withProgress));
            }
        }

        return laid is null ? published : published.WithNodes(CollectionsMarshal.AsSpan(laid));
    }

    /// <summary>A read of the current snapshot: the published one it was laid over, the versions of the samples it read, and what it gave.</summary>
    private sealed record Laid(Snapshot Published, long Versions, Snapshot Snapshot);
}
