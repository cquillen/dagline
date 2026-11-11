namespace Dagline;

/// <summary>
/// Builds a graph with plain statements: add nodes, declare dependencies on the handles that come back, then call <see cref="Build"/>.
/// </summary>
/// <remarks>
/// <para>
/// The builder is the scope for the nodes at the root of the graph. A group handle is the scope for that group's children,
/// so a tree of any depth is built with ordinary nested loops.
/// </para>
/// <para>
/// The declaration order of the graph is a depth-first walk of that tree, each group before its children,
/// with the children of each scope in the order they were added to it. Adding <c>a</c>, <c>a/x</c>, <c>b</c>, then <c>a/y</c>
/// gives <c>a</c>, <c>a/x</c>, <c>a/y</c>, <c>b</c>.
/// It is the order of the graph, of every snapshot, and the order in which ready tasks start.
/// </para>
/// <para>A builder is not safe to use from several threads at once.</para>
/// </remarks>
public sealed class GraphBuilder
{
    private readonly ChildNodes _children;
    private readonly List<DeclaredDependency> _dependencies = [];

    /// <summary>Creates a builder with no nodes.</summary>
    public GraphBuilder()
    {
        _children = new ChildNodes(this, parent: null);
    }

    /// <summary>Adds a group at the root of the graph.</summary>
    /// <param name="segment">The segment that names the group among the nodes at the root. At the root it is the group's whole path.</param>
    /// <returns>A handle to the new group, which is also the scope for its children. A group may stay empty.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="segment"/> is <see langword="null"/>.</exception>
    public GroupNode Group(string segment) => _children.AddGroup(segment);

    /// <summary>Adds a task at the root of the graph.</summary>
    /// <param name="segment">The segment that names the task among the nodes at the root. At the root it is the task's whole path.</param>
    /// <param name="body">The work of the task.</param>
    /// <returns>A handle to the new task, on which its dependencies are declared and which other nodes can depend on.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="segment"/> or <paramref name="body"/> is <see langword="null"/>.</exception>
    public TaskNode Task(string segment, TaskBody body) => _children.AddTask(segment, body);

    /// <summary>Builds the graph from everything added so far.</summary>
    /// <returns>An immutable graph that can be run any number of times.</returns>
    public Graph Build() => new([.. _children.InDeclarationOrder()], _dependencies);

    internal void Declare(Node dependent, Node upstream, DependencyKind kind)
        => _dependencies.Add(new DeclaredDependency(dependent, upstream, kind));
}
