using System.Collections.Frozen;
using System.Runtime.InteropServices;

namespace Dagline;

/// <summary>
/// Takes a run's snapshots one after another, each as the run is at that moment: the nodes of each are those of the one before
/// with the node changes recorded since applied.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RunSteps"/> records each change to a task as it makes it, <see cref="RollUp"/> hands over each changed group as a
/// finished node, and <see cref="RunSteps"/> takes a snapshot with each event it decides, and when the run as it is now is read.
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
    // Reused by each take, so taking a snapshot allocates no list of its own for the changes it applies.
    private readonly List<(int Index, NodeSnapshot Element)> _taking = [];

    // Reports that publish no event, such as progress, are part of no snapshot until the next take.
    // Keeping only the latest node at each position bounds what they hold to one entry per node, however often a task reports.
    // A task changed by a report is kept as its instance, whose node is built only when the snapshot is taken.
    private readonly Dictionary<int, object> _sinceLastTake = [];
    private readonly int[] _tally = new int[slots.TallyLength];
    private ChunkedList<NodeSnapshot> _nodes = ChunkedList<NodeSnapshot>.From(atTheStart);

    // Most events take or give back no slot, so their snapshots share what was built at the last one that did.
    private (int Version, LimitUse RunningTasks, IReadOnlyDictionary<string, LimitUse> Limits)? _slotsTaken;

    /// <summary>
    /// Records a node as it is after a change, for the next snapshot. A later record at the same position before
    /// that snapshot replaces this one.
    /// </summary>
    public void Record(int position, NodeSnapshot node) => _sinceLastTake[position] = node;

    /// <summary>
    /// Records that a task's instance has changed, for the next snapshot, which takes the task's node from the instance as
    /// it is then. A later record at the same position before that snapshot replaces this one.
    /// </summary>
    public void Record(int position, TaskInstance instance) => _sinceLastTake[position] = instance;

    /// <summary>The snapshot of the run as it is now: the one before with the node changes recorded since applied.</summary>
    /// <param name="milestones">When the run started, the drain and the stop it has applied, and when and how it ended.</param>
    /// <param name="runCounts">The run's counts as they are now.</param>
    public Snapshot Take(RunMilestones milestones, TaskCounts runCounts)
    {
        foreach (var (position, recorded) in _sinceLastTake)
        {
            _taking.Add((position, recorded as NodeSnapshot ?? ((TaskInstance)recorded).ToNodeSnapshot()));
        }

        _sinceLastTake.Clear();
        _nodes = _nodes.With(CollectionsMarshal.AsSpan(_taking));
        _taking.Clear();

        if (_slotsTaken is not { } slotsTaken || slotsTaken.Version != slots.Version)
        {
            slots.CopyTo(_tally);
            slotsTaken = (slots.Version, slots.RunningTasksIn(_tally), slots.LimitsIn(_tally));
            _slotsTaken = slotsTaken;
        }

        var (started, drain, stop, ended, outcome) = milestones;
        return new Snapshot(
            started,
            ended,
            outcome,
            drain,
            stop,
            _nodes,
            positions,
            runCounts,
            slotsTaken.RunningTasks,
            slotsTaken.Limits,
            journal);
    }
}

/// <summary>
/// What a run's snapshots say about its journal. It is fixed when the run starts, so it is given to the
/// <see cref="SnapshotTaker"/> once.
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

/// <summary>
/// When a run started, the drain and the stop it has applied, and when and how it ended, as a snapshot shows them. Each changes
/// at most once, so the snapshots between two milestones are taken from one of these.
/// </summary>
/// <param name="Started">When the run started.</param>
/// <param name="Drain">The drain the run has applied, or <see langword="null"/> while none has been.</param>
/// <param name="Stop">The stop the run has applied, or <see langword="null"/> while none has been.</param>
/// <param name="Ended">When the run ended, or <see langword="null"/> while it has not.</param>
/// <param name="Outcome">How the run ended, or <see langword="null"/> while it has not.</param>
internal sealed record RunMilestones(DateTimeOffset Started, DrainState? Drain, StopState? Stop, DateTimeOffset? Ended, RunOutcome? Outcome);
