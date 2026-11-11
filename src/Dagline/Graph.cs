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
    internal Graph(IReadOnlyList<TaskNode> tasks, IReadOnlyList<DeclaredDependency> dependencies)
    {
        var positions = tasks.Index().ToDictionary(task => (Node)task.Item, task => task.Index);
        var declaredOn = dependencies.ToLookup(dependency => dependency.Dependent);
        var dependentsOf = dependencies.ToLookup(
            dependency => positions[dependency.Upstream],
            dependency => positions[dependency.Dependent]);

        Tasks =
        [
            .. tasks.Select((task, position) => new GraphTask(
                position,
                task.Path,
                task.Body,
                [.. declaredOn[task].Select(dependency => new Dependency(dependency.Upstream.Path, dependency.Kind))],
                [.. declaredOn[task].Select(dependency => new Upstream(positions[dependency.Upstream], dependency.Kind))],
                [.. dependentsOf[position].Distinct().Order()])),
        ];
        Positions = Tasks.ToFrozenDictionary(task => task.Path, task => task.Position);
    }

    /// <summary>Every task, in declaration order.</summary>
    internal ImmutableArray<GraphTask> Tasks { get; }

    /// <summary>Where each node is in declaration order.</summary>
    internal FrozenDictionary<NodePath, int> Positions { get; }

    /// <summary>The task at a position among the nodes of the graph.</summary>
    internal GraphTask TaskAt(int position) => Tasks[position];
}
