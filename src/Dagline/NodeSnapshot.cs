namespace Dagline;

/// <summary>One node of a run at one instant. Immutable.</summary>
public sealed record NodeSnapshot
{
    internal NodeSnapshot(GraphNode node, NodeStatus status, IReadOnlyDictionary<NodeStatus, int> counts)
    {
        Path = node.Path;
        Title = node.Title;
        IsGroup = node is GraphGroup;
        Dependencies = node.Dependencies;
        Limits = node.Limits;
        Status = status;
        Counts = counts;
    }

    /// <summary>The identity of the node.</summary>
    public NodePath Path { get; }

    /// <summary>
    /// The readable name the graph gave the node, or <see langword="null"/> when it gave none.
    /// It is separate from the node's segment and never part of <see cref="Path"/>.
    /// </summary>
    public string? Title { get; }

    /// <summary><see langword="true"/> for a group and <see langword="false"/> for a task.</summary>
    public bool IsGroup { get; }

    /// <summary>
    /// The dependencies declared on this node itself, one for each upstream, in the order each upstream was first named.
    /// An upstream that was given both kinds is listed once, as <see cref="DependencyKind.Requires"/>.
    /// </summary>
    /// <remarks>
    /// A dependency declared on a group is listed on that group and not on the nodes beneath it,
    /// although it applies to every task beneath the group.
    /// </remarks>
    public IReadOnlyList<Dependency> Dependencies { get; }

    /// <summary>
    /// The limits named on this node itself, each once, in the order first named.
    /// </summary>
    /// <remarks>
    /// A limit named on a group is listed on that group and not on the nodes beneath it,
    /// although every task beneath the group holds a slot of it while it runs.
    /// <see cref="Snapshot.Limits"/> gives the size of each limit and how many of its slots are in use.
    /// </remarks>
    public IReadOnlyList<string> Limits { get; }

    /// <summary>
    /// Where the node is in its life. A task has the status the run gave it. A group's status is derived from the tasks
    /// beneath it at any depth.
    /// </summary>
    /// <remarks>
    /// While any task beneath a group has not settled, the group is running if any of them is running, else ready if any is ready,
    /// else pending. Once all have settled it is failed if any failed, else cancelled if any was cancelled,
    /// else blocked if any is blocked, else succeeded. A group with no tasks beneath it is succeeded from the start of the run.
    /// </remarks>
    public NodeStatus Status { get; internal init; }

    /// <summary>
    /// How many tasks have each status: for a group, the tasks beneath it at any depth, and for a task, the task itself.
    /// Every status is present, with zero for a status no task has, and the entries are in lifecycle order:
    /// pending, ready, running, succeeded, failed, blocked, cancelled.
    /// </summary>
    public IReadOnlyDictionary<NodeStatus, int> Counts { get; internal init; }

    /// <summary>
    /// Why the task failed, or <see langword="null"/> for a task that has not failed and for a group.
    /// </summary>
    /// <remarks>
    /// An exception that escapes a task body, a cancellation exception included, is <see cref="FailureKind.Unexpected"/>.
    /// </remarks>
    public FailureKind? Failure { get; internal init; }

    /// <summary>
    /// The path of the failed task that blocked this task, or <see langword="null"/> for a task that is not blocked and for a group.
    /// </summary>
    /// <remarks>
    /// It is always a failed task, never a blocked one: a task blocked through a chain of blocked tasks names the one failure
    /// that started the cascade. It is the first failure to reach the task, and a later failure of another upstream does not change it.
    /// </remarks>
    public NodePath? BlockedBy { get; internal init; }

    /// <summary>
    /// The one-line message of the task's outcome, or <see langword="null"/> when it has none and for a group.
    /// For an unexpected failure it is the message of the exception that escaped the task body, with each line break in it
    /// replaced by a space.
    /// </summary>
    public string? Message { get; internal init; }

    /// <summary>
    /// The detail lines of the task's outcome, in the order added. Empty when it has none and for a group.
    /// For an unexpected failure they are the full name of the exception's type, then each line of its stack trace
    /// as the runtime wrote it.
    /// </summary>
    public IReadOnlyList<string> Detail { get; internal init; } = [];

    /// <summary>
    /// The status text of the task's progress: the line the task last set with <see cref="TaskContext.Status"/>.
    /// <see langword="null"/> when the task has set none or has cleared it, and for a group.
    /// </summary>
    /// <remarks>
    /// Progress is never an event: read it from a snapshot. It is frozen when the task settles, so a settled task
    /// still shows how far it got.
    /// </remarks>
    public string? StatusText { get; internal init; }

    /// <summary>
    /// The count of the task's progress: the number the task last gave <see cref="TaskContext.Count"/>.
    /// <see langword="null"/> when the task has given none, and for a group.
    /// </summary>
    /// <remarks>Like <see cref="StatusText"/>, it is never an event and is frozen when the task settles.</remarks>
    public long? Count { get; internal init; }

    /// <summary>
    /// The total that <see cref="Count"/> is counting towards, as the task last gave it to <see cref="TaskContext.Count"/>.
    /// <see langword="null"/> when the last count came with no total, when the task has given no count, and for a group.
    /// </summary>
    /// <remarks>
    /// Dagline does not compare the two numbers, so the count can be above the total.
    /// </remarks>
    public long? Total { get; internal init; }

    /// <summary>
    /// When the task started running, read from the run's <see cref="RunOptions.TimeProvider"/>,
    /// or <see langword="null"/> while it has not started. A blocked task never starts, so it has no start time.
    /// </summary>
    public DateTimeOffset? Started { get; internal init; }

    /// <summary>
    /// When the task settled, read from the run's <see cref="RunOptions.TimeProvider"/>,
    /// or <see langword="null"/> while it has not settled. A blocked task settles at the time the task that blocked it failed.
    /// </summary>
    public DateTimeOffset? Settled { get; internal init; }
}
