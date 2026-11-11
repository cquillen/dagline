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
    private readonly Slots _slots;
    private readonly TaskCompletionSource<RunOutcome> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DateTimeOffset _startedAt;
    private DateTimeOffset? _endedAt;
    private RunOutcome? _outcome;
    private volatile Snapshot _current;

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
        _current = TakeSnapshot();
    }

    public Snapshot Current => _current;

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

    private void Settle(GraphTask task, NodeStatus status)
    {
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            _slots.GiveBack(task);
            Move(task, status, now);
            Advance(WaitingFor(task), now);
        }
    }

    /// <summary>
    /// The tasks, in declaration order, that wait for a task that has just settled or for a group that settled with it.
    /// </summary>
    private IEnumerable<GraphTask> WaitingFor(GraphTask settled)
        => _settledInThisStep
            .Prepend<GraphNode>(settled)
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
        foreach (var task in waiting.Where(DependenciesAreMet).ToList())
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
    /// Tries every ready task in declaration order and starts each one that finds all its slots free.
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
        await task.Body(new TaskContext(task.Path, _timeProvider)).ConfigureAwait(false);
        Settle(task, NodeStatus.Succeeded);
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

    private void Move(GraphTask task, NodeStatus to, DateTimeOffset now)
    {
        var before = _nodes[task.Position];
        var after = before with
        {
            Status = to,
            Counts = StatusCounts.OfOneTask(to),
            Started = to is NodeStatus.Running ? now : before.Started,
            Settled = to.IsSettled() ? now : before.Settled,
        };

        _nodes[task.Position] = after;
        _counts.Move(before.Status, to);
        foreach (var group in task.Groups.Select(_graph.GroupAt))
        {
            MoveBeneath(group, before.Status, to);
        }

        Publish(new TaskStatusChanged(now, after, before.Status, to));
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
        _current = TakeSnapshot();
        _delivery.Publish(runEvent, _current);
    }

    private Snapshot TakeSnapshot()
        => new(_startedAt, _endedAt, _outcome, [.. _nodes], _graph.Positions, _counts.ToDictionary(), _slots.Cap, _slots.Limits);
}
