using System.Collections.Frozen;
using System.Runtime.InteropServices;

namespace Dagline;

/// <summary>
/// Takes a run's snapshots one after another, each just after the change it is given: the nodes of each are those of the one
/// before with the change's node changes applied, so changes are taken exactly once each, in the order they were made.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RunSteps"/> records each change to a task as it makes it, <see cref="RollUp"/> hands over each changed group as a
/// finished node, and <see cref="RunSteps"/> closes the changes recorded so far into a
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
/// <param name="slots">The run's slots, which give the size of the max running tasks and of each limit.</param>
/// <param name="journal">What every snapshot of the run says about its journal, which is fixed for the run.</param>
internal sealed class SnapshotTaker(
    IEnumerable<NodeSnapshot> atTheStart,
    FrozenDictionary<NodePath, int> positions,
    Slots slots,
    SnapshotJournal journal)
{
    private readonly Queue<NodeChange> _untaken = new();

    // Reused by each take, so taking a snapshot allocates no list of its own for the changes it applies.
    private readonly List<(int Index, NodeSnapshot Element)> _taking = [];

    // Reports that publish no event, such as progress, close no change until the next event or a read of the current snapshot.
    // Keeping only the latest node at each position bounds what they hold to one entry per node, however often a task reports.
    // A task changed by a report is kept as its instance, whose node is built only when the change is closed.
    private readonly Dictionary<int, object> _sinceLastChange = [];
    private ChunkedList<NodeSnapshot> _nodes = ChunkedList<NodeSnapshot>.From(atTheStart);

    // Most events take or give back no slot, so their changes share the last tally, and their snapshots what was built from it.
    private int[]? _tally;
    private int _tallyVersion;
    private (int[] Tally, LimitUse RunningTasks, IReadOnlyDictionary<string, LimitUse> Limits)? _slotsTaken;

    /// <summary>
    /// Records a node as it is after a change, for the next change to close. A later record at the same position before
    /// that close replaces this one.
    /// </summary>
    public void Record(int position, NodeSnapshot node) => _sinceLastChange[position] = node;

    /// <summary>
    /// Records that a task's instance has changed, for the next change to close, which takes the task's node from the instance as
    /// it is then. A later record at the same position before that close replaces this one.
    /// </summary>
    public void Record(int position, TaskInstance instance) => _sinceLastChange[position] = instance;

    /// <summary>
    /// Closes the node changes recorded since the last change into a new one, with everything else the snapshot holds.
    /// The slots in use are copied, so the change keeps them as they are now, unless no slot has been taken or given back since
    /// the last change, which it then shares a copy with.
    /// </summary>
    /// <param name="milestones">When the run started, the drain and the stop it has applied, and when and how it ended.</param>
    /// <param name="runCounts">The run's counts as they are now.</param>
    public SnapshotChange Close(RunMilestones milestones, TaskCounts runCounts)
    {
        if (_tally is null || _tallyVersion != slots.Version)
        {
            _tally = new int[slots.TallyLength];
            slots.CopyTo(_tally);
            _tallyVersion = slots.Version;
        }

        var slotsInUse = _tally;

        foreach (var (position, recorded) in _sinceLastChange)
        {
            var node = recorded as NodeSnapshot ?? ((TaskInstance)recorded).ToNodeSnapshot();
            _untaken.Enqueue(new NodeChange(position, node));
        }

        SnapshotChange change = new(_sinceLastChange.Count, milestones, runCounts, slotsInUse);
        _sinceLastChange.Clear();
        return change;
    }

    /// <summary>The snapshot just after <paramref name="change"/>, which must be the oldest change not yet taken.</summary>
    public Snapshot Take(SnapshotChange change)
    {
        for (var taken = 0; taken < change.NodeChanges; taken++)
        {
            var (position, node) = _untaken.Dequeue();
            _taking.Add((position, node));
        }

        _nodes = _nodes.With(CollectionsMarshal.AsSpan(_taking));
        _taking.Clear();

        if (_slotsTaken is not { } slotsTaken || !ReferenceEquals(slotsTaken.Tally, change.SlotsInUse))
        {
            slotsTaken = (change.SlotsInUse, slots.RunningTasksIn(change.SlotsInUse), slots.LimitsIn(change.SlotsInUse));
            _slotsTaken = slotsTaken;
        }

        var (started, drain, stop, ended, outcome) = change.Milestones;
        return new Snapshot(
            started,
            ended,
            outcome,
            drain,
            stop,
            _nodes,
            positions,
            change.RunCounts,
            slotsTaken.RunningTasks,
            slotsTaken.Limits,
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
