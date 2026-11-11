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

    /// <summary>Gives this group a title: a readable name for people.</summary>
    /// <param name="title">The title. It may hold any text and need not be unique. Calling again replaces it.</param>
    /// <returns>This handle.</returns>
    /// <remarks>
    /// A title is separate from the segment and never part of the path, so it can change between runs
    /// without changing which work the path means. It is carried to <see cref="NodeSnapshot.Title"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="title"/> is <see langword="null"/>.</exception>
    public GroupNode Title(string title)
    {
        SetTitle(title);
        return this;
    }

    /// <summary>
    /// Names a limit that every task beneath this group holds: a ceiling on how many of the tasks that hold it
    /// may be running at once.
    /// </summary>
    /// <param name="name">
    /// The name of the limit: any text that is not empty. Names are compared ordinally, so <c>database</c> and <c>Database</c>
    /// are two limits.
    /// </param>
    /// <returns>This handle.</returns>
    /// <remarks>
    /// <para>
    /// The graph only names a limit. Its size is an option of the run, in <see cref="RunOptions.Limits"/>,
    /// so it can differ between runs while the graph stays the same work. A run whose options give no size for a limit
    /// the graph names is refused with <see cref="RunConfigurationException"/> before any task starts.
    /// </para>
    /// <para>
    /// The limit applies to every task beneath the group at any depth, whether it was added before or after this call,
    /// and each of them holds a slot of its own: the group itself holds none. A task holds one slot of each of its limits,
    /// those named on it and on every group that holds it, from the moment it starts until it settles.
    /// It takes them all at once or takes none, so it never holds one slot while it waits for another.
    /// </para>
    /// <para>Call again to name another limit. Naming the same limit twice is the same as naming it once.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    public GroupNode Limit(string name)
    {
        NameLimit(name);
        return this;
    }

    /// <summary>
    /// Declares that every task beneath this group <em>requires</em> <paramref name="upstream"/>:
    /// each becomes ready only once the upstream has succeeded.
    /// </summary>
    /// <param name="upstream">
    /// The node the tasks beneath this group require. A group stands for every task beneath it at any depth,
    /// and an empty group is met at once.
    /// </param>
    /// <returns>This handle.</returns>
    /// <remarks>
    /// The dependency applies to every task beneath the group at any depth, whether it was added before or after this call.
    /// The snapshot shows it on the group, not on each task.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="upstream"/> is <see langword="null"/>.</exception>
    public GroupNode Requires(Node upstream)
    {
        Declare(upstream, DependencyKind.Requires);
        return this;
    }

    /// <summary>
    /// Declares that every task beneath this group <em>requires</em> every node in <paramref name="upstreams"/>, as if
    /// <see cref="Requires(Node)"/> had been called with each in turn.
    /// </summary>
    /// <param name="upstreams">
    /// The nodes the tasks beneath this group require. The collection is read once, during the call.
    /// An empty one declares nothing, so a dependency on a list that came back empty is valid.
    /// </param>
    /// <returns>This handle.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="upstreams"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A handle in <paramref name="upstreams"/> is <see langword="null"/>. Nothing is declared.</exception>
    public GroupNode Requires(IEnumerable<Node> upstreams)
    {
        Declare(upstreams, DependencyKind.Requires);
        return this;
    }

    /// <summary>
    /// Declares that every task beneath this group runs <em>after</em> <paramref name="upstream"/>:
    /// each becomes ready once the upstream has settled, whatever the outcome.
    /// </summary>
    /// <param name="upstream">
    /// The node the tasks beneath this group run after. A group stands for every task beneath it at any depth,
    /// and an empty group is met at once.
    /// </param>
    /// <returns>This handle.</returns>
    /// <remarks>
    /// The dependency applies to every task beneath the group at any depth, whether it was added before or after this call.
    /// The snapshot shows it on the group, not on each task.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="upstream"/> is <see langword="null"/>.</exception>
    public GroupNode After(Node upstream)
    {
        Declare(upstream, DependencyKind.After);
        return this;
    }

    /// <summary>
    /// Declares that every task beneath this group runs <em>after</em> every node in <paramref name="upstreams"/>, as if
    /// <see cref="After(Node)"/> had been called with each in turn.
    /// </summary>
    /// <param name="upstreams">
    /// The nodes the tasks beneath this group run after. The collection is read once, during the call.
    /// An empty one declares nothing, so a dependency on a list that came back empty is valid.
    /// </param>
    /// <returns>This handle.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="upstreams"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A handle in <paramref name="upstreams"/> is <see langword="null"/>. Nothing is declared.</exception>
    public GroupNode After(IEnumerable<Node> upstreams)
    {
        Declare(upstreams, DependencyKind.After);
        return this;
    }

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
