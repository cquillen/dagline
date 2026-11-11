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
    private protected Node(GraphBuilder builder, NodePath path)
    {
        Builder = builder;
        Path = path;
    }

    /// <summary>The identity of this node.</summary>
    public NodePath Path { get; }

    private protected GraphBuilder Builder { get; }
}
