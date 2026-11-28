namespace Dagline;

/// <summary>
/// How many of each task's dependencies are not met, kept up to date by propagation (spec #139): a node that reaches a status
/// tells its dependents once, and each dependency the status meets is counted off. A pending task whose count reaches zero has
/// every dependency met, of either kind, including those declared on a group that holds it.
/// </summary>
/// <remarks>
/// Each task's count starts at the number of its upstreams, which the graph has already merged to one dependency for each
/// upstream named. A settled node meets an <see cref="DependencyKind.After"/> dependency on it, and only a succeeded one meets a
/// <see cref="DependencyKind.Requires"/> dependency, as <see cref="NodeStatusRules.Meets"/> says, so a dependency on a node that
/// settles in any other way stays unmet: blocking the dependent is decided apart, from <see cref="RequiringOf"/>.
/// </remarks>
internal sealed class UnmetDependencies
{
    private readonly int[] _unmet;
    private readonly bool[] _told;

    // For each node, the tasks that depend on it and by which kind, in declaration order. Kept here, not looked up in each
    // dependent's upstreams, so that a node many tasks depend on tells each in one step.
    private readonly (BuiltTask Dependent, DependencyKind Kind)[][] _dependentsOf;

    public UnmetDependencies(Graph graph)
    {
        _unmet = new int[graph.Nodes.Length];
        _told = new bool[graph.Nodes.Length];
        var dependentsOf = new List<(BuiltTask, DependencyKind)>?[graph.Nodes.Length];
        foreach (var task in graph.Tasks)
        {
            _unmet[task.Position] = task.Upstreams.Length;
            foreach (var upstream in task.Upstreams)
            {
                (dependentsOf[upstream.Position] ??= []).Add((task, upstream.Kind));
            }
        }

        _dependentsOf = [.. dependentsOf.Select(dependents => dependents?.ToArray() ?? [])];
    }

    /// <summary>Whether every dependency of <paramref name="task"/> is met.</summary>
    public bool AreMet(BuiltTask task) => _unmet[task.Position] == 0;

    /// <summary>
    /// The node at <paramref name="position"/> has settled with <paramref name="status"/>: each dependency on it that the status
    /// meets is counted off, and each dependent whose count that brings to zero is added to <paramref name="nowMet"/>. A node tells
    /// its dependents once: telling again changes nothing.
    /// </summary>
    public void Settled(int position, NodeStatus status, ICollection<BuiltTask> nowMet)
    {
        if (_told[position])
        {
            return;
        }

        _told[position] = true;
        foreach (var (dependent, kind) in _dependentsOf[position])
        {
            if (status.Meets(kind) && --_unmet[dependent.Position] == 0)
            {
                nowMet.Add(dependent);
            }
        }
    }

    /// <summary>The tasks that require the node at <paramref name="position"/>, in declaration order.</summary>
    public IEnumerable<BuiltTask> RequiringOf(int position)
    {
        foreach (var (dependent, kind) in _dependentsOf[position])
        {
            if (kind is DependencyKind.Requires)
            {
                yield return dependent;
            }
        }
    }
}
