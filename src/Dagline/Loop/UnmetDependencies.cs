namespace Dagline;

/// <summary>
/// What a dependency means, as one propagation over the graph (spec #139): a node that reaches a status tells its dependents once.
/// Each task keeps a count of the dependencies it has that are not met; a pending task whose count reaches zero has every
/// dependency met, of either kind, including those declared on a group that holds it. A node that cannot succeed tells the tasks
/// that require it, which are blocked and tell theirs in turn.
/// </summary>
/// <remarks>
/// <para>
/// Each task's count starts at the number of its upstreams, which the graph has already merged to one dependency for each upstream
/// named, requires winning. A settled node meets an <see cref="DependencyKind.After"/> dependency on it, and only a succeeded one
/// meets a <see cref="DependencyKind.Requires"/> dependency, as <see cref="NodeStatusRules.Meets"/> says.
/// </para>
/// <para>
/// The triggers differ and the propagation is the same. <see cref="Settled"/> is told by a task as it settles and by a group as the
/// roll-up settles it, and at the start of the run by every node already settled. <see cref="Failed"/> is told by a task that
/// fails: it, and each group that holds it, cannot succeed from then on, and neither can a task blocked by it, so a group cannot
/// succeed from its first failure beneath it, long before it settles. A cancelled task blocks nothing: a dependency that requires
/// it stays unmet.
/// </para>
/// </remarks>
internal sealed class UnmetDependencies
{
    private readonly int[] _unmet;
    private readonly bool[] _settledTold;

    // The nodes a failure has reached: a failed or blocked task, and every group that holds one. Each tells its requirers once.
    private readonly bool[] _cannotSucceed;
    private readonly Queue<int> _unvisited = new();

    // For each node, the tasks that depend on it and by which kind, in declaration order. Kept here, not looked up in each
    // dependent's upstreams, so that a node many tasks depend on tells each in one pass.
    private readonly (BuiltTask Dependent, DependencyKind Kind)[][] _dependentsOf;

    public UnmetDependencies(Graph graph)
    {
        _unmet = new int[graph.Nodes.Length];
        _settledTold = new bool[graph.Nodes.Length];
        _cannotSucceed = new bool[graph.Nodes.Length];
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

    /// <summary>
    /// The node at <paramref name="position"/> has settled with <paramref name="status"/>: each dependency on it that the status
    /// meets is counted off, and each dependent whose count that brings to zero is added to <paramref name="nowMet"/>, whatever
    /// its status. A node tells this once: telling again changes nothing.
    /// </summary>
    public void Settled(int position, NodeStatus status, List<BuiltTask> nowMet)
    {
        if (_settledTold[position])
        {
            return;
        }

        _settledTold[position] = true;
        foreach (var (dependent, kind) in _dependentsOf[position])
        {
            if (status.Meets(kind) && --_unmet[dependent.Position] == 0)
            {
                nowMet.Add(dependent);
            }
        }
    }

    /// <summary>
    /// A task has failed. Adds to <paramref name="blocked"/>, in declaration order, every task for which
    /// <paramref name="canBeBlocked"/> holds that requires it: directly, through a group that holds it, or through a task that this
    /// blocks. A node an earlier failure reached is passed over, with everything that requires it: that failure blocked them first.
    /// </summary>
    /// <param name="failed">The task that failed.</param>
    /// <param name="canBeBlocked">Whether a task can still be blocked: whether it is pending.</param>
    /// <param name="blocked">Where the tasks to block are added.</param>
    public void Failed(BuiltTask failed, Func<BuiltTask, bool> canBeBlocked, List<BuiltTask> blocked)
    {
        var first = blocked.Count;
        Reach(failed);
        while (_unvisited.TryDequeue(out var position))
        {
            foreach (var (dependent, kind) in _dependentsOf[position])
            {
                if (kind is DependencyKind.Requires && !_cannotSucceed[dependent.Position] && canBeBlocked(dependent))
                {
                    blocked.Add(dependent);
                    Reach(dependent);
                }
            }
        }

        blocked.Sort(first, blocked.Count - first, ByPosition.Instance);
    }

    /// <summary>Marks a task that cannot succeed, and each group that holds it, and queues each not reached before.</summary>
    private void Reach(BuiltTask task)
    {
        ReachNode(task.Position);
        foreach (var group in task.Groups)
        {
            ReachNode(group);
        }
    }

    private void ReachNode(int position)
    {
        if (!_cannotSucceed[position])
        {
            _cannotSucceed[position] = true;
            _unvisited.Enqueue(position);
        }
    }

    /// <summary>Declaration order.</summary>
    private sealed class ByPosition : IComparer<BuiltTask>
    {
        public static ByPosition Instance { get; } = new();

        public int Compare(BuiltTask? x, BuiltTask? y) => x!.Position.CompareTo(y!.Position);
    }
}
