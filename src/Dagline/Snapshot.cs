using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace Dagline;

/// <summary>
/// Everything Dagline knows about a run at one instant: every node of the graph with its status and the data attached to it.
/// </summary>
/// <remarks>
/// A snapshot is immutable and consistent: it never changes after it is made, and everything in it is as of the same instant.
/// It is safe to read from any thread and to keep after the run has ended.
/// </remarks>
public sealed class Snapshot
{
    private readonly FrozenDictionary<NodePath, int> _positions;
    private IReadOnlyList<NodeSnapshot>? _failed;
    private IReadOnlyList<NodeSnapshot>? _cancelled;
    private IReadOnlyList<NodeSnapshot>? _neverReached;
    private IReadOnlyList<NodeSnapshot>? _waiting;
    private IReadOnlyList<NodeSnapshot>? _working;
    private IReadOnlyList<BlockedTasks>? _blocked;
    private FrozenDictionary<NodePath, BlockedTasks>? _blockedByCause;
    private IReadOnlyList<NodeSnapshot>? _withWarnings;

    internal Snapshot(
        DateTimeOffset started,
        DateTimeOffset? ended,
        RunOutcome? outcome,
        StopState? stop,
        ChunkedList<NodeSnapshot> nodes,
        FrozenDictionary<NodePath, int> positions,
        TaskCounts counts,
        LimitUse runningTasks,
        IReadOnlyDictionary<string, LimitUse> limits,
        SnapshotJournal journal)
    {
        Started = started;
        Ended = ended;
        Outcome = outcome;
        Stop = stop;
        Nodes = nodes;
        _positions = positions;
        Counts = counts;
        RunningTasks = runningTasks;
        Limits = limits;
        Journal = journal.Info;
        SetAside = journal.SetAside;
        JournalPathsNotInGraph = journal.JournalPathsNotInGraph;
        JournalPathsNotInGraphUnsucceeded = journal.JournalPathsNotInGraphUnsucceeded;
    }

    /// <summary>When the run started, read from the run's <see cref="RunOptions.TimeProvider"/>.</summary>
    public DateTimeOffset Started { get; }

    /// <summary>When the run ended, or <see langword="null"/> while it has not.</summary>
    public DateTimeOffset? Ended { get; }

    /// <summary>How the run ended, or <see langword="null"/> while it has not.</summary>
    public RunOutcome? Outcome { get; }

    /// <summary>
    /// The stop the run has applied, or <see langword="null"/> while none has been. It is set in the snapshot handed with
    /// <see cref="StopRequested"/> and in every later one, and carries the time of that event.
    /// </summary>
    public StopState? Stop { get; }

    /// <summary>Every node of the graph, in declaration order.</summary>
    public IReadOnlyList<NodeSnapshot> Nodes { get; }

    /// <summary>
    /// How many tasks of the run have each status, with their sums and sub-counts: the total, the settled, those that have not
    /// succeeded, the earlier successes among the succeeded, and the waiting and working among the running.
    /// </summary>
    /// <remarks>
    /// Earlier successes are counted as succeeded from the first snapshot on, and <see cref="TaskCounts.EarlierSuccesses"/> counts
    /// them apart. That number is fixed for the run: zero for a run with no journal and for a new journal.
    /// </remarks>
    public TaskCounts Counts { get; }

    /// <summary>
    /// The max running tasks of the run and how full it is: <see cref="LimitUse.Size"/> is <see cref="RunOptions.MaxRunningTasks"/>,
    /// and <see cref="LimitUse.InUse"/> is the number of tasks running at the instant of this snapshot.
    /// </summary>
    public LimitUse RunningTasks { get; }

    /// <summary>
    /// Every limit the graph names, on a task or on a group, by name: its size, from <see cref="RunOptions.Limits"/>,
    /// and how many of its slots are in use at the instant of this snapshot.
    /// </summary>
    /// <remarks>
    /// The entries are in the order the graph first names each limit, in declaration order, and the names are compared ordinally.
    /// A limit that no task holds, such as one named only on an empty group, is listed with no slot in use.
    /// A size the options give for a limit the graph does not name is not listed.
    /// </remarks>
    public IReadOnlyDictionary<string, LimitUse> Limits { get; }

