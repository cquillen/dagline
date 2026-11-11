using System.Collections.Frozen;

namespace Dagline;

/// <summary>
/// One run of a graph, and the single owner of everything that changes during it.
/// Every change is applied under one lock, one at a time, so the order of the run's events
/// is a function of the order in which task bodies end.
/// </summary>
/// <remarks>
/// A change and everything that follows from it are one step: the step publishes its events in order, each with
/// the snapshot as of that event, before the next change is looked at. No consumer code runs under the lock:
/// task bodies are started on the thread pool, and reporters are called by <see cref="ReporterDelivery"/>.
/// </remarks>
internal sealed class Run : ISnapshotSource
{
    private readonly Lock _gate = new();
    private readonly Graph _graph;
    private readonly TimeProvider _timeProvider;
    private readonly ReporterDelivery _delivery;
    private readonly NodeSnapshot[] _nodes;
    private readonly StatusCounts _counts;
    private readonly FrozenDictionary<int, StatusCounts> _groupCounts;
    private readonly List<GraphGroup> _settledInThisStep = [];
    private readonly SortedSet<int> _ready = [];
    private readonly HashSet<int> _reportedFailures = [];
    private readonly Slots _slots;
    private readonly TaskCompletionSource<RunOutcome> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DateTimeOffset _startedAt;
    private DateTimeOffset? _endedAt;
    private RunOutcome? _outcome;
    private volatile Snapshot? _current;

    public Run(Graph graph, LimitSizes sizes, TimeProvider timeProvider, ReporterDelivery delivery)
    {
        _graph = graph;
        _slots = new Slots(graph.LimitNames, sizes);
        _timeProvider = timeProvider;
        _delivery = delivery;
        _counts = new StatusCounts(pending: graph.Tasks.Length);
        _groupCounts = graph.Nodes.OfType<GraphGroup>().ToFrozenDictionary(
            group => group.Position,
            group => new StatusCounts(pending: group.TaskCount));
        _nodes = [.. graph.Nodes.Select(AtTheStart)];
        _startedAt = timeProvider.GetUtcNow();
    }

    public Snapshot Current => _current ?? TakeCurrent();

    /// <summary>Completes with the run outcome once no task is running and none is ready.</summary>
    public Task<RunOutcome> Ended => _ended.Task;

    public void Start()
    {
        lock (_gate)
        {
            Publish(new RunStarted(_startedAt, this));
            Advance(_graph.Tasks, _startedAt);
        }
    }

    /// <summary>
    /// Applies what a running task reports about itself. A report from a task that has settled changes nothing.
    /// </summary>
    /// <param name="task">The task that reports.</param>
    /// <param name="report">Gives the task as it is with the report applied, from the task as it was.</param>
    public void Report(GraphTask task, Func<NodeSnapshot, NodeSnapshot> report)
    {
        lock (_gate)
        {
            Apply(task, report);
        }
    }

    /// <summary>
    /// Applies a report that is a reported failure: from then on the task settles failed as a reported failure, however its body ends.
    /// </summary>
    /// <param name="task">The task that reports.</param>
    /// <param name="report">Gives the task as it is with the report applied, from the task as it was.</param>
    public void ReportFailure(GraphTask task, Func<NodeSnapshot, NodeSnapshot> report)
    {
        lock (_gate)
        {
            if (Apply(task, report))
            {
                _reportedFailures.Add(task.Position);
            }
        }
    }

    /// <returns><see langword="true"/> when the report was applied: the task is running.</returns>
    private bool Apply(GraphTask task, Func<NodeSnapshot, NodeSnapshot> report)
    {
        var before = _nodes[task.Position];
        if (before.Status is not NodeStatus.Running)
        {
            return false;
        }

        var after = report(before);
        _nodes[task.Position] = after;
        _current = null;

        // A warning that was kept is the one report that is also an event.
        foreach (var warning in after.Warnings.Skip(before.Warnings.Count))
        {
            Publish(new WarningAdded(_timeProvider.GetUtcNow(), task.Path, warning));
        }

        return true;
    }

