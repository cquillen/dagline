using System.Collections.Frozen;

namespace Dagline;

/// <summary>
/// Every scheduling decision of one run. It takes one input for each thing that can happen to a run and answers with the steps
/// that input caused, in order: each step's events with what each changed in the snapshot, the tasks it starts and the effects
/// it asks for. <see cref="Run"/> carries the steps out.
/// </summary>
/// <remarks>
/// <para>
/// It owns the task instances, the ready set, the slots, the counts by status, the stop and the end rule. It holds no lock,
/// no disposable object and no thread, and never reads a clock: each input carries the time, which every event of its steps shares.
/// It is not thread-safe: the run gives it one input at a time, under its lock.
/// </para>
/// <para>
/// The snapshot just after each event is taken only when the run publishes the event, from what the event changed. A step that
/// makes many tasks ready therefore holds one small change for each, and not a snapshot of the whole run for each, until it is
/// carried out: holding those snapshots made a large run slow.
/// </para>
/// <para>
/// One input may cause several steps. A stop with a grace period of zero gives three: the stop, letting go of the running tasks,
/// and the end of the run. The end of the run is always a step of its own.
/// </para>
/// </remarks>
internal sealed class RunSteps
{
    private readonly Graph _graph;
    private readonly TimeSpan _gracePeriod;
    private readonly StatusCounts _counts;
    private readonly FrozenDictionary<int, StatusCounts> _groupCounts;
    private readonly List<BuiltGroup> _settledInThisStep = [];
    private readonly SortedSet<int> _ready = [];
    private readonly TaskInstance?[] _instances;
    private readonly Slots _slots;
    private readonly List<Step> _decided = [];
    private readonly NodeSnapshot[] _nodes;
    private readonly SnapshotTaker _snapshots;

    private StepUnderWay? _step;
    private RunMilestones _milestones = new(Started: default, Stop: null, Ended: null, Outcome: null);
    private int _requestsApplied;

    /// <param name="graph">The graph the run runs.</param>
    /// <param name="settings">The settings of the run: the sizes of its slots and its grace period.</param>
    public RunSteps(Graph graph, RunSettings settings)
    {
        _graph = graph;
        _gracePeriod = settings.GracePeriod;
        _slots = new Slots(graph.LimitNames, settings.Sizes);
        _counts = new StatusCounts(pending: graph.Tasks.Length);
        _groupCounts = graph.Nodes.OfType<BuiltGroup>().ToFrozenDictionary(
            group => group.Position,
            group => new StatusCounts(pending: group.TaskCount));
        _instances = new TaskInstance?[graph.Nodes.Length];
        foreach (var task in graph.Tasks)
        {
            _instances[task.Position] = new TaskInstance(task);
        }

        _nodes = [.. graph.Nodes.Select(AtTheStart)];
        _snapshots = new SnapshotTaker(_nodes, graph.Positions, _slots);
    }

    /// <summary>
    /// The run starts: the tasks with no dependency become ready and those that fit start. When stop requests have already been made,
    /// the stop is a step of its own, after the tasks have become ready and before any starts, so the run starts nothing and ends.
    /// </summary>
    /// <param name="now">When the run starts.</param>
    /// <param name="stopRequests">How many requests the run's signal has had so far.</param>
    /// <param name="source">The run as reporters read it, which <see cref="RunStarted"/> carries.</param>
    public IReadOnlyList<Step> Start(DateTimeOffset now, int stopRequests, ISnapshotSource source)
    {
        _milestones = _milestones with { Started = now };
        Begin();
        Publish(new RunStarted(now, source));
        MakeReady(_graph.Tasks, now);
        AnnounceSettledGroups(now);

        // A second request needs nothing more here: once stopped from its start, the run has no task to stop waiting for.
        _requestsApplied = stopRequests;
        if (stopRequests > 0)
        {
            Begin();
            ApplyStop(now);
        }

        StartWhatFitsOrEnd(now);
        return Decided();
    }

    /// <summary>
    /// A task's body has ended: the task settles, then what follows from it is applied: the tasks its failure blocks, and what follows
    /// for all of them. A task the run has let go of has settled already, and its body ending gives no step.
    /// </summary>
    public IReadOnlyList<Step> BodyEnded(BuiltTask task, TaskOutcome ended, DateTimeOffset now)
    {
        var changes = InstanceOf(task).Settle(ended, runHasStopped: HasStopped, now);
        if (changes.Count == 0)
        {
            return [];
        }

        Begin();
        Settle(task, changes);
        var blocked = InstanceOf(task).Status is NodeStatus.Failed ? Block(task, now) : [];
        Advance(DependentsOf([task, .. blocked]), now);
        return Decided();
    }

    /// <summary>
    /// A running task reports something about itself through its <see cref="TaskContext"/>. The report is one step, with the event it
    /// produced, or with none when it changed only the snapshot. A report on a task that is not running gives no step.
    /// </summary>
    /// <param name="task">The task that reports.</param>
    /// <param name="report">The operation on the task's instance, given the time.</param>
    /// <param name="now">When the task reports.</param>
    public IReadOnlyList<Step> Report(BuiltTask task, Func<TaskInstance, DateTimeOffset, TaskChange?> report, DateTimeOffset now)
    {
        if (report(InstanceOf(task), now) is not { } change)
        {
            return [];
        }

        Begin();
        Apply(task, change);
        return Decided();
    }

