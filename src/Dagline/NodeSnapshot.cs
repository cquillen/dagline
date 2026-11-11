namespace Dagline;

/// <summary>One node of a run at one instant. Immutable.</summary>
public sealed record NodeSnapshot
{
    internal NodeSnapshot(NodePath path, string? title, bool isGroup, IReadOnlyList<Dependency> dependencies)
    {
        Path = path;
        Title = title;
        IsGroup = isGroup;
        Dependencies = dependencies;
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