    /// <summary>Settles a task whose body has ended. A reported failure the task recorded wins over the way the body ended.</summary>
    private void Settle(GraphTask task, TaskOutcome ended)
    {
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            var outcome = _reportedFailures.Remove(task.Position) ? TaskOutcome.ReportedFailure : ended;
            _slots.GiveBack(task);
            Move(task, outcome, now);
            var blocked = outcome.Status is NodeStatus.Failed ? Block(task, now) : [];
            Advance(WaitingFor([task, .. blocked]), now);
        }
    }

    /// <summary>
    /// Blocks every pending task that requires a task that has just failed: directly, through a group that holds it,
    /// or through a task that this blocks. Each records the failed task as its cause.
    /// </summary>
    /// <returns>The tasks blocked, in declaration order.</returns>
    private List<GraphTask> Block(GraphTask failed, DateTimeOffset now)
    {
        List<GraphTask> blocked = [.. PendingTasksThatRequire(failed).Order().Select(_graph.TaskAt)];
        var outcome = TaskOutcome.Blocked(failed.Path);
        foreach (var task in blocked)
        {
            Move(task, outcome, now);
        }

        return blocked;
    }

    /// <summary>
    /// The positions of the pending tasks that cannot run now that a task has failed. A task that is already blocked is passed over,
    /// with everything that requires it: an earlier failure reached it first.
    /// </summary>
    private HashSet<int> PendingTasksThatRequire(GraphTask failed)
    {
        HashSet<int> found = [];
        HashSet<int> cannotSucceed = [];
        Queue<GraphNode> unvisited = new();
        Reach(failed);
        while (unvisited.TryDequeue(out var node))
        {
            foreach (var dependent in _graph.Requiring(node).Where(IsPending))
            {
                if (found.Add(dependent.Position))
                {
                    Reach(dependent);
                }
            }
        }

        return found;

        // A group is visited once, however many of the tasks beneath it the failure reaches.
        void Reach(GraphTask task)
        {
            foreach (var taskOrGroup in _graph.WithItsGroups(task))
            {
                if (cannotSucceed.Add(taskOrGroup.Position))
                {
                    unvisited.Enqueue(taskOrGroup);
                }
            }
        }
    }

    private bool IsPending(GraphTask task) => _nodes[task.Position].Status is NodeStatus.Pending;

    /// <summary>
    /// The tasks, in declaration order, that wait for one of the tasks that have just settled or for a group that settled with them.
    /// </summary>
    private IEnumerable<GraphTask> WaitingFor(IEnumerable<GraphTask> settled)
        => settled
            .Concat<GraphNode>(_settledInThisStep)
            .SelectMany(node => node.Dependents)
            .Distinct()
            .Order()
            .Select(_graph.TaskAt);

    /// <summary>
    /// Applies what follows from one change, causes before effects: the tasks among <paramref name="waiting"/>
    /// whose dependencies are now met become ready, the groups that the step settled are announced,
    /// the ready tasks that fit start, and the run ends when nothing is left to do.
    /// </summary>
    private void Advance(IEnumerable<GraphTask> waiting, DateTimeOffset now)
    {
        MakeReady(waiting, now);
        AnnounceSettledGroups(now);
        StartWhatFits(now);

        if (_counts[NodeStatus.Running] == 0 && _counts[NodeStatus.Ready] == 0)
        {
            End(now);
        }
    }

    private void MakeReady(IEnumerable<GraphTask> waiting, DateTimeOffset now)
    {
        List<GraphTask> ready = [.. waiting.Where(DependenciesAreMet)];
        foreach (var task in ready)
        {
            Move(task, NodeStatus.Ready, now);
            _ready.Add(task.Position);
        }
    }

    private bool DependenciesAreMet(GraphTask task)
        => task.Upstreams.All(upstream => _nodes[upstream.Position].Status.Meets(upstream.Kind));

    /// <summary>Publishes one event for each group the current step settled: the deepest first, then in declaration order.</summary>
    private void AnnounceSettledGroups(DateTimeOffset now)
    {
        foreach (var group in _settledInThisStep.OrderByDescending(group => group.Depth).ThenBy(group => group.Position))
        {
            var settled = _nodes[group.Position];
            Publish(new GroupSettled(now, group.Path, settled.Status, settled.Counts));
        }

        _settledInThisStep.Clear();
    }

    /// <summary>
    /// Tries every ready task in declaration order and starts each one that finds a slot free in the cap and in every limit it holds.
    /// One that does not fit is passed over and stays ready, so it never holds up the tasks behind it.
    /// </summary>
    private void StartWhatFits(DateTimeOffset now)
    {
        foreach (var task in _ready.Select(_graph.TaskAt).ToList())
        {
            if (_slots.TryTake(task))
            {
                _ready.Remove(task.Position);
                Move(task, NodeStatus.Running, now);
                _ = Task.Run(() => RunBodyAsync(task));
            }
        }
    }

    // No token: a run cannot be stopped, and a task body takes none.
    private async Task RunBodyAsync(GraphTask task)
    {
        var outcome = await InvokeBodyAsync(task).ConfigureAwait(false);
        Settle(task, outcome);
    }

    /// <summary>
    /// Invokes the body of a task and says how it ended. This is the one place consumer code of a task is invoked,
    /// and so the one catch-all: whatever escapes the body, a cancellation exception included, is an unexpected failure
    /// of that task and of nothing else.
    /// </summary>
    private async Task<TaskOutcome> InvokeBodyAsync(GraphTask task)
    {
        try
        {
            await task.Body(new TaskContext(this, task, _timeProvider)).ConfigureAwait(false);
            return TaskOutcome.Succeeded;
        }
        catch (Exception escaped)
        {
            return TaskOutcome.UnexpectedFailure(escaped);
        }
    }

    private void End(DateTimeOffset now)
    {
        var outcome = _counts[NodeStatus.Succeeded] == _graph.Tasks.Length ? RunOutcome.Complete : RunOutcome.Incomplete;
        _endedAt = now;
        _outcome = outcome;
        Publish(new RunEnded(now, outcome));
        _delivery.Complete();
        _ended.SetResult(outcome);
    }

    /// <summary>Moves a task on to a status in which it has not settled: ready, or running.</summary>
    private void Move(GraphTask task, NodeStatus to, DateTimeOffset now)
    {
        var before = _nodes[task.Position];
        Replace(task, before with { Status = to, Started = to is NodeStatus.Running ? now : before.Started }, now);
    }

    /// <summary>Settles a task: moves it to the status of its outcome and freezes that outcome on it.</summary>
    private void Move(GraphTask task, TaskOutcome outcome, DateTimeOffset now)
    {
        Replace(task, outcome.Settle(_nodes[task.Position], now), now);
    }

    /// <summary>
    /// Puts a task as it is after a change of status in place of the task as it was, counts the change in the run
    /// and in every group that holds the task, and publishes it.
    /// </summary>
    private void Replace(GraphTask task, NodeSnapshot changed, DateTimeOffset now)
    {
        var from = _nodes[task.Position].Status;
        var to = changed.Status;
        var after = changed with { Counts = StatusCounts.OfOneTask(to) };

        _nodes[task.Position] = after;
        _counts.Move(from, to);
        foreach (var group in task.Groups.Select(_graph.GroupAt))
        {
            MoveBeneath(group, from, to);
        }

        Publish(new TaskStatusChanged(now, after, from, to));
    }

    /// <summary>
    /// Counts one task's change of status in a group that holds it, and derives the group's status again.
    /// Only a task that had not settled changes status, so a group that comes out settled has settled with this change.
    /// </summary>
    private void MoveBeneath(GraphGroup group, NodeStatus from, NodeStatus to)
    {
        var counts = _groupCounts[group.Position];
        counts.Move(from, to);
        var status = counts.GroupStatus;
        _nodes[group.Position] = _nodes[group.Position] with { Status = status, Counts = counts.ToDictionary() };

        if (status.IsSettled())
        {
            _settledInThisStep.Add(group);
        }
    }

    private NodeSnapshot AtTheStart(GraphNode node)
        => _groupCounts.TryGetValue(node.Position, out var counts)
            ? new NodeSnapshot(node, counts.GroupStatus, counts.ToDictionary())
            : new NodeSnapshot(node, NodeStatus.Pending, StatusCounts.OfOneTask(NodeStatus.Pending));

    private void Publish(RunEvent runEvent)
    {
        var snapshot = TakeSnapshot();
        _current = snapshot;
        _delivery.Publish(runEvent, snapshot);
    }

    // A progress report only marks the current snapshot as out of date, so a task that reports often costs
    // one snapshot for each read and not one for each report.
    private Snapshot TakeCurrent()
    {
        lock (_gate)
        {
            return _current ??= TakeSnapshot();
        }
    }

    private Snapshot TakeSnapshot()
        => new(_startedAt, _endedAt, _outcome, [.. _nodes], _graph.Positions, _counts.ToDictionary(), _slots.Cap, _slots.Limits);
}