    /// <summary>
    /// The run's signal has had <paramref name="requests"/> requests. The first is a stop: from its step on nothing starts,
    /// and the run ends once nothing is running. The second also lets go of the running tasks.
    /// Since it reads the count, requests are applied in order and once, however many calls it takes to hear them.
    /// A request already applied, or heard after the run has ended, gives no step: the wait for reporters hears it.
    /// </summary>
    public IReadOnlyList<Step> StopHeard(int requests, DateTimeOffset now)
    {
        if (HasEnded)
        {
            return [];
        }

        _requestsApplied = requests;
        if (!HasStopped)
        {
            Begin();
            ApplyStop(now);
            StartWhatFitsOrEnd(now);
            BoundTheGracePeriod(now);
        }

        if (requests >= 2)
        {
            LetGo(StoppedWaitingReason.SecondStop, now);
        }

        return Decided();
    }

    /// <summary>The grace period is over: the run lets go of the tasks still running, if it has not ended.</summary>
    public IReadOnlyList<Step> GracePeriodOver(DateTimeOffset now)
    {
        LetGo(StoppedWaitingReason.GracePeriodOver, now);
        return Decided();
    }

    /// <summary>The status, as it is now, of the node at <paramref name="position"/>.</summary>
    public NodeStatus StatusAt(int position) => _nodes[position].Status;

    /// <summary>
    /// The snapshot of the run just after an event of a decided step: as it was then, however far the run has got since.
    /// The run takes the snapshot of every event, as it publishes it, in the order the steps give the events,
    /// and before it gives the next input, since each snapshot is built on the one before.
    /// </summary>
    public Snapshot SnapshotAfter(StepEvent stepEvent) => _snapshots.Take(stepEvent.Change);

    /// <summary>
    /// The run as it is now. The snapshot of every event decided so far must have been taken first, as
    /// <see cref="SnapshotAfter"/> says.
    /// </summary>
    public Snapshot TakeSnapshot() => _snapshots.Take(ChangeSinceLastEvent());

    private bool HasStopped => _milestones.Stop is not null;

    private bool HasEnded => _milestones.Outcome is not null;

    private bool NothingIsRunning => _counts[NodeStatus.Running] == 0;

    /// <summary>
    /// Applies the first request: from this step on nothing starts. The step signals the token every task sees,
    /// so it is signalled before the run can end and <see cref="Runner.RunAsync"/> return.
    /// </summary>
    private void ApplyStop(DateTimeOffset now)
    {
        _milestones = _milestones with { Stop = new StopState(now, _gracePeriod, GracePeriodRules.DeadlineFrom(now, _gracePeriod)) };
        _step!.SignalsTheTasks = true;
        Publish(new StopRequested(now, _gracePeriod));
    }

    /// <summary>
    /// Bounds the grace period once a stop has been applied and the run has not ended: a grace period of zero lets go of the running
    /// tasks at once, and a limited one asks the stop's step to start a timer that lets go of those still running when it fires.
    /// </summary>
    private void BoundTheGracePeriod(DateTimeOffset now)
    {
        if (HasEnded || GracePeriodRules.IsUnlimited(_gracePeriod))
        {
            return;
        }

        if (_gracePeriod == TimeSpan.Zero)
        {
            LetGo(StoppedWaitingReason.GracePeriodOver, now);
            return;
        }

        _step!.GracePeriodTimer = _gracePeriod;
    }

    /// <summary>
    /// Ends the wait for the running tasks after a stop, if the run is still waiting for any. In a step of its own it publishes
    /// <see cref="StoppedWaiting"/>, then records each running task as cancelled in declaration order and lets go of it.
    /// What follows is applied once for all of them, as after any task settles, and the run ends, since nothing is left running.
    /// </summary>
    private void LetGo(StoppedWaitingReason reason, DateTimeOffset now)
    {
        if (HasEnded || NothingIsRunning)
        {
            return;
        }

        Begin();
        Publish(new StoppedWaiting(now, reason));
        List<BuiltTask> leftBehind = [.. _graph.Tasks.Where(task => InstanceOf(task).Status is NodeStatus.Running)];
        foreach (var task in leftBehind)
        {
            Settle(task, InstanceOf(task).Settle(TaskOutcome.LeftBehind, runHasStopped: true, now));
        }

        Advance(DependentsOf(leftBehind), now);
    }

