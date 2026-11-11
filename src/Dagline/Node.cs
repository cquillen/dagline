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
        Segment = segment;
        Path = parent is null ? NodePath.AtRoot(segment) : parent.Path.Child(segment);
        ValidPath = NodePath.IsSegment(segment) && (parent is null || parent.ValidPath is not null) ? Path : null;
    }

    /// <summary>
    /// The identity of this node: the segment of every group that holds it, from the root down, then its own segment.
    /// </summary>
    public NodePath Path { get; }

    /// <summary>The group that holds this node, or <see langword="null"/> for a node at the root.</summary>
    internal GroupNode? Parent { get; }

    /// <summary>The segment this node was added with, which <see cref="GraphBuilder.Build"/> checks.</summary>
    internal string Segment { get; }

    /// <summary>
    /// <see cref="Path"/> when every segment of it is valid, and <see langword="null"/> when the segment of this node
    /// or of a group that holds it is not.
    /// </summary>
    internal NodePath? ValidPath { get; }

    /// <summary>The title given to this node so far, or <see langword="null"/> when it has none.</summary>
    internal string? TitleText { get; private set; }

    /// <summary>The builder this handle came from. A dependency joins two handles of one builder.</summary>
    internal GraphBuilder Builder { get; }

    /// <summary>Every group that holds this node, the nearest first.</summary>
    internal IEnumerable<GroupNode> Ancestors()
    {
        for (var group = Parent; group is not null; group = group.Parent)
        {
            yield return group;
        }
    }

    private protected void SetTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);

        TitleText = title;
    }

    private protected void Declare(Node upstream, DependencyKind kind)
    {
        ArgumentNullException.ThrowIfNull(upstream);

        Builder.Declare(this, upstream, kind);
    }

    private protected void Declare(IEnumerable<Node> upstreams, DependencyKind kind)
    {
        ArgumentNullException.ThrowIfNull(upstreams);

        Node[] handles = [.. upstreams];
        if (handles.Contains(null))
        {
            throw new ArgumentException("A handle in the collection is null.", nameof(upstreams));
        }

        foreach (var upstream in handles)
        {
            Builder.Declare(this, upstream, kind);
        }
    }
}
