namespace Dagline;

/// <summary>
/// One run of a graph, and the single owner of everything that changes during it.
/// Every change is applied under one lock, one at a time, so the order of the run's events
/// is a function of the order in which task bodies end.
/// </summary>
internal sealed class Run : ISnapshotSource
{
    private readonly Lock _gate = new();
    private readonly Graph _graph;
    private readonly TimeProvider _timeProvider;
    private readonly ReporterDelivery _delivery;
    private readonly NodeSnapshot[] _nodes;
    private readonly StatusCounts _counts;
    private readonly TaskCompletionSource<RunOutcome> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DateTimeOffset _startedAt;
    private DateTimeOffset? _endedAt;
    private RunOutcome? _outcome;
    private volatile Snapshot _current;

    public Run(Graph graph, TimeProvider timeProvider, ReporterDelivery delivery)
    {
        _graph = graph;
        _timeProvider = timeProvider;
        _delivery = delivery;
        _nodes = [.. graph.Tasks.Select(task => new NodeSnapshot(task.Path, task.Dependencies))];
        _counts = new StatusCounts(pending: graph.Tasks.Length);
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
            Move(task, status, now);
            Advance(task.Dependents.Select(_graph.TaskAt), now);
        }
    }

    /// <summary>
    /// Applies what follows from one change, causes before effects: the tasks among <paramref name="waiting"/>
    /// whose dependencies are now met become ready, the ready tasks start, and the run ends when nothing is left to do.
    /// </summary>
    private void Advance(IEnumerable<GraphTask> waiting, DateTimeOffset now)
    {
        var ready = MakeReady(waiting, now);
        StartAll(ready, now);

        if (_counts[NodeStatus.Running] == 0 && _counts[NodeStatus.Ready] == 0)
        {
            End(now);
        }
    }

    private List<GraphTask> MakeReady(IEnumerable<GraphTask> waiting, DateTimeOffset now)
    {
        List<GraphTask> ready = [.. waiting.Where(DependenciesAreMet)];
        foreach (var task in ready)
        {
            Move(task, NodeStatus.Ready, now);
        }

        return ready;
    }

    private bool DependenciesAreMet(GraphTask task)
        => task.Upstreams.All(upstream => _nodes[upstream.Position].Status.Meets(upstream.Kind));

    private void StartAll(List<GraphTask> ready, DateTimeOffset now)
    {
        foreach (var task in ready)
        {
            Move(task, NodeStatus.Running, now);
            _ = Task.Run(() => RunBodyAsync(task));
        }
    }

    // No token: a run cannot be stopped, and a task body takes none.
    private async Task RunBodyAsync(GraphTask task)
    {
        await task.Body(new TaskContext()).ConfigureAwait(false);
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
            Started = to is NodeStatus.Running ? now : before.Started,
            Settled = to.IsSettled() ? now : before.Settled,
        };

        _nodes[task.Position] = after;
        _counts.Move(before.Status, to);
        Publish(new TaskStatusChanged(now, after, before.Status, to));
    }

    private void Publish(RunEvent runEvent)
    {
        _current = TakeSnapshot();
        _delivery.Publish(runEvent, _current);
    }

    private Snapshot TakeSnapshot() => new(_startedAt, _endedAt, _outcome, [.. _nodes], _graph.Positions, _counts.ToDictionary());
}
