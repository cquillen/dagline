using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace Dagline;

/// <summary>
/// One run of a graph, and the single owner of everything that changes during it.
/// Every change is applied under one lock, one at a time, so the order of the run's events
/// is a function of the order in which task bodies end.
/// </summary>
/// <remarks>
/// <para>
/// A change and everything that follows from it are one step: the step publishes its events in order, each with
/// the snapshot as of that event, before the next change is looked at. No consumer code runs under the lock:
/// task bodies are started on the thread pool, and reporters are called by <see cref="ReporterDelivery"/>.
/// </para>
/// <para>
/// What a running task reports through its <see cref="TaskContext"/> is a change like any other and is applied under the same lock.
/// It goes straight onto the task as the snapshot shows it, and only a warning that is kept is published.
/// </para>
/// </remarks>
[SuppressMessage(
    "Reliability",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The stopping source has no timer and no wait handle is ever taken from it, and a body the run has ended may still hold its token.")]
internal sealed class Run : ISnapshotSource
{
    private readonly Lock _gate = new();
    private readonly Graph _graph;
    private readonly TimeProvider _timeProvider;
    private readonly ReporterDelivery _delivery;
    private readonly StatusCounts _counts;
    private readonly FrozenDictionary<int, StatusCounts> _groupCounts;
    private readonly List<BuiltGroup> _settledInThisStep = [];
    private readonly SortedSet<int> _ready = [];
    private readonly HashSet<int> _reportedFailures = [];
    private readonly Dictionary<int, int> _openWaitingScopes = [];
    private readonly Slots _slots;
    private readonly TaskCompletionSource<RunOutcome> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StopSignal? _signal;

    private readonly CancellationTokenSource _stopping = new();
    private readonly DateTimeOffset _startedAt;
    private CancellationTokenRegistration _hearsTheSignal;
    private StopState? _stop;
    private ChunkedList<NodeSnapshot> _nodes;
    private DateTimeOffset? _endedAt;
    private RunOutcome? _outcome;
    private volatile Snapshot? _current;

    public Run(Graph graph, LimitSizes sizes, TimeProvider timeProvider, ReporterDelivery delivery, StopSignal? signal)
    {
        _signal = signal;
        _graph = graph;
        _slots = new Slots(graph.LimitNames, sizes);
        _timeProvider = timeProvider;
        _delivery = delivery;
        _counts = new StatusCounts(pending: graph.Tasks.Length);
        _groupCounts = graph.Nodes.OfType<BuiltGroup>().ToFrozenDictionary(
            group => group.Position,
            group => new StatusCounts(pending: group.TaskCount));
        _nodes = ChunkedList<NodeSnapshot>.From(graph.Nodes.Select(AtTheStart));
        _startedAt = timeProvider.GetUtcNow();
    }

    public Snapshot Current => _current ?? TakeCurrent();

    /// <summary>Completes with the run outcome once no task is running and none is ready.</summary>
    public Task<RunOutcome> Ended => _ended.Task;

    /// <summary>
    /// Starts the run: the tasks with no dependency become ready and those that fit start. A signal already requested
    /// is applied once the tasks have become ready and before any starts, so the run starts nothing.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            Publish(new RunStarted(_startedAt, this));
            MakeReady(_graph.Tasks, _startedAt);
            AnnounceSettledGroups(_startedAt);
            if (_signal?.IsRequested == true)
            {
                ApplyStop(_startedAt);
            }

            StartWhatFitsOrEnd(_startedAt);
        }

        SignalTheTasksIfStopped();
        HearTheSignal();
    }

    /// <summary>
    /// Listens for the first request on the run's signal until the run ends. It is registered once the run has started,
    /// outside the lock, since a signal already requested calls back at once: a request made in between is heard then,
    /// and one already applied changes nothing.
    /// </summary>
    private void HearTheSignal()
    {
        if (_signal is null)
        {
            return;
        }

        var registration = _signal.FirstRequest.UnsafeRegister(static run => ((Run)run!).Stop(), this);
        lock (_gate)
        {
            if (_outcome is null)
            {
                _hearsTheSignal = registration;
                return;
            }
        }

        // The run ended before it could listen, so nothing else will let the signal forget it.
        registration.Unregister();
    }

    /// <summary>
    /// Applies the first request on the run's signal: from this step on nothing starts, and the run ends once nothing is running.
    /// A request heard after a stop has been applied, or after the run has ended, changes nothing.
    /// </summary>
    private void Stop()
    {
        lock (_gate)
        {
            if (_stop is not null || _outcome is not null)
            {
                return;
            }

            var now = _timeProvider.GetUtcNow();
            ApplyStop(now);
            StartWhatFitsOrEnd(now);
        }

        SignalTheTasksIfStopped();
    }

    private void ApplyStop(DateTimeOffset now)
    {
        // The wait for running tasks has no bound yet.
        _stop = new StopState(now, Timeout.InfiniteTimeSpan, Deadline: null);
        Publish(new StopRequested(now, _stop.GracePeriod));
    }

    /// <summary>
    /// Signals the token every task sees, once a stop has been applied. It is called outside the lock,
    /// because cancelling runs the callbacks that task bodies registered on the token.
    /// </summary>
    private void SignalTheTasksIfStopped()
    {
        if (Volatile.Read(ref _stop) is null)
        {
            return;
        }

        try
        {
            _stopping.Cancel();
        }
        catch (AggregateException)
        {
            // A callback a task body registered on its token threw. That is the body's affair, and the stop goes on.
        }
    }

    /// <summary>
    /// Applies what a running task reports about itself. A report from a task that has settled changes nothing.
    /// </summary>
    /// <param name="task">The task that reports.</param>
    /// <param name="report">Gives the task as it is with the report applied, from the task as it was.</param>
    public void Report(BuiltTask task, Func<NodeSnapshot, NodeSnapshot> report)
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
    public void ReportFailure(BuiltTask task, Func<NodeSnapshot, NodeSnapshot> report)
    {
        lock (_gate)
        {
            if (Apply(task, report))
            {
                _reportedFailures.Add(task.Position);
            }
        }
    }

    /// <summary>
    /// The status, as it is now, of a node that a task depends on. It answers whether or not the task has settled.
    /// </summary>
    /// <exception cref="ArgumentException">The task does not depend on the node.</exception>
    public NodeStatus StatusOf(BuiltTask task, Node dependency)
    {
        if (!_graph.TryGetDependency(task, dependency, out var position))
        {
            throw new ArgumentException(
                $"The task '{task.Path}' does not depend on the node '{dependency.Path}'. A task can read the status of an upstream "
                + "declared on it or on a group that holds it, and of a node beneath such an upstream.",
                nameof(dependency));
        }

        lock (_gate)
        {
            return _nodes[position].Status;
        }
    }

    /// <summary>
    /// Opens a waiting scope of a task. The task's first open scope begins its waiting marker, with the scope's text and the time.
    /// </summary>
    /// <returns><see langword="true"/> when the scope was opened: the task is running.</returns>
    public bool OpenWaitingScope(BuiltTask task, string? text)
    {
        lock (_gate)
        {
            var before = _nodes[task.Position];
            if (before.Status is not NodeStatus.Running)
            {
                return false;
            }

            var open = _openWaitingScopes.GetValueOrDefault(task.Position) + 1;
            _openWaitingScopes[task.Position] = open;
            if (open == 1)
            {
                var now = _timeProvider.GetUtcNow();
                _nodes = _nodes.With(task.Position, before with { WaitingSince = now, WaitingText = text });
                Publish(new WaitingChanged(now, task.Path, Waiting: true, text, Since: now));
            }

            return true;
        }
    }

    /// <summary>
    /// Closes one waiting scope of a task. Its last open scope ends the marker. Once the task has settled this changes nothing,
    /// since settling ended the marker.
    /// </summary>
    public void CloseWaitingScope(BuiltTask task)
    {
        lock (_gate)
        {
            if (!_openWaitingScopes.TryGetValue(task.Position, out var open))
            {
                return;
            }

            if (open > 1)
            {
                _openWaitingScopes[task.Position] = open - 1;
                return;
            }

            EndWaiting(task, _timeProvider.GetUtcNow());
        }
    }

    /// <summary>Ends the waiting marker of a task if it has one, however many of its scopes are still open.</summary>
    private void EndWaiting(BuiltTask task, DateTimeOffset now)
    {
        if (!_openWaitingScopes.Remove(task.Position))
        {
            return;
        }

        var waiting = _nodes[task.Position];
        _nodes = _nodes.With(task.Position, waiting with { WaitingSince = null, WaitingText = null });
        Publish(new WaitingChanged(now, task.Path, Waiting: false, waiting.WaitingText, waiting.WaitingSince!.Value));
    }

    /// <returns><see langword="true"/> when the report was applied: the task is running.</returns>
    private bool Apply(BuiltTask task, Func<NodeSnapshot, NodeSnapshot> report)
    {
        var before = _nodes[task.Position];
        if (before.Status is not NodeStatus.Running)
        {
            return false;
        }

        var after = report(before);
        _nodes = _nodes.With(task.Position, after);
        _current = null;

        // A warning that was kept is the one report that is also an event.
        foreach (var warning in after.Warnings.Skip(before.Warnings.Count))
        {
            Publish(new WarningAdded(_timeProvider.GetUtcNow(), task.Path, warning));
        }

        return true;
    }

    /// <summary>Settles a task whose body has ended. A reported failure the task recorded wins over the way the body ended.</summary>
    private void Settle(BuiltTask task, TaskOutcome ended)
    {
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            var outcome = _reportedFailures.Remove(task.Position) ? TaskOutcome.ReportedFailure
                : _stop is not null ? ended.OnceStopped()
                : ended;
            _slots.GiveBack(task);
            EndWaiting(task, now);
            Move(task, outcome, now);
            var blocked = outcome.Status is NodeStatus.Failed ? Block(task, now) : [];
            Advance(DependentsOf([task, .. blocked]), now);
        }
    }

    /// <summary>
    /// Blocks every pending task that requires a task that has just failed: directly, through a group that holds it,
    /// or through a task that this blocks. Each records the failed task as its cause.
    /// </summary>
    /// <returns>The tasks blocked, in declaration order.</returns>
    private List<BuiltTask> Block(BuiltTask failed, DateTimeOffset now)
    {
        List<BuiltTask> blocked = [.. PendingTasksThatRequire(failed).Order().Select(_graph.TaskAt)];
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
    private HashSet<int> PendingTasksThatRequire(BuiltTask failed)
    {
        HashSet<int> found = [];
        HashSet<int> cannotSucceed = [];
        Queue<BuiltNode> unvisited = new();
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
        void Reach(BuiltTask task)
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

    private bool IsPending(BuiltTask task) => _nodes[task.Position].Status is NodeStatus.Pending;

    /// <summary>
    /// The tasks, in declaration order, that depend on one of the tasks that have just settled or on a group that settled with them.
    /// </summary>
    private IEnumerable<BuiltTask> DependentsOf(IEnumerable<BuiltTask> settled)
        => settled
            .Concat<BuiltNode>(_settledInThisStep)
            .SelectMany(node => node.Dependents)
            .Distinct()
            .Order()
            .Select(_graph.TaskAt);

    /// <summary>
    /// Applies what follows from one change, causes before effects: the tasks among <paramref name="candidates"/>
    /// whose dependencies are now met become ready, the groups that the step settled are announced,
    /// the ready tasks that fit start, and the run ends when nothing is left to do.
    /// </summary>
    private void Advance(IEnumerable<BuiltTask> candidates, DateTimeOffset now)
    {
        MakeReady(candidates, now);
        AnnounceSettledGroups(now);
        StartWhatFitsOrEnd(now);
    }

    /// <summary>
    /// Starts the ready tasks that fit, unless a stop has been applied, and ends the run when nothing is left to do:
    /// no task is running and, unless a stop has been applied, none is ready.
    /// </summary>
    private void StartWhatFitsOrEnd(DateTimeOffset now)
    {
        if (_stop is null)
        {
            StartWhatFits(now);
        }

        if (_counts[NodeStatus.Running] == 0 && (_stop is not null || _counts[NodeStatus.Ready] == 0))
        {
            End(now);
        }
    }

    private void MakeReady(IEnumerable<BuiltTask> candidates, DateTimeOffset now)
    {
        List<BuiltTask> ready = [.. candidates.Where(DependenciesAreMet)];
        foreach (var task in ready)
        {
            Move(task, NodeStatus.Ready, now);
            _ready.Add(task.Position);
        }
    }

    private bool DependenciesAreMet(BuiltTask task)
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
    /// Tries the ready tasks in declaration order and starts each one that finds a slot free in the cap and in every limit it holds.
    /// One that does not fit is passed over and stays ready, so it never holds up the tasks behind it.
    /// The walk ends as soon as the cap is full, since every task holds a slot of the cap and none after that point could start.
    /// </summary>
    private void StartWhatFits(DateTimeOffset now)
    {
        var started = new List<int>();
        foreach (var position in _ready)
        {
            if (_slots.CapIsFull)
            {
                break;
            }

            var task = _graph.TaskAt(position);
            if (_slots.TryTake(task))
            {
                // Moved at once, not after the walk, so the snapshot with each start counts only the slots taken so far.
                Move(task, NodeStatus.Running, now);
                _ = Task.Run(() => RunBodyAsync(task));
                started.Add(position);
            }
        }

        // The ready set cannot change while it is walked, so the tasks that started leave it here.
        started.ForEach(position => _ready.Remove(position));
    }

    // No token: a run is stopped through its signal, and a task body takes none.
    private async Task RunBodyAsync(BuiltTask task)
    {
        var outcome = await InvokeBodyAsync(task).ConfigureAwait(false);
        Settle(task, outcome);
    }

    /// <summary>
    /// Invokes the body of a task and says how it ended. This is the one place a task body is invoked,
    /// and so the one catch-all for it: whatever escapes the body is a failure of that task and of nothing else,
    /// or its cancellation when it is a cancellation exception and the run has been stopped by the time the task settles. The exception is read here, outside the lock and before the task settles,
    /// because its members are consumer code too.
    /// </summary>
    private async Task<TaskOutcome> InvokeBodyAsync(BuiltTask task)
    {
        try
        {
            await task.Body(new TaskContext(this, task, _timeProvider, _stopping.Token)).ConfigureAwait(false);
            return TaskOutcome.Succeeded;
        }
        catch (Exception escaped)
        {
            return TaskOutcome.UnexpectedFailure(escaped);
        }
    }

    private void End(DateTimeOffset now)
    {
        var outcome = _counts[NodeStatus.Succeeded] == _graph.Tasks.Length ? RunOutcome.Complete
            : _stop is not null ? RunOutcome.Stopped
            : RunOutcome.Incomplete;

        // Unregister does not wait for a callback that is running, which may be waiting for this lock.
        _hearsTheSignal.Unregister();
        _endedAt = now;
        _outcome = outcome;
        Publish(new RunEnded(now, outcome));
        _delivery.Complete();
        _ended.SetResult(outcome);
    }

    /// <summary>Moves a task on to a status in which it has not settled: ready, or running.</summary>
    private void Move(BuiltTask task, NodeStatus to, DateTimeOffset now)
    {
        var before = _nodes[task.Position];
        Replace(task, before with { Status = to, Started = to is NodeStatus.Running ? now : before.Started }, now);
    }

    /// <summary>Settles a task: moves it to the status of its outcome and freezes that outcome on it.</summary>
    private void Move(BuiltTask task, TaskOutcome outcome, DateTimeOffset now)
    {
        Replace(task, outcome.Settle(_nodes[task.Position], now), now);
    }

    /// <summary>
    /// Puts a task as it is after a change of status in place of the task as it was, counts the change in the run
    /// and in every group that holds the task, and publishes it.
    /// </summary>
    private void Replace(BuiltTask task, NodeSnapshot changed, DateTimeOffset now)
    {
        var from = _nodes[task.Position].Status;
        var to = changed.Status;
        var after = changed with { Counts = StatusCounts.OfOneTask(to) };

        _nodes = _nodes.With(task.Position, after);
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
    private void MoveBeneath(BuiltGroup group, NodeStatus from, NodeStatus to)
    {
        var counts = _groupCounts[group.Position];
        counts.Move(from, to);
        var status = counts.GroupStatus;
        _nodes = _nodes.With(group.Position, _nodes[group.Position] with { Status = status, Counts = counts.ToDictionary() });

        if (status.IsSettled())
        {
            _settledInThisStep.Add(group);
        }
    }

    private NodeSnapshot AtTheStart(BuiltNode node)
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
        => new(_startedAt, _endedAt, _outcome, _stop, _nodes, _graph.Positions, _counts.ToDictionary(), _slots.Cap, _slots.Limits);
}
