namespace Dagline;

/// <summary>
/// The roll-up of one run: the run's counts, derived from all its tasks, and each group's status, counts, waiting and warnings,
/// derived from the tasks beneath it. <see cref="RunSteps"/> tells it each change to a task, and asks it what the run's counts are,
/// what status and counts a group has, which groups settled and how each changed group now looks.
/// </summary>
/// <remarks>
/// <para>
/// It is told every change to a task, and works out for itself what the change means: a change of status and the begin or end of a
/// wait are counted in the run and in every group above the task, and a warning, kept or beyond the number kept, is counted in every
/// group above the task, from the task's warning count. Any other change, such as progress, touches no group.
/// </para>
/// <para>
/// A task may be given warnings very often, so a group is recorded as changed once until the changes are handed over, a group's
/// counts are copied once until they next change, and the groups above a task are walked without allocating.
/// It is not thread-safe: <see cref="RunSteps"/> owns it.
/// </para>
/// </remarks>
internal sealed class RollUp
{
    // A group that has not settled shows the furthest any of its tasks has got, and a settled one the worst that happened (ADR 0003).
    private static readonly NodeStatus[] _groupStatusPrecedence =
    [
        NodeStatus.Running,
        NodeStatus.Ready,
        NodeStatus.Pending,
        NodeStatus.Failed,
        NodeStatus.Cancelled,
        NodeStatus.Blocked,
    ];

    private readonly Graph _graph;
    private readonly StatusCounts _run;

    // Each indexed by position among the nodes, so a task's place in the group arrays, and a group's in the task arrays, is unused.
    private readonly StatusCounts?[] _groupCounts;
    private readonly int[] _warningsBeneath;
    private readonly int[] _warningsOf;
    private readonly bool[] _isChanged;

    private readonly List<int> _changed = [];
    private readonly List<BuiltGroup> _settled = [];

    /// <summary>Seeds the roll-up, in one pass, from every task as it is when the run starts.</summary>
    /// <param name="graph">The graph the run runs.</param>
    /// <param name="atTheStart">
    /// Each task as a snapshot shows it at the start, indexed by position among the nodes. A task succeeded at the start is an
    /// earlier success, and the warnings it holds are those it succeeded with.
    /// </param>
    public RollUp(Graph graph, IReadOnlyList<NodeSnapshot?> atTheStart)
    {
        _graph = graph;
        var nodes = graph.Nodes.Length;
        _warningsBeneath = new int[nodes];
        _warningsOf = new int[nodes];
        _isChanged = new bool[nodes];

        var earlierSuccesses = new int[nodes];
        var earlierInRun = 0;
        foreach (var task in graph.Tasks)
        {
            var node = atTheStart[task.Position]!;
            _warningsOf[task.Position] = node.WarningCount;
            if (node.Status is not NodeStatus.Succeeded)
            {
                continue;
            }

            earlierInRun++;
            foreach (var group in task.Groups)
            {
                earlierSuccesses[group]++;
                _warningsBeneath[group] += node.WarningCount;
            }
        }

        _run = new StatusCounts(pending: graph.Tasks.Length - earlierInRun, earlierSuccesses: earlierInRun);
        _groupCounts = new StatusCounts?[nodes];
        foreach (var group in graph.Nodes.OfType<BuiltGroup>())
        {
            var earlier = earlierSuccesses[group.Position];
            _groupCounts[group.Position] = new StatusCounts(pending: group.TaskCount - earlier, earlierSuccesses: earlier);
        }
    }

    /// <summary>The groups that have settled since <see cref="TakeSettled"/> was last asked, in the order they settled.</summary>
    public IReadOnlyList<BuiltGroup> Settled => _settled;

    /// <summary>How many of the run's tasks have <paramref name="status"/> now.</summary>
    public int InRun(NodeStatus status) => _run[status];

    /// <summary>
    /// The run's counts as they are now. Until they next change, every call gives the same copy, so the snapshots of events that
    /// change no count share one.
    /// </summary>
    public TaskCounts RunCounts => _run.ToTaskCounts();

    /// <summary>A group's counts as they are now, from the tasks beneath it at any depth.</summary>
    public TaskCounts CountsOf(BuiltGroup group) => _groupCounts[group.Position]!.ToTaskCounts();

