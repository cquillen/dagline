namespace Dagline;

/// <summary>
/// The children of one scope of a builder, in the order they were added: the nodes at the root of the graph, or those of one group.
/// </summary>
internal sealed class ChildNodes(GraphBuilder builder, GroupNode? parent)
{
    private readonly List<Node> _nodes = [];

    public GroupNode AddGroup(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);

        var group = new GroupNode(builder, parent, segment);
        _nodes.Add(group);
        return group;
    }

    public TaskNode AddTask(string segment, TaskBody body)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(body);

        var task = new TaskNode(builder, parent, segment, body);
        _nodes.Add(task);
        return task;
    }

    /// <summary>
    /// These children and every node beneath them in declaration order:
    /// depth first, each group before its own children, and each scope's children in the order added to it.
    /// </summary>
    public IEnumerable<Node> InDeclarationOrder()
        => _nodes.SelectMany(node => node is GroupNode group ? group.Children.InDeclarationOrder().Prepend(node) : [node]);
}
