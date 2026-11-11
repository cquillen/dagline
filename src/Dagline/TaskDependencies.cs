using System.Collections.Immutable;

namespace Dagline;

/// <summary>
/// Works out which nodes each task depends on, from the dependencies as they were declared on handles.
/// </summary>
/// <remarks>
/// A task depends on the upstreams declared on it and on those declared on every group that holds it.
/// Where that names one upstream more than once, it is one dependency, and requires wins.
/// An upstream that is a group is kept as the group: its derived status says whether every task beneath it has succeeded or settled.
/// </remarks>
internal sealed class TaskDependencies
{
    private readonly IReadOnlyDictionary<Node, int> _positions;
    private readonly ILookup<Node, DeclaredDependency> _declaredOn;
    private readonly Dictionary<TaskNode, ImmutableArray<Upstream>> _upstreams;
    private readonly ILookup<int, int> _dependents;

    /// <param name="tasks">Every task of the graph, in declaration order.</param>
    /// <param name="positions">Where each node of the graph is in declaration order.</param>
    /// <param name="declared">Every dependency, in the order declared.</param>
    public TaskDependencies(
        IReadOnlyList<TaskNode> tasks,
        IReadOnlyDictionary<Node, int> positions,
        IReadOnlyList<DeclaredDependency> declared)
    {
        _positions = positions;
        _declaredOn = declared.ToLookup(dependency => dependency.Dependent);
        _upstreams = tasks.ToDictionary(task => task, ResolveUpstreams);
        _dependents = tasks
            .SelectMany(task => _upstreams[task].Select(upstream => (Upstream: upstream.Position, Dependent: positions[task])))
            .ToLookup(dependency => dependency.Upstream, dependency => dependency.Dependent);
    }

    /// <summary>
    /// The dependencies declared on one node, one for each upstream, in the order each upstream was first named.
    /// </summary>
    public IReadOnlyList<Dependency> DeclaredOn(Node node)
        => [.. Merge(_declaredOn[node]).Select(dependency => new Dependency(dependency.Upstream.Path, dependency.Kind))];

    /// <summary>Every node a task depends on, in declaration order.</summary>
    public ImmutableArray<Upstream> UpstreamsOf(TaskNode task) => _upstreams[task];

    /// <summary>The positions of the tasks that depend on a node, in declaration order.</summary>
    public ImmutableArray<int> DependentsOf(Node node) => [.. _dependents[_positions[node]]];

    private ImmutableArray<Upstream> ResolveUpstreams(TaskNode task)
    {
        var ownAndInherited = task.WithAncestors().SelectMany(node => _declaredOn[node]);

        return
        [
            .. Merge(ownAndInherited)
                .Select(dependency => new Upstream(_positions[dependency.Upstream], dependency.Kind))
                .OrderBy(upstream => upstream.Position),
        ];
    }

    /// <summary>One dependency for each upstream named, in the order each was first named.</summary>
    private static IEnumerable<(Node Upstream, DependencyKind Kind)> Merge(IEnumerable<DeclaredDependency> dependencies)
        => dependencies.MergedBy(dependency => dependency.Upstream);
}
