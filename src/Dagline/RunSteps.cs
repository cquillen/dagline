namespace Dagline;

/// <summary>
/// Every scheduling decision of one run. It takes one input for each thing that can happen to a run and answers with the steps
/// that input caused, in order: each step's events with what each changed in the snapshot, the tasks it starts and the effects
/// it asks for. <see cref="Run"/> carries the steps out.
/// </summary>
/// <remarks>
/// <para>
/// It owns the task instances, the ready set, the slots, the stop and the end rule. It tells <see cref="RollUp"/> of every change
/// to a task, and asks it for the run's counts, a group's status and counts, and the groups that settled, so that it can announce
/// them. It builds no journal entry: <see cref="JournalRecorder"/> builds them from the events of each step, and all this type
/// learns of the journal is that a write to it failed, through <see cref="JournalWriteFailed"/>. It holds no lock, no disposable
/// object and no thread, and never reads a clock: each input that can cause an event carries the time, which every event of its
/// steps shares.
/// It is not thread-safe: the run gives it one input at a time, under its lock.
/// </para>
/// <para>
/// The snapshot just after each event is taken only when the run publishes the event, from what the event changed. A step that
/// makes many tasks ready therefore holds one small change for each, and not a snapshot of the whole run for each, until it is
/// carried out: holding those snapshots made a large run slow.
/// </para>
/// <para>
/// One input may cause several steps. A stop with a grace period of zero gives two: the stop, and letting go of the running tasks.
/// The end of the run is decided by an input of its own, <see cref="EndIfDone"/>, which the run gives after each input that can
/// settle a task or apply a stop, with the same time, so that a failed journal write reaches the steps before the run can end.
/// </para>
/// </remarks>
internal sealed class RunSteps
{
    private readonly Graph _graph;
    private readonly TimeSpan _gracePeriod;
    private readonly RollUp _rollUp;
    private readonly SortedSet<int> _ready = [];
    private readonly TaskInstance?[] _instances;
    private readonly Slots _slots;
    // Nothing changes a step once it is decided, so every report that produced no event answers with this one.
    private static readonly IReadOnlyList<Step> _changedOnlyTheSnapshot =
        [new Step(Events: [], Starts: [], SignalsTheTasks: false, GracePeriodTimer: null, Ends: null)];

    private readonly List<Step> _decided = [];
    private readonly SnapshotTaker _snapshots;
    private readonly JournalFold? _earlier;
    private readonly Continuation _continuation;

    private StepUnderWay? _step;
    private RunMilestones _milestones = new(Started: default, Stop: null, Ended: null, Outcome: null);
    private int _requestsApplied;
    private bool _journalWriteFailed;

    /// <param name="graph">The graph the run runs.</param>
    /// <param name="settings">The settings of the run: the sizes of its slots and its grace period.</param>
    /// <param name="earlier">
    /// The journal the run is on, as it was folded when opened, or <see langword="null"/> for a run with no journal. It gives each
    /// task the checkpoint it begins with, and the journal as the run's snapshots show it.
    /// </param>
    /// <param name="continuation">
    /// What the run takes from the journal. A task it holds as an earlier success begins succeeded and is never made ready.
    /// </param>
    public RunSteps(Graph graph, RunSettings settings, JournalFold? earlier, Continuation continuation)
    {
        _graph = graph;
        _earlier = earlier;
        _gracePeriod = settings.GracePeriod;
        _slots = new Slots(graph.LimitNames, settings.Sizes);
        _continuation = continuation;
        _instances = InstancesAtTheStart();

        var atTheStart = new NodeSnapshot?[graph.Nodes.Length];
        foreach (var task in graph.Tasks)
        {
            atTheStart[task.Position] = InstanceOf(task).ToNodeSnapshot();
        }

        _rollUp = new RollUp(graph, atTheStart);
        _snapshots = SnapshotsAtTheStart(atTheStart);
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
            ApplyStop(StopRequester.App, now);
        }

        StartWhatFitsUnlessStopped(now);
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
    /// A running task reports something about itself that is never an event, such as progress, through its <see cref="TaskContext"/>.
    /// The input carries no time, since nothing it causes has one. The report is one step with no event,
    /// and a report on a task that is not running gives no step.
    /// </summary>
    /// <param name="task">The task that reports.</param>
    /// <param name="report">The operation on the task's instance.</param>
    public IReadOnlyList<Step> Report(BuiltTask task, Func<TaskInstance, TaskChange?> report)
        => Reported(task, report(InstanceOf(task)));

