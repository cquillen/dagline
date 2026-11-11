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
    private IReadOnlyList<BlockedTasks>? _blocked;
    private IReadOnlyList<NodeSnapshot>? _withWarnings;

    internal Snapshot(
        DateTimeOffset started,
        DateTimeOffset? ended,
        RunOutcome? outcome,
        StopState? stop,
        ChunkedList<NodeSnapshot> nodes,
        FrozenDictionary<NodePath, int> positions,
        IReadOnlyDictionary<NodeStatus, int> counts,
        LimitUse cap,
        IReadOnlyDictionary<string, LimitUse> limits)
    {
        Started = started;
        Ended = ended;
        Outcome = outcome;
        Stop = stop;
        Nodes = nodes;
        _positions = positions;
        Counts = counts;
        Cap = cap;
        Limits = limits;
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
    /// How many tasks have each status. Every status is present, with zero for a status no task has,
    /// and the entries are in lifecycle order: pending, ready, running, succeeded, failed, blocked, cancelled.
    /// </summary>
    public IReadOnlyDictionary<NodeStatus, int> Counts { get; }

    /// <summary>
    /// The cap of the run: its size, from <see cref="RunOptions.Cap"/>, and how many of its slots are in use,
    /// which is the number of tasks running at the instant of this snapshot.
    /// </summary>
    public LimitUse Cap { get; }

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

    /// <summary>Every failed task, in declaration order. Empty when no task has failed.</summary>
    /// <remarks>
    /// Each entry is the same object as the one in <see cref="Nodes"/>. The list is worked out on the first read
    /// and kept, so reading it again costs nothing.
    /// </remarks>
    public IReadOnlyList<NodeSnapshot> Failed => TasksWith(ref _failed, status => status is NodeStatus.Failed);

    /// <summary>Every cancelled task, in declaration order. Empty when no task has been cancelled.</summary>
    /// <remarks>
    /// A task is cancelled when its body ended with a cancellation exception after a stop. Each entry is the same object
    /// as the one in <see cref="Nodes"/>. The list is worked out on the first read and kept.
    /// </remarks>
    public IReadOnlyList<NodeSnapshot> Cancelled => TasksWith(ref _cancelled, status => status is NodeStatus.Cancelled);

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
        => Ended is null ? [] : TasksWith(ref _neverReached, status => status is NodeStatus.Pending or NodeStatus.Ready);

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
    public int WarningCount => WithWarnings.Sum(task => task.Warnings.Count + task.WarningsOmitted);

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

    /// <summary>The tasks, in declaration order, whose status passes <paramref name="matches"/>: worked out on the first read and kept.</summary>
    private IReadOnlyList<NodeSnapshot> TasksWith(ref IReadOnlyList<NodeSnapshot>? kept, Func<NodeStatus, bool> matches)
        => LazyInitializer.EnsureInitialized(ref kept, () => [.. Nodes.Where(node => !node.IsGroup && matches(node.Status))]);
}
