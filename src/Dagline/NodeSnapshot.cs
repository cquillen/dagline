namespace Dagline;

/// <summary>One node of a run at one instant. Immutable.</summary>
public sealed record NodeSnapshot
{
    internal NodeSnapshot(NodePath path, bool isGroup, IReadOnlyList<Dependency> dependencies)
    {
        Path = path;
        IsGroup = isGroup;
        Dependencies = dependencies;
    }

    /// <summary>The identity of the node.</summary>
    public NodePath Path { get; }

    /// <summary><see langword="true"/> for a group and <see langword="false"/> for a task.</summary>
    public bool IsGroup { get; }

    /// <summary>The dependencies declared on this node, in the order they were declared.</summary>
    public IReadOnlyList<Dependency> Dependencies { get; }

    /// <summary>Where the node is in its life.</summary>
    public NodeStatus Status { get; internal init; }

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