    /// <summary>
    /// A running task reports something about itself that may be an event, such as a warning or the begin of a wait, through its
    /// <see cref="TaskContext"/>. The report is one step, with the event it produced, or with none when it changed only the snapshot.
    /// A report on a task that is not running gives no step.
    /// </summary>
    /// <param name="task">The task that reports.</param>
    /// <param name="argument">What the task reports, such as the warning, passed on to <paramref name="report"/>.</param>
    /// <param name="report">
    /// The operation on the task's instance, given <paramref name="argument"/> and the time. It is passed the argument so that it can be
    /// a static lambda, which allocates nothing on a path a task may take very often.
    /// </param>
    /// <param name="now">When the task reports.</param>
    public IReadOnlyList<Step> Report<TArgument>(
        BuiltTask task,
        TArgument argument,
        Func<TaskInstance, TArgument, DateTimeOffset, TaskChange?> report,
        DateTimeOffset now)
        => Reported(task, report(InstanceOf(task), argument, now));

    /// <summary>
    /// The step a report caused, given what it changed, if anything. A task may report progress millions of times,
    /// so a report that produced no event allocates nothing here beyond the task as it now is: it is recorded for the next
    /// snapshot, the roll-up is told of it, and the step it answers with is shared by all such reports.
    /// </summary>
    private IReadOnlyList<Step> Reported(BuiltTask task, TaskChange? reported)
    {
        if (reported is not { } change)
        {
            return [];
        }

        if (change.Event is null)
        {
            _snapshots.Record(task.Position, change.Task);
            _rollUp.TaskChanged(task, change);
            return _changedOnlyTheSnapshot;
        }

        Begin();
        Apply(task, change);
        return Decided();
    }

    /// <summary>
    /// The run's signal has had <paramref name="requests"/> requests. The first is a stop: from its step on nothing starts,
    /// and the run ends once nothing is running. The second also lets go of the running tasks, and so does any new request once
    /// Dagline has stopped the run itself, as the stop's facts say.
    /// Since it reads the count, requests are applied in order and once, however many calls it takes to hear them.
    /// A request already applied, or heard after the run has ended, gives no step: the wait for reporters hears it.
    /// </summary>
    public IReadOnlyList<Step> StopHeard(int requests, DateTimeOffset now)
    {
        if (HasEnded)
        {
            return [];
        }

        var heardNew = requests > _requestsApplied;
        _requestsApplied = requests;
        if (heardNew && _milestones.Stop?.By is StopRequester.Dagline)
        {
            LetGo(GraceEndReason.SecondStop, now);
            return Decided();
        }

        if (!HasStopped)
        {
            Begin();
            ApplyStop(StopRequester.App, now);
            BoundTheGracePeriod(now);
        }

        if (requests >= 2)
        {
            LetGo(GraceEndReason.SecondStop, now);
        }

        return Decided();
    }

    /// <summary>
    /// A write to the journal failed. Unless a stop has been applied, Dagline applies one itself, in a step of its own that signals
    /// the tasks, and the grace period is bounded as for any stop. The run then ends with no run outcome, and so with a
    /// <see cref="JournalWriteException"/>. The run gives this input once, before the end input. Heard after the run has ended, it
    /// gives no step and changes nothing: the run keeps the outcome it ended with.
    /// </summary>
    /// <param name="now">When the write failed.</param>
    public IReadOnlyList<Step> JournalWriteFailed(DateTimeOffset now)
    {
        if (HasEnded)
        {
            return [];
        }

        _journalWriteFailed = true;
        if (!HasStopped)
        {
            Begin();
            ApplyStop(StopRequester.Dagline, now);
            BoundTheGracePeriod(now);
        }

        return Decided();
    }

    /// <summary>
    /// The end of each input that can settle a task or apply a stop: the run ends, in a step of its own, when no task is running and,
    /// unless a stop has been applied, none is ready. Given after the run has ended, or while it has work left, it gives no step.
    /// </summary>
    public IReadOnlyList<Step> EndIfDone(DateTimeOffset now)
    {
        if (!HasEnded && NothingIsRunning && (HasStopped || _rollUp.InRun(NodeStatus.Ready) == 0))
        {
            End(now);
        }

        return Decided();
    }

    /// <summary>The grace period is over: the run lets go of the tasks still running, if it has not ended.</summary>
    public IReadOnlyList<Step> GracePeriodOver(DateTimeOffset now)
    {
        LetGo(GraceEndReason.GracePeriodOver, now);
        return Decided();
    }

    /// <summary>The checkpoint of a task as it is now: empty when there is none.</summary>
    public string CheckpointOf(BuiltTask task) => InstanceOf(task).Checkpoint;

