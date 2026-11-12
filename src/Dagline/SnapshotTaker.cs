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
/// cost one small entry for each node an event changed, not a list of every node each, until the step is carried out.
/// </para>
/// <para>
/// The nodes of each snapshot share every chunk but the changed ones with the snapshot before, as <see cref="ChunkedList{T}"/> allows.
/// It is not thread-safe: <see cref="RunSteps"/> owns it, and the run uses it under its lock.
/// </para>
/// </remarks>
/// <param name="atTheStart">Every node as it is when the run starts, in declaration order.</param>
/// <param name="positions">The position of each node among the graph's nodes, by path.</param>
/// <param name="slots">The run's slots, which give the size of the cap and of each limit.</param>
/// <param name="journal">What every snapshot of the run says about its journal, which is fixed for the run.</param>
internal sealed class SnapshotTaker(
    IEnumerable<NodeSnapshot> atTheStart,
    FrozenDictionary<NodePath, int> positions,
    Slots slots,
    SnapshotJournal journal)
{
    private readonly Queue<NodeChange> _untaken = new();

    // Reports that publish no event, such as progress, close no change until the next event or a read of the current snapshot.
    // Keeping only the latest node at each position bounds what they hold to one entry per node, however often a task reports.
    private readonly Dictionary<int, NodeSnapshot> _sinceLastChange = [];
    private ChunkedList<NodeSnapshot> _nodes = ChunkedList<NodeSnapshot>.From(atTheStart);

    /// <summary>
    /// Records a node as it is after a change, for the next change to close. A later record at the same position before
    /// that close replaces this one.
    /// </summary>
    public void Record(int position, NodeSnapshot node) => _sinceLastChange[position] = node;

    /// <summary>
    /// Closes the node changes recorded since the last change into a new one, with everything else the snapshot holds.
    /// The counts and the slots in use are copied, so the change keeps them as they are now.
    /// </summary>
    public SnapshotChange Close(RunMilestones milestones, StatusCounts counts)
    {
        var tally = new int[TaskCounts.TallyLength + slots.TallyLength];
        counts.CopyTo(tally);
        slots.CopyTo(tally.AsSpan(TaskCounts.TallyLength));

        foreach (var (position, node) in _sinceLastChange)
        {
            _untaken.Enqueue(new NodeChange(position, node));
        }

        SnapshotChange change = new(_sinceLastChange.Count, milestones, tally);
        _sinceLastChange.Clear();
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
        var slotsInUse = change.Tally.AsSpan(TaskCounts.TallyLength);
        return new Snapshot(
            started,
            ended,
            outcome,
            stop,
            _nodes,
            positions,
            TaskCounts.From(change.Tally),
            slots.CapIn(slotsInUse),
            slots.LimitsIn(slotsInUse),
            journal);
    }
}

/// <summary>
/// What a run's snapshots say about its journal. It is fixed when the run starts, so it is given to the
/// <see cref="SnapshotTaker"/> once and not carried on each <see cref="SnapshotChange"/>.
/// </summary>
/// <param name="Info">The journal the run is on, or <see langword="null"/> for a run with no journal.</param>
internal sealed record SnapshotJournal(JournalInfo? Info)
{
    /// <summary>The paths of the earlier successes the run set aside, in declaration order.</summary>
    public IReadOnlyList<NodePath> SetAside { get; init; } = [];

    /// <summary>How many journal paths the run did not find in its graph.</summary>
    public int JournalPathsNotInGraph { get; init; }

    /// <summary>How many of the journal paths not in the graph have not succeeded.</summary>
    public int JournalPathsNotInGraphUnsucceeded { get; init; }
}
