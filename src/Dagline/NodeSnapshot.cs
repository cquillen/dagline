namespace Dagline;

/// <summary>One node of a run at one instant. Immutable.</summary>
public sealed record NodeSnapshot
{
    internal NodeSnapshot(NodePath path)
    {
        Path = path;
    }

    /// <summary>The identity of the node.</summary>
    public NodePath Path { get; }

    /// <summary>Where the node is in its life.</summary>
    public NodeStatus Status { get; internal init; }
}
