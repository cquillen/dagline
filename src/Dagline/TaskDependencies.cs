using System.Collections.Immutable;

namespace Dagline;

/// <summary>
/// Works out which tasks each task waits for, from the dependencies as they were declared on handles.
/// </summary>
/// <remarks>
/// A dependency against a group covers every task beneath that group at any depth, so one against an empty group covers nothing.
/// A dependency declared on a group applies to every task beneath that group.
/// Where that gives one task several dependencies on one upstream task, they are one dependency, and requires wins.
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
            .ToLookup(wait => wait.Upstream, wait => wait.Dependent);
    }

    /// <summary>
    /// The dependencies declared on one node, one for each upstream, in the order each upstream was first named.
    /// </summary>
    public IReadOnlyList<Dependency> DeclaredOn(Node node)
        =>
        [
            .. _declaredOn[node]
                .GroupBy(dependency => dependency.Upstream, dependency => dependency.Kind)
                .Select(kinds => new Dependency(kinds.Key.Path, kinds.Strongest())),
        ];

    /// <summary>Every task a task waits for, in declaration order.</summary>
    public ImmutableArray<Upstream> UpstreamsOf(TaskNode task) => _upstreams[task];

    /// <summary>The positions of the tasks that wait for a task, in declaration order.</summary>
    public ImmutableArray<int> DependentsOf(TaskNode task) => [.. _dependents[_positions[task]]];

    private ImmutableArray<Upstream> ResolveUpstreams(TaskNode task)
        =>
        [
            .. task.Ancestors()
                .Prepend<Node>(task)
                .SelectMany(node => _declaredOn[node])
                .SelectMany(dependency => dependency.Upstream.CoveredTasks().Select(upstream => (Position: _positions[upstream], dependency.Kind)))
                .GroupBy(covered => covered.Position, covered => covered.Kind)
                .Select(kinds => new Upstream(kinds.Key, kinds.Strongest()))
                .OrderBy(upstream => upstream.Position),
        ];
}