    /// <summary>
    /// The status of a group, from the tasks beneath it at any depth (ADR 0003). While any task has not settled: running if any is
    /// running, else ready if any is ready, else pending. Once all have settled: failed if any failed, else cancelled if any was
    /// cancelled, else blocked if any is blocked, else succeeded. A group with no tasks is therefore succeeded.
    /// </summary>
    /// <param name="group">The group's position among the nodes.</param>
    public NodeStatus StatusOf(int group)
    {
        var counts = _groupCounts[group]!;
        foreach (var status in _groupStatusPrecedence)
        {
            if (counts[status] > 0)
            {
                return status;
            }
        }

        return NodeStatus.Succeeded;
    }

    /// <summary>
    /// A group as a snapshot shows it now: its status, counts, waiting or working and warnings are those of the tasks beneath it,
    /// and nothing else is attached to it. While the group's counts are unchanged, its snapshots share one counts value.
    /// </summary>
    public NodeSnapshot SnapshotOf(BuiltGroup group)
        => new(group, StatusOf(group.Position), CountsOf(group))
        {
            WarningsBeneath = _warningsBeneath[group.Position],
        };

    /// <summary>
    /// A task has changed: <paramref name="change"/> holds the task as it is now and the event the change produced, if any.
    /// The kind of change is decided once, from its event: a change of status, or the begin or end of a wait, is counted in the run
    /// and in every group above the task. A warning, whether kept or beyond the number kept, is then counted in every group above
    /// the task, from the task's warning count. Each group so changed is recorded, once, for <see cref="HandOverChanged"/>, and each
    /// one a change of status settled for <see cref="TakeSettled"/>.
    /// </summary>
    public void TaskChanged(BuiltTask task, TaskChange change)
    {
        switch (change.Event)
        {
            case TaskStatusChanged moved:
                CountMove(task, moved);
                break;
            case WaitingChanged waited:
                CountWait(task, waited);
                break;
        }

        CountWarnings(task, change.Task.WarningCount);
    }

    private void CountMove(BuiltTask task, TaskStatusChanged moved)
    {
        _run.Move(moved.From, moved.To);
        foreach (var group in task.Groups)
        {
            _groupCounts[group]!.Move(moved.From, moved.To);
            MarkChanged(group);

            // Only a task that had not settled changes status, so a group that comes out settled has settled with this change.
            if (StatusOf(group).IsSettled())
            {
                _settled.Add(_graph.GroupAt(group));
            }
        }
    }

    private void CountWait(BuiltTask task, WaitingChanged waited)
    {
        _run.Count(waited);
        foreach (var group in task.Groups)
        {
            _groupCounts[group]!.Count(waited);
            MarkChanged(group);
        }
    }

    private void CountWarnings(BuiltTask task, int warningCount)
    {
        var warned = warningCount - _warningsOf[task.Position];
        if (warned == 0)
        {
            return;
        }

        _warningsOf[task.Position] = warningCount;
        foreach (var group in task.Groups)
        {
            _warningsBeneath[group] += warned;
            MarkChanged(group);
        }
    }

    private void MarkChanged(int group)
    {
        if (!_isChanged[group])
        {
            _isChanged[group] = true;
            _changed.Add(group);
        }
    }

    /// <summary>
    /// Hands each group changed since the last hand-over to <paramref name="snapshots"/>, as a snapshot shows it now,
    /// and forgets that it changed.
    /// </summary>
    public void HandOverChanged(SnapshotTaker snapshots)
    {
        foreach (var group in _changed)
        {
            snapshots.Record(group, SnapshotOf(_graph.GroupAt(group)));
            _isChanged[group] = false;
        }

        _changed.Clear();
    }

    /// <summary>
    /// The groups that have settled since this was last asked, in the order they are announced: the deepest first, then in
    /// declaration order. It forgets them.
    /// </summary>
    public IReadOnlyList<BuiltGroup> TakeSettled()
    {
        if (_settled.Count == 0)
        {
            return [];
        }

        List<BuiltGroup> settled = [.. _settled.OrderByDescending(group => group.Depth).ThenBy(group => group.Position)];
        _settled.Clear();
        return settled;
    }
}
