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
    /// When the task started running, read from the run's <see cref="RunOptions.TimeProvider"/>,
    /// or <see langword="null"/> while it has not started.
    /// </summary>
    public DateTimeOffset? Started { get; internal init; }

    /// <summary>
    /// When the task settled, read from the run's <see cref="RunOptions.TimeProvider"/>,
    /// or <see langword="null"/> while it has not settled.
    /// </summary>
    public DateTimeOffset? Settled { get; internal init; }
}
