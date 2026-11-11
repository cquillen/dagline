using System.Collections.Immutable;

namespace Dagline;

/// <summary>
/// Works out which tasks each task waits for, from the dependencies as they were declared on handles.
/// </summary>
/// <remarks>
/// A dependency against a group covers every task beneath that group at any depth, so one against an empty group covers nothing.
/// A dependency declared on a group applies to every task beneath that group.
/// </remarks>
internal sealed class TaskDependencies
{
    private readonly Dictionary<Node, int> _positions;
    private readonly ILookup<Node, DeclaredDependency> _declaredOn;
    private readonly ILookup<Node, int> _tasksBeneath;
    private readonly Dictionary<TaskNode, ImmutableArray<Upstream>> _upstreams;
    private readonly ILookup<int, int> _dependents;

    /// <param name="nodes">Every node of the graph, in declaration order.</param>
    /// <param name="declared">Every dependency, in the order declared.</param>
    public TaskDependencies(ImmutableArray<Node> nodes, IReadOnlyList<DeclaredDependency> declared)
    {
        List<TaskNode> tasks = [.. nodes.OfType<TaskNode>()];

        _positions = nodes.Index().ToDictionary(node => node.Item, node => node.Index);
        _declaredOn = declared.ToLookup(dependency => dependency.Dependent);
        _tasksBeneath = tasks
            .SelectMany(task => task.Ancestors().Select(group => (Group: (Node)group, Task: _positions[task])))
            .ToLookup(beneath => beneath.Group, beneath => beneath.Task);
        _upstreams = tasks.ToDictionary(task => task, ResolveUpstreams);
        _dependents = tasks
            .SelectMany(task => _upstreams[task].Select(upstream => (Upstream: upstream.Position, Dependent: _positions[task])))
            .ToLookup(wait => wait.Upstream, wait => wait.Dependent);
    }

    /// <summary>The dependencies declared on one node, in the order declared.</summary>
    public IReadOnlyList<Dependency> DeclaredOn(Node node)
        => [.. _declaredOn[node].Select(dependency => new Dependency(dependency.Upstream.Path, dependency.Kind))];

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
                .SelectMany(dependency => TasksCoveredBy(dependency.Upstream).Select(position => new Upstream(position, dependency.Kind)))
                .Distinct()
                .OrderBy(upstream => upstream.Position),
        ];

    private IEnumerable<int> TasksCoveredBy(Node node) => node is TaskNode ? [_positions[node]] : _tasksBeneath[node];
}
