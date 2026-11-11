using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Dagline;

/// <summary>
/// A built graph: the nodes a <see cref="GraphBuilder"/> collected and the dependencies between them.
/// </summary>
/// <remarks>
/// A graph is immutable. It can be run any number of times, one run after another or several at once,
/// and every run starts with every task pending.
/// </remarks>
public sealed class Graph
{
    internal Graph(ImmutableArray<Node> nodes, IReadOnlyList<DeclaredDependency> dependencies)
    {
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

    /// <summary>Every node, in declaration order.</summary>
    internal ImmutableArray<GraphNode> Nodes { get; }

    /// <summary>Every task, in declaration order.</summary>
    internal ImmutableArray<GraphTask> Tasks { get; }

    /// <summary>Where each node is in declaration order.</summary>
    internal FrozenDictionary<NodePath, int> Positions { get; }

    /// <summary>The task at a position among the nodes of the graph.</summary>
    internal GraphTask TaskAt(int position) => (GraphTask)Nodes[position];
}
