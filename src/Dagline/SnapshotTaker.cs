using System.Collections.Frozen;

namespace Dagline;

/// <summary>
/// Takes a run's snapshots one after another, each just after the change it is given: the nodes of each are those of the one
/// before with the change's node changes applied, so changes are taken exactly once each, in the order they were made.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RunSteps"/> records each change to a node as it makes it, and closes the changes recorded so far into a
/// <see cref="SnapshotChange"/> with each event. A snapshot is built only when it is taken, so the changes a step has decided
/// cost one small entry each, not a list of every node each, until the step is carried out.
/// </para>
/// <para>
/// The nodes of each snapshot share every chunk but the changed ones with the snapshot before, as <see cref="ChunkedList{T}"/> allows.
/// It is not thread-safe: <see cref="RunSteps"/> owns it, and the run uses it under its lock.
/// </para>
/// </remarks>
/// <param name="atTheStart">Every node as it is when the run starts, in declaration order.</param>
/// <param name="positions">The position of each node among the graph's nodes, by path.</param>
/// <param name="slots">The run's slots, which give the size of the cap and of each limit.</param>
internal sealed class SnapshotTaker(IEnumerable<NodeSnapshot> atTheStart, FrozenDictionary<NodePath, int> positions, Slots slots)
{
    private readonly Queue<NodeChange> _untaken = new();
    private ChunkedList<NodeSnapshot> _nodes = ChunkedList<NodeSnapshot>.From(atTheStart);
    private int _recordedSinceLastChange;

    /// <summary>Records a node as it is after a change, for the next change to close.</summary>
    public void Record(int position, NodeSnapshot node)
    {
        _untaken.Enqueue(new NodeChange(position, node));
        _recordedSinceLastChange++;
    }

    /// <summary>
    /// Closes the node changes recorded since the last change into a new one, with everything else the snapshot holds.
    /// The counts and the slots in use are copied, so the change keeps them as they are now.
    /// </summary>
    public SnapshotChange Close(RunMilestones milestones, StatusCounts counts)
    {
        var tally = new int[StatusCounts.TallyLength + slots.TallyLength];
        counts.CopyTo(tally);
        slots.CopyTo(tally.AsSpan(StatusCounts.TallyLength));

        SnapshotChange change = new(_recordedSinceLastChange, milestones, tally);
        _recordedSinceLastChange = 0;
        return change;
    }

    /// <summary>The snapshot just after <paramref name="change"/>, which must be the oldest change not yet taken.</summary>
    public Snapshot Take(SnapshotChange change)
    {
        for (var taken = 0; taken < change.NodeChanges; taken++)
        {
            var (position, node) = _untaken.Dequeue();
            _nodes = _nodes.With(position, node);
        }

        var (started, stop, ended, outcome) = change.Milestones;
        var slotsInUse = change.Tally.AsSpan(StatusCounts.TallyLength);
        return new Snapshot(
            started,
            ended,
            outcome,
            stop,
            _nodes,
            positions,
            StatusCounts.ToDictionary(change.Tally),
            slots.CapIn(slotsInUse),
            slots.LimitsIn(slotsInUse));
    }
}
