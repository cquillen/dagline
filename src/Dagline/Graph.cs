using System.Collections;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Dagline;

/// <summary>
/// A built graph: the nodes a <see cref="GraphBuilder"/> collected and the dependencies between them.
/// </summary>
/// <remarks>
/// A graph is immutable. It can be run any number of times, one run after another or several at once,
/// and every run starts with every task pending.
/// </remarks>
public sealed class Graph : IReadOnlyList<Node>
{
    private readonly ImmutableArray<Node> _handles;

    internal Graph(ImmutableArray<Node> nodes, IReadOnlyList<DeclaredDependency> dependencies)
    {
        _handles = nodes;

        var positions = nodes.Index().ToDictionary(node => node.Item, node => node.Index);
        var declaredOn = dependencies.ToLookup(dependency => dependency.Dependent);
        var dependentsOf = dependencies.ToLookup(
            dependency => positions[dependency.Upstream],
            dependency => positions[dependency.Dependent]);

        Nodes = [.. nodes.Select(ToGraphNode)];
        Tasks = [.. Nodes.OfType<GraphTask>()];
        Positions = Nodes.ToFrozenDictionary(node => node.Path, node => node.Position);

        GraphNode ToGraphNode(Node node, int position)
        {
            IReadOnlyList<Dependency> declared =
                [.. declaredOn[node].Select(dependency => new Dependency(dependency.Upstream.Path, dependency.Kind))];

            return node is TaskNode task
                ? new GraphTask(
                    position,
                    node.Path,
                    declared,
                    task.Body,
                    [.. declaredOn[node].Select(dependency => new Upstream(positions[dependency.Upstream], dependency.Kind))],
                    [.. dependentsOf[position].Distinct().Order()])
                : new GraphGroup(position, node.Path, declared);
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
}
