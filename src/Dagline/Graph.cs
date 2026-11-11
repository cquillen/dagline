using System.Collections;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Dagline;

/// <summary>
/// A built graph: the nodes a <see cref="GraphBuilder"/> collected and the dependencies between them.
/// </summary>
/// <remarks>
/// <para>
/// A graph is immutable. It can be run any number of times, one run after another or several at once,
/// and every run starts with every task pending.
/// </para>
/// <para>
/// As a list it holds the handle of every node, tasks and groups together, in declaration order:
/// a depth-first walk of the tree, each group before its children, with the children of each group in the order
/// they were added to it. A snapshot lists the nodes in the same order, and ready tasks start in it.
/// </para>
/// </remarks>
public sealed class Graph : IReadOnlyList<Node>
{
    private readonly ImmutableArray<Node> _handles;

    internal Graph(ImmutableArray<Node> nodes, IReadOnlyList<DeclaredDependency> declared)
    {
        _handles = nodes;

        var positions = nodes.Index().ToDictionary(node => node.Item, node => node.Index);
        List<TaskNode> tasks = [.. nodes.OfType<TaskNode>()];
        var dependencies = new TaskDependencies(tasks, positions, declared);
        var tasksBeneath = tasks.SelectMany(task => task.Ancestors()).CountBy(group => (Node)group).ToDictionary();

        LimitNames = [.. nodes.SelectMany(node => node.Limits).Distinct()];
        var limitPositions = LimitNames.Index().ToDictionary(limit => limit.Item, limit => limit.Index);

        Nodes = [.. nodes.Select(ToGraphNode)];
        Tasks = [.. Nodes.OfType<GraphTask>()];
        Positions = Nodes.ToFrozenDictionary(node => node.Path, node => node.Position);

        GraphNode ToGraphNode(Node node, int position)
        {
            IReadOnlyList<string> limitsNamed = [.. node.Limits.Select(limit => limit.Text)];

            return node is TaskNode task
                ? new GraphTask(
                    position,
                    node.Path,
                    node.TitleText,
                    dependencies.DeclaredOn(node),
                    limitsNamed,
                    dependencies.DependentsOf(node),
                    task.Body,
                    [.. node.Ancestors().Select(group => positions[group])],
                    dependencies.UpstreamsOf(task),
                    [.. HeldBy(task).Select(limit => limitPositions[limit])])
                : new GraphGroup(
                    position,
                    node.Path,
                    node.TitleText,
                    dependencies.DeclaredOn(node),
                    limitsNamed,
                    dependencies.DependentsOf(node),
                    Depth: node.Ancestors().Count(),
                    TaskCount: tasksBeneath.GetValueOrDefault(node));
        }
    }

    /// <summary>How many nodes the graph has, tasks and groups together.</summary>
    public int Count => _handles.Length;

    /// <summary>Every node, in declaration order.</summary>
    internal ImmutableArray<GraphNode> Nodes { get; }

    /// <summary>Every task, in declaration order.</summary>
    internal ImmutableArray<GraphTask> Tasks { get; }

    /// <summary>Where each node is in declaration order.</summary>
    internal FrozenDictionary<NodePath, int> Positions { get; }

    /// <summary>Every limit the graph names, on a task or on a group, each once, in the order first named in declaration order.</summary>
    internal ImmutableArray<LimitName> LimitNames { get; }

    /// <summary>The handle of the node at a position in declaration order.</summary>
    /// <param name="index">The position of the node, from zero.</param>
    /// <returns>The handle the builder returned when the node was added.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="index"/> is negative, or is not less than <see cref="Count"/>.
    /// </exception>
    public Node this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);

            return _handles[index];
        }
    }

    /// <summary>Finds the node with the given path, if the graph has one.</summary>
    /// <param name="path">The path to look for.</param>
    /// <param name="node">
    /// The handle the builder returned when the node was added, when the method returns <see langword="true"/>.
    /// </param>
    /// <returns><see langword="true"/> when the graph has a node with that path.</returns>
    public bool TryGetNode(NodePath path, [MaybeNullWhen(false)] out Node node)
    {
        var found = Positions.TryGetValue(path, out var position);
        node = found ? _handles[position] : null;
        return found;
    }

    /// <summary>Enumerates the handles of the nodes in declaration order.</summary>
    /// <returns>An enumerator over every node of the graph, tasks and groups together.</returns>
    public IEnumerator<Node> GetEnumerator() => ((IEnumerable<Node>)_handles).GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>The task at a position among the nodes of the graph.</summary>
    internal GraphTask TaskAt(int position) => (GraphTask)Nodes[position];

    /// <summary>The group at a position among the nodes of the graph.</summary>
    internal GraphGroup GroupAt(int position) => (GraphGroup)Nodes[position];

    /// <summary>A task and every group that holds it: the nodes that cannot succeed unless the task does.</summary>
    internal IEnumerable<GraphNode> WithItsGroups(GraphTask task) => task.Groups.Select(GroupAt).Prepend<GraphNode>(task);

    /// <summary>The tasks that require a node, in declaration order.</summary>
    internal IEnumerable<GraphTask> Requiring(GraphNode node)
    {
        var requires = new Upstream(node.Position, DependencyKind.Requires);
        return node.Dependents.Select(TaskAt).Where(dependent => dependent.Upstreams.Contains(requires));
    }

    /// <summary>
    /// Finds a node that a task depends on: an upstream declared on the task or on a group that holds it,
    /// or a node beneath such an upstream at any depth.
    /// </summary>
    /// <param name="task">The task that depends.</param>
    /// <param name="handle">A handle to the node, which may be of another graph.</param>
    /// <param name="position">The position of the node among the nodes of this graph, when the method returns <see langword="true"/>.</param>
    internal bool TryGetDependency(GraphTask task, Node handle, out int position)
        => TryGetPosition(handle, out position)
            && handle.WithAncestors().Select(node => Positions[node.Path]).Any(task.IsUpstream);

    /// <summary>Finds where a handle is in declaration order. A handle of another graph is not found, whatever its path.</summary>
    private bool TryGetPosition(Node handle, out int position)
        => Positions.TryGetValue(handle.Path, out position) && ReferenceEquals(_handles[position], handle);

    /// <summary>The limits a task holds: those named on it and those named on every group that holds it, each once.</summary>
    private static IEnumerable<LimitName> HeldBy(TaskNode task)
        => task.WithAncestors().SelectMany(node => node.Limits).Distinct();
}