    /// <summary>
    /// The journal the run is on, or <see langword="null"/> for a run with <see cref="Dagline.Journal.None"/>.
    /// It is the same in every snapshot of the run, from the one handed with <see cref="RunStarted"/> on.
    /// </summary>
    /// <remarks>A run on a new journal is run 1, and its id is known only from here.</remarks>
    public JournalInfo? Journal { get; }

    /// <summary>
    /// The paths of the tasks whose earlier success this run set aside, in declaration order. Empty for a run with no journal,
    /// for a new journal, and when every success on record was kept.
    /// </summary>
    /// <remarks>
    /// A task the journal records as succeeded is set aside when it is <em>after</em> a node that is not an earlier success in this
    /// run: a task that is not one, or a group with such a task beneath it. A <em>requires</em> dependency never sets a success aside.
    /// A task set aside starts pending and runs again. The set-asides are written to the journal before any task starts, and from
    /// then on the task has no success on record, in this run and in later ones, until it succeeds again. The list is fixed for the run.
    /// </remarks>
    public IReadOnlyList<NodePath> SetAside { get; }

    /// <summary>
    /// How many paths the journal holds that this run's graph does not have as a task: a path no longer in the graph, or one that is
    /// a group in the graph. Zero for a run with no journal and for a new journal.
    /// </summary>
    /// <remarks>
    /// Such a path is left in the journal untouched and is not part of the run: it has no node, no event and no part in the run
    /// outcome. When a later run's graph has it as a task again, what the journal holds for it applies again. The number is fixed for the run.
    /// </remarks>
    public int JournalPathsNotInGraph { get; }

    /// <summary>
    /// How many of the <see cref="JournalPathsNotInGraph"/> have not succeeded: the journal holds no success for the path, or the
    /// success was set aside. This is the work a run on a smaller graph leaves behind, which a complete run does not show.
    /// </summary>
    public int JournalPathsNotInGraphUnsucceeded { get; }

    /// <summary>Every failed task, in declaration order. Empty when no task has failed.</summary>
    /// <remarks>
    /// Each entry is the same object as the one in <see cref="Nodes"/>. The list is worked out on the first read
    /// and kept, so reading it again costs nothing.
    /// </remarks>
    public IReadOnlyList<NodeSnapshot> Failed => TasksWith(ref _failed, task => task.Status is NodeStatus.Failed);

    /// <summary>Every cancelled task, in declaration order. Empty when no task has been cancelled.</summary>
    /// <remarks>
    /// A task is cancelled when its body ended with a cancellation exception after a stop. Each entry is the same object
    /// as the one in <see cref="Nodes"/>. The list is worked out on the first read and kept.
    /// </remarks>
    public IReadOnlyList<NodeSnapshot> Cancelled => TasksWith(ref _cancelled, task => task.Status is NodeStatus.Cancelled);

    /// <summary>
    /// Every task that is waiting, as <see cref="NodeSnapshot.IsWaiting"/> says: running with a waiting scope open.
    /// Tasks only, in declaration order. Empty when no task is waiting.
    /// </summary>
    /// <remarks>
    /// Each entry is the same object as the one in <see cref="Nodes"/>. The list is worked out on the first read and kept.
    /// Together with <see cref="Working"/> it holds every running task, and no task is in both.
    /// </remarks>
    public IReadOnlyList<NodeSnapshot> Waiting => TasksWith(ref _waiting, task => task.IsWaiting);

    /// <summary>
    /// Every task that is working, as <see cref="NodeSnapshot.IsWorking"/> says: running with no waiting scope open.
    /// Tasks only, in declaration order. Empty when no task is working.
    /// </summary>
    /// <remarks>
    /// Each entry is the same object as the one in <see cref="Nodes"/>. The list is worked out on the first read and kept.
    /// </remarks>
    public IReadOnlyList<NodeSnapshot> Working => TasksWith(ref _working, task => task.IsWorking);

