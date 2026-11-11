namespace Dagline;

/// <summary>
/// A handle to a group of a graph that is being built. A group is a named container of tasks and other groups,
/// and never runs code itself.
/// </summary>
/// <remarks>
/// <para>
/// The handle is also the scope for the group's children: <see cref="Group"/> and <see cref="Task"/> add a node
/// beneath this group, so nested loops build a tree with no lambdas. Groups nest to any depth, and a group may stay empty.
/// </para>
/// <para>
/// Keep the handle to add more children later. The children of a group are in the order they were added to it,
/// whatever was added elsewhere in between.
/// </para>
/// <para>Nesting never orders work: only a dependency does.</para>
/// </remarks>
public sealed class GroupNode : Node
{
    internal GroupNode(GraphBuilder builder, GroupNode? parent, string segment)
        : base(builder, parent, segment)
    {
        Children = new ChildNodes(builder, this);
    }

    internal ChildNodes Children { get; }

    /// <summary>Adds a group beneath this group.</summary>
    /// <param name="segment">The segment that names the new group among the children of this one.</param>
    /// <returns>A handle to the new group, which is also the scope for its own children.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="segment"/> is <see langword="null"/>.</exception>
    public GroupNode Group(string segment) => Children.AddGroup(segment);

    /// <summary>Adds a task beneath this group.</summary>
    /// <param name="segment">The segment that names the task among the children of this group.</param>
    /// <param name="body">The work of the task.</param>
    /// <returns>A handle to the new task, on which its dependencies are declared and which other nodes can depend on.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="segment"/> or <paramref name="body"/> is <see langword="null"/>.</exception>
    public TaskNode Task(string segment, TaskBody body) => Children.AddTask(segment, body);
}