    /// <summary>
    /// A running task saves a checkpoint, or clears it when <paramref name="text"/> is empty, through its <see cref="TaskContext"/>.
    /// It makes no step, no event and no change to any snapshot.
    /// </summary>
    /// <param name="task">The task whose checkpoint changes.</param>
    /// <param name="text">The checkpoint, or empty to clear it.</param>
    /// <returns>
    /// Whether the checkpoint changed, and so is to be recorded: <see langword="false"/> when the task cleared a checkpoint it did not have.
    /// </returns>
    /// <exception cref="InvalidOperationException">The task is not running: it has settled, or the run has let go of it.</exception>
    public bool ChangeCheckpoint(BuiltTask task, string text) => InstanceOf(task).ChangeCheckpoint(text);

    /// <summary>
    /// The status, as it is now, of the node at <paramref name="position"/>: a task's is its instance's,
    /// and a group's is the roll-up's.
    /// </summary>
    public NodeStatus StatusAt(int position)
        => _instances[position] is { } instance ? instance.Status : _rollUp.StatusOf(position);

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

    // From the end time, not the outcome: a run that ends with an error has none.
    private bool HasEnded => _milestones.Ended is not null;

    private bool NothingIsRunning => _rollUp.InRun(NodeStatus.Running) == 0;

    /// <summary>
    /// Applies the first request: from this step on nothing starts. The step signals the token every task sees,
    /// so it is signalled before the run can end and <see cref="Runner.RunAsync"/> return.
    /// </summary>
    private void ApplyStop(StopRequester by, DateTimeOffset now)
    {
        _milestones = _milestones with { Stop = new StopState(now, _gracePeriod, GracePeriodRules.DeadlineFrom(now, _gracePeriod), by) };
        _step!.SignalsTheTasks = true;
        Publish(new StopRequested(now, by, _gracePeriod));
    }

    /// <summary>
    /// Bounds the grace period once a stop has been applied and the run has not ended: a grace period of zero lets go of the running
    /// tasks at once, and a limited one asks the stop's step to start a timer that lets go of those still running when it fires.
    /// </summary>
    private void BoundTheGracePeriod(DateTimeOffset now)
    {
        if (NothingIsRunning || GracePeriodRules.IsUnlimited(_gracePeriod))
        {
            return;
        }

        if (_gracePeriod == TimeSpan.Zero)
        {
            LetGo(GraceEndReason.GracePeriodOver, now);
            return;
        }

        _step!.GracePeriodTimer = _gracePeriod;
    }