    /// <summary>
    /// Every task the run never reached: those still pending or ready once the run has ended, in declaration order.
    /// Empty while the run has not ended.
    /// </summary>
    /// <remarks>
    /// Only a stopped run leaves tasks unreached: after a stop nothing starts, so a task that was ready stays ready,
    /// and one whose dependencies were not met stays pending. These tasks are not settled, and a group with one beneath it
    /// does not settle. A later run has them left to do. Each entry is the same object as the one in <see cref="Nodes"/>.
    /// The list is worked out on the first read and kept.
    /// </remarks>
    public IReadOnlyList<NodeSnapshot> NeverReached
        => Ended is null ? [] : TasksWith(ref _neverReached, task => task.Status is NodeStatus.Pending or NodeStatus.Ready);

    /// <summary>
    /// What each failure blocked: one entry for each failed task that blocked at least one task, in declaration order
    /// of the failed task. Empty when no task is blocked.
    /// </summary>
    /// <remarks>
    /// A failed task that blocked nothing has no entry, and a blocked task is in exactly one entry: that of the failed task
    /// its <see cref="NodeSnapshot.BlockedBy"/> names. The list is worked out on the first read and kept.
    /// </remarks>
    public IReadOnlyList<BlockedTasks> Blocked
        => LazyInitializer.EnsureInitialized(ref _blocked, () => BlockedTasks.Among(Nodes, _positions));

    /// <summary>
    /// How many warnings the tasks of the run have added so far, whatever the status of each task: those kept in
    /// <see cref="NodeSnapshot.Warnings"/> and those only counted in <see cref="NodeSnapshot.WarningsOmitted"/>.
    /// </summary>
    public int WarningCount => WithWarnings.Sum(task => task.WarningCount);

    /// <summary>Every task that has added at least one warning, in declaration order. Empty when no task has.</summary>
    /// <remarks>
    /// A task is listed from its first warning on, while it is still running, and whatever status it settles with.
    /// Each entry is the same object as the one in <see cref="Nodes"/>. The list is worked out on the first read and kept.
    /// </remarks>
    public IReadOnlyList<NodeSnapshot> WithWarnings
        => LazyInitializer.EnsureInitialized(ref _withWarnings, () => [.. Nodes.Where(node => node.Warnings.Count > 0)]);

    /// <summary>The node with the given path.</summary>
    /// <param name="path">The path of a node of the graph.</param>
    /// <returns>The node as it was at the instant of this snapshot.</returns>
    /// <exception cref="KeyNotFoundException">The graph has no node with that path.</exception>
    public NodeSnapshot this[NodePath path]
        => TryGetNode(path, out var node)
            ? node
            : throw new KeyNotFoundException($"The graph has no node with the path '{path}'.");

    /// <summary>Finds the node with the given path, if the graph has one.</summary>
    /// <param name="path">The path to look for.</param>
    /// <param name="node">The node as it was at the instant of this snapshot, when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the graph has a node with that path.</returns>
    public bool TryGetNode(NodePath path, [MaybeNullWhen(false)] out NodeSnapshot node)
    {
        var found = _positions.TryGetValue(path, out var position);
        node = found ? Nodes[position] : null;
        return found;
    }

    /// <summary>Finds what the failed task at <paramref name="cause"/> blocked, if it blocked anything.</summary>
    /// <param name="cause">The path of a failed task.</param>
    /// <param name="blocked">
    /// The entry of <see cref="Blocked"/> for that task, when the method returns <see langword="true"/>: every task it blocked,
    /// and the roots that name them.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the task at <paramref name="cause"/> has failed and blocked at least one task.
    /// <see langword="false"/> for a failed task that blocked nothing, for any other node, and for a path the graph does not have.
    /// </returns>
    /// <remarks>The lookup is built from <see cref="Blocked"/> on the first call and kept, so later calls cost little.</remarks>
    public bool TryGetBlocked(NodePath cause, [MaybeNullWhen(false)] out BlockedTasks blocked)
        => LazyInitializer.EnsureInitialized(ref _blockedByCause, () => Blocked.ToFrozenDictionary(entry => entry.Cause))
            .TryGetValue(cause, out blocked);

    /// <summary>The tasks, in declaration order, that pass <paramref name="matches"/>: worked out on the first read and kept.</summary>
    private IReadOnlyList<NodeSnapshot> TasksWith(ref IReadOnlyList<NodeSnapshot>? kept, Func<NodeSnapshot, bool> matches)
        => LazyInitializer.EnsureInitialized(ref kept, () => [.. Nodes.Where(node => !node.IsGroup && matches(node))]);
}
