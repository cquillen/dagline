namespace Dagline;

/// <summary>
/// A handle to a node of a graph that is being built, returned by the builder when the node is added.
/// </summary>
/// <remarks>
/// Dependencies are declared between handles, never between strings, so a misspelt upstream does not compile.
/// The set of node kinds is closed: a type outside Dagline cannot derive from this class.
/// </remarks>
public abstract class Node
{
    private protected Node(GraphBuilder builder, GroupNode? parent, string segment)
    {
        Builder = builder;
        Parent = parent;
        Path = parent is null ? NodePath.AtRoot(segment) : parent.Path.Child(segment);
    }

    /// <summary>
    /// The identity of this node: the segment of every group that holds it, from the root down, then its own segment.
    /// </summary>
    public NodePath Path { get; }

    /// <summary>The group that holds this node, or <see langword="null"/> for a node at the root.</summary>
    internal GroupNode? Parent { get; }

    private protected GraphBuilder Builder { get; }
}
