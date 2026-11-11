namespace Dagline;

/// <summary>
/// Builds a graph with plain statements: add nodes, declare dependencies on the handles that come back, then call <see cref="Build"/>.
/// </summary>
/// <remarks>
/// <para>
/// The order in which nodes are added is the declaration order of the graph.
/// It is the order of every snapshot and the order in which ready tasks start.
/// </para>
/// <para>A builder is not safe to use from several threads at once.</para>
/// </remarks>
public sealed class GraphBuilder
{
    private readonly List<TaskNode> _tasks = [];
    private readonly List<DeclaredDependency> _dependencies = [];

    /// <summary>Adds a task at the root of the graph.</summary>
    /// <param name="segment">The segment that names the task among its siblings. At the root it is the task's whole path.</param>
    /// <param name="body">The work of the task.</param>
    /// <returns>A handle to the new task, on which its dependencies are declared and which other nodes can depend on.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="segment"/> or <paramref name="body"/> is <see langword="null"/>.</exception>
    public TaskNode Task(string segment, TaskBody body)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(body);

        var task = new TaskNode(this, NodePath.AtRoot(segment), body);
        _tasks.Add(task);
        return task;
    }

    /// <summary>Builds the graph from everything added so far.</summary>
    /// <returns>An immutable graph that can be run any number of times.</returns>
    public Graph Build() => new(_tasks, _dependencies);

    internal void Declare(Node dependent, Node upstream, DependencyKind kind)
        => _dependencies.Add(new DeclaredDependency(dependent, upstream, kind));
}
