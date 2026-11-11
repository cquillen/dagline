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
/// <para>
/// Adding a node or declaring a dependency never fails because of what the graph then looks like: a handle always comes back,
/// so graph-building code runs to its end. <see cref="Build"/> checks the whole graph and reports every problem at once.
/// </para>
/// <para>
/// A builder is single use. Once <see cref="Build"/> has returned a graph, the builder and every handle it made throw
/// <see cref="InvalidOperationException"/> from any member that would change the graph, so a built graph cannot change.
/// The handles stay valid to read, and the <see cref="Graph"/> lists them.
/// </para>
/// <para>A builder is not safe to use from several threads at once.</para>
/// </remarks>
public sealed class GraphBuilder
{
    private readonly ChildNodes _children;
    private readonly List<DeclaredDependency> _dependencies = [];
    private bool _isBuilt;

    /// <summary>Creates a builder with no nodes.</summary>
    public GraphBuilder()
    {
        _children = new ChildNodes(this, parent: null);
    }

    /// <summary>Adds a group at the root of the graph.</summary>
    /// <param name="segment">
    /// The segment that names the group among the nodes at the root. At the root it is the group's whole path.
    /// A segment is one or more of <c>A-Z</c>, <c>a-z</c>, <c>0-9</c>, <c>.</c>, <c>_</c> and <c>-</c>,
    /// and no other node at the root may have the same one, or one that differs only by case.
    /// </param>
    /// <returns>
    /// A handle to the new group, which is also the scope for its children. A group may stay empty.
    /// A handle is returned even when the segment is invalid or already taken: <see cref="Build"/> reports that.
    /// Adding a group with the segment of an existing group makes a duplicate path and does not reopen that group.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="segment"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The graph has been built.</exception>
    public GroupNode Group(string segment) => _children.AddGroup(segment);

    /// <summary>Adds a task at the root of the graph.</summary>
    /// <param name="segment">
    /// The segment that names the task among the nodes at the root. At the root it is the task's whole path.
    /// A segment is one or more of <c>A-Z</c>, <c>a-z</c>, <c>0-9</c>, <c>.</c>, <c>_</c> and <c>-</c>,
    /// and no other node at the root may have the same one, or one that differs only by case.
    /// </param>
    /// <param name="body">The work of the task.</param>
    /// <returns>
    /// A handle to the new task, on which its dependencies are declared and which other nodes can depend on.
    /// A handle is returned even when the segment is invalid or already taken: <see cref="Build"/> reports that.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="segment"/> or <paramref name="body"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The graph has been built.</exception>
    public TaskNode Task(string segment, TaskBody body) => _children.AddTask(segment, body);

    /// <summary>Checks everything added so far and builds the graph from it.</summary>
    /// <returns>An immutable graph that can be run any number of times.</returns>
    /// <remarks>
    /// <para>
    /// The whole graph is checked before anything is built, and every problem found is reported together.
    /// A problem is one of:
    /// </para>
    /// <list type="bullet">
    /// <item><description>a segment that is not one or more of <c>A-Z</c>, <c>a-z</c>, <c>0-9</c>, <c>.</c>, <c>_</c> and <c>-</c>;</description></item>
    /// <item><description>a path added more than once, which includes a group added again and a task and a group on one path;</description></item>
    /// <item><description>siblings whose segments differ only by case;</description></item>
    /// <item><description>a dependency on a handle from another builder;</description></item>
    /// <item><description>a node that depends on itself;</description></item>
    /// <item><description>a dependency between a node and its own ancestor or descendant;</description></item>
    /// <item><description>a cycle of dependencies.</description></item>
    /// </list>
    /// <para>
    /// Both kinds of dependency count towards a cycle. A group stands for whatever is beneath it, and a dependency declared on a group
    /// applies to everything beneath it, so a cycle can pass through a group and is reported even when that group is empty.
    /// </para>
    /// <para>
    /// A call that returns a graph ends the use of the builder. A call that throws <see cref="GraphException"/> does not,
    /// but nothing can be taken out of a builder, so another call reports the same problems again.
    /// </para>
    /// </remarks>
    /// <exception cref="GraphException">The graph has at least one problem. <see cref="GraphException.Problems"/> lists them all.</exception>
    /// <exception cref="InvalidOperationException">This builder has already built its graph.</exception>
    public Graph Build()
    {
        ThrowIfBuilt();

        var problems = GraphValidation.Problems(this, _children, _dependencies);
        if (problems.Count > 0)
        {
            throw new GraphException(problems);
        }

        Graph graph = new([.. _children.InDeclarationOrder()], _dependencies);
        _isBuilt = true;
        return graph;
    }

    /// <summary>
    /// The single-use rule. Every member of the builder or of a handle that would change the graph calls this
    /// before it changes anything, once its arguments are known to be valid.
    /// </summary>
    internal void ThrowIfBuilt()
    {
        if (_isBuilt)
        {
            throw new InvalidOperationException(
                "The graph has been built. A builder is single use: it and its handles cannot change a graph once Build has returned it.");
        }
    }

    internal void Declare(Node dependent, Node upstream, DependencyKind kind)
        => _dependencies.Add(new DeclaredDependency(dependent, upstream, kind));
}