    /// <summary>
    /// Gives back the slots of a task that has just settled, and applies the changes its instance made in settling:
    /// the end of its wait, if it was waiting, then its change of status.
    /// </summary>
    private void Settle(BuiltTask task, IReadOnlyList<TaskChange> changes)
    {
        _slots.GiveBack(task);
        foreach (var change in changes)
        {
            Apply(task, change);
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
        foreach (var task in blocked)
        {
            Apply(task, InstanceOf(task).Block(failed.Path, now));
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

    private bool IsPending(BuiltTask task) => InstanceOf(task).Status is NodeStatus.Pending;

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
        if (!HasStopped)
        {
            StartWhatFits(now);
        }

        if (NothingIsRunning && (HasStopped || _counts[NodeStatus.Ready] == 0))
        {
            End(now);
        }
    }

    private void MakeReady(IEnumerable<BuiltTask> candidates, DateTimeOffset now)
    {
        List<BuiltTask> ready = [.. candidates.Where(DependenciesAreMet)];
        foreach (var task in ready)
        {
            Apply(task, InstanceOf(task).MakeReady(now));
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
    /// Tries the ready tasks in declaration order and starts each one whose slots it can take. One that does not fit is passed over
    /// and stays ready, so it never holds up the tasks behind it. The walk ends once the slots answer that the cap is full,
    /// since every task holds a slot of the cap and none after that point could start.
    /// </summary>
    private void StartWhatFits(DateTimeOffset now)
    {
        var started = new List<int>();
        foreach (var position in _ready)
        {
            var task = _graph.TaskAt(position);
            var answer = _slots.Take(task);
            if (answer is SlotsAnswer.CapFull)
            {
                break;
            }

            if (answer is SlotsAnswer.Took)
            {
                // Moved at once, not after the walk, so the snapshot with each start counts only the slots taken so far.
                Apply(task, InstanceOf(task).Start(now));
                _step!.Starts.Add(task);
                started.Add(position);
            }
        }

        // The ready set cannot change while it is walked, so the tasks that started leave it here.
        started.ForEach(position => _ready.Remove(position));
    }

    /// <summary>Ends the run, in a step of its own whose one event is <see cref="RunEnded"/>.</summary>
    private void End(DateTimeOffset now)
    {
        var outcome = _counts[NodeStatus.Succeeded] == _graph.Tasks.Length ? RunOutcome.Complete
            : HasStopped ? RunOutcome.Stopped
            : RunOutcome.Incomplete;

        Begin();
        _milestones = _milestones with { Ended = now, Outcome = outcome };
        _step!.Ends = new RunEnd(outcome, _requestsApplied);
        Publish(new RunEnded(now, outcome));
    }

    /// <summary>
    /// Puts a task as it is after a change to its instance in place of the task as it was. A change of status is counted in the run
    /// and in every group that holds the task. The event the change produced is published in the step under way.
    /// </summary>
    private void Apply(BuiltTask task, TaskChange change)
    {
        Record(task.Position, change.Task);
        if (change.Event is TaskStatusChanged moved)
        {
            _counts.Move(moved.From, moved.To);
            foreach (var group in task.Groups.Select(_graph.GroupAt))
            {
                MoveBeneath(group, moved.From, moved.To);
            }
        }

        if (change.Event is { } produced)
        {
            Publish(produced);
        }
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
        Record(group.Position, _nodes[group.Position] with { Status = status, Counts = counts.ToDictionary() });

        if (status.IsSettled())
        {
            _settledInThisStep.Add(group);
        }
    }

    private NodeSnapshot AtTheStart(BuiltNode node)
        => _groupCounts.TryGetValue(node.Position, out var counts)
            ? new NodeSnapshot(node, counts.GroupStatus, counts.ToDictionary())
            : InstanceOf((BuiltTask)node).Snapshot;

    // Indexed by position among the nodes, so a group's place is empty.
    private TaskInstance InstanceOf(BuiltTask task) => _instances[task.Position]!;

    /// <summary>Puts a node as it is after a change in place of the node as it was, and records the change for the next snapshot.</summary>
    private void Record(int position, NodeSnapshot node)
    {
        _nodes[position] = node;
        _snapshots.Record(position, node);
    }

    private void Publish(RunEvent runEvent) => _step!.Events.Add(new StepEvent(runEvent, ChangeSinceLastEvent()));

    /// <summary>What has changed in the snapshot since the last event, which from now on is part of the snapshot taken next.</summary>
    private SnapshotChange ChangeSinceLastEvent() => _snapshots.Close(_milestones, _counts);

    /// <summary>Begins a new step, once the step under way, if any, is decided.</summary>
    private void Begin()
    {
        Finish();
        _step = new StepUnderWay();
    }

    private void Finish()
    {
        if (_step is not null)
        {
            _decided.Add(_step.Decided());
            _step = null;
        }
    }

    /// <summary>The steps the current input caused, in order.</summary>
    private List<Step> Decided()
    {
        Finish();
        List<Step> decided = [.. _decided];
        _decided.Clear();
        return decided;
    }

    /// <summary>A step while it is being decided.</summary>
    private sealed class StepUnderWay
    {
        public List<StepEvent> Events { get; } = [];

        public List<BuiltTask> Starts { get; } = [];

        public bool SignalsTheTasks { get; set; }

        public TimeSpan? GracePeriodTimer { get; set; }

        public RunEnd? Ends { get; set; }

        public Step Decided() => new(Events, Starts, SignalsTheTasks, GracePeriodTimer, Ends);
    }
}