    /// <summary>
    /// Ends the wait for the running tasks after a stop, if the run is still waiting for any. In a step of its own it publishes
    /// <see cref="GraceEnded"/>, then records each running task as cancelled in declaration order and lets go of it.
    /// What follows is applied once for all of them, as after any task settles. Nothing is left running, so the end input that follows ends the run.
    /// </summary>
    private void LetGo(GraceEndReason reason, DateTimeOffset now)
    {
        if (HasEnded || NothingIsRunning)
        {
            return;
        }

        Begin();
        Publish(new GraceEnded(now, reason));
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
            .Concat<BuiltNode>(_rollUp.Settled)
            .SelectMany(node => node.Dependents)
            .Distinct()
            .Order()
            .Select(_graph.TaskAt);

    /// <summary>
    /// Applies what follows from one change, causes before effects: the tasks among <paramref name="candidates"/>
    /// whose dependencies are now met become ready, the groups that the step settled are announced,
    /// and the ready tasks that fit start.
    /// </summary>
    private void Advance(IEnumerable<BuiltTask> candidates, DateTimeOffset now)
    {
        MakeReady(candidates, now);
        AnnounceSettledGroups(now);
        StartWhatFitsUnlessStopped(now);
    }

    /// <summary>Starts the ready tasks that fit, unless a stop has been applied.</summary>
    private void StartWhatFitsUnlessStopped(DateTimeOffset now)
    {
        if (!HasStopped)
        {
            StartWhatFits(now);
        }
    }

    /// <summary>
    /// Makes ready each pending task among <paramref name="candidates"/> whose dependencies are all met. Only a pending task is
    /// considered: a dependent may be an earlier success, since nothing checks the graph against the journal.
    /// </summary>
    private void MakeReady(IEnumerable<BuiltTask> candidates, DateTimeOffset now)
    {
        List<BuiltTask> ready = [.. candidates.Where(task => IsPending(task) && DependenciesAreMet(task))];
        foreach (var task in ready)
        {
            Apply(task, InstanceOf(task).MakeReady(now));
            _ready.Add(task.Position);
        }
    }

    private bool DependenciesAreMet(BuiltTask task)
        => task.Upstreams.All(upstream => StatusAt(upstream.Position).Meets(upstream.Kind));

    /// <summary>
    /// Publishes one event for each group the current step settled, in the order the roll-up gives them, each with the group's
    /// status and counts.
    /// </summary>
    private void AnnounceSettledGroups(DateTimeOffset now)
    {
        foreach (var group in _rollUp.TakeSettled())
        {
            Publish(new GroupSettled(now, group.Path, _rollUp.StatusOf(group.Position), _rollUp.CountsOf(group)));
        }
    }

    /// <summary>
    /// Tries the ready tasks in declaration order and starts each one whose slots it can take. One that does not fit is passed over
    /// and stays ready, so it never holds up the tasks behind it. The walk ends once the slots answer that the max running tasks is reached,
    /// since every task holds a slot of the max running tasks and none after that point could start.
    /// </summary>
    private void StartWhatFits(DateTimeOffset now)
    {
        var started = new List<int>();
        foreach (var position in _ready)
        {
            var task = _graph.TaskAt(position);
            var answer = _slots.Take(task);
            if (answer is SlotsAnswer.MaxRunningTasksReached)
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

    /// <summary>
    /// Ends the run, in a step of its own. With a run outcome, its one event is <see cref="RunEnded"/>. After a failed journal write
    /// it has no outcome and no event: the run publishes that <see cref="RunEnded"/> itself, with the error it ends with.
    /// </summary>
    private void End(DateTimeOffset now)
    {
        Begin();
        if (_journalWriteFailed)
        {
            _milestones = _milestones with { Ended = now };
            _step!.Ends = new RunEnd.ByFailedWrite(_requestsApplied);
            return;
        }

        var outcome = _rollUp.InRun(NodeStatus.Succeeded) == _graph.Tasks.Length ? RunOutcome.Complete
            : HasStopped ? RunOutcome.Stopped
            : RunOutcome.Incomplete;
        _milestones = _milestones with { Ended = now, Outcome = outcome };
        _step!.Ends = new RunEnd.WithOutcome(outcome, _requestsApplied);
        Publish(new RunEnded(now, outcome));
    }

    /// <summary>
    /// Records a task as it is after a change to its instance, for the next snapshot, and tells the roll-up of the change.
    /// The event the change produced is published in the step under way.
    /// </summary>
    private void Apply(BuiltTask task, TaskChange change)
    {
        _snapshots.Record(task.Position, change.Task);
        _rollUp.TaskChanged(task, change);
        if (change.Event is { } produced)
        {
            Publish(produced);
        }
    }

    /// <summary>
    /// Every task's instance at the start of the run, indexed by position among the nodes: an earlier success for a task the journal
    /// holds as one, and otherwise a pending task with the checkpoint the journal holds for its path, if any.
    /// </summary>
    private TaskInstance?[] InstancesAtTheStart()
    {
        var instances = new TaskInstance?[_graph.Nodes.Length];
        foreach (var task in _graph.Tasks)
        {
            instances[task.Position] = _continuation.EarlierSuccessOf(task) is { } recorded
                ? TaskInstance.EarlierSuccess(task, recorded)
                : new TaskInstance(task, _earlier?.CheckpointAt(task.Path) ?? "");
        }

        return instances;
    }

    /// <summary>
    /// The run's snapshots, starting from every node as it is at the start, each group as the roll-up hands it over, and the journal
    /// as the run continues it.
    /// </summary>
    /// <param name="tasksAtTheStart">Each task as it is at the start, indexed by position among the nodes, so a group's place is empty.</param>
    private SnapshotTaker SnapshotsAtTheStart(NodeSnapshot?[] tasksAtTheStart)
        => new(
            _graph.Nodes.Select(node => tasksAtTheStart[node.Position] ?? _rollUp.SnapshotOf((BuiltGroup)node)),
            _graph.Positions,
            _slots,
            new SnapshotJournal(_earlier?.NextRun)
            {
                SetAside = [.. _continuation.SetAside.Select(task => task.Path)],
                JournalPathsNotInGraph = _continuation.PathsNotInGraph,
                JournalPathsNotInGraphUnsucceeded = _continuation.PathsNotInGraphUnsucceeded,
            });

    // Indexed by position among the nodes, so a group's place is empty.
    private TaskInstance InstanceOf(BuiltTask task) => _instances[task.Position]!;

    private void Publish(RunEvent runEvent) => _step!.Events.Add(new StepEvent(runEvent, ChangeSinceLastEvent()));

    /// <summary>What has changed in the snapshot since the last event, which from now on is part of the snapshot taken next.</summary>
    private SnapshotChange ChangeSinceLastEvent()
    {
        _rollUp.HandOverChanged(_snapshots);
        return _snapshots.Close(_milestones, _rollUp.RunCounts);
    }

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
