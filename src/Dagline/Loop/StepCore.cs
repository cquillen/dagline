namespace Dagline;

/// <summary>
/// Every decision of one run on the new engine (ADR 0015): it takes one input and the time the loop read for it, and answers with
/// the effects that input caused, in the order the loop is to carry them out. The loop decides nothing.
/// </summary>
/// <remarks>
/// <para>
/// It has no thread, no clock and no await, and is given one input at a time by the loop alone. It owns the task instances, the
/// ready tasks, the slots, the held tasks, the roll-up and the snapshots, and where the run is in its life.
/// </para>
/// <para>
/// A record is always the last effect of its list. What follows the write is kept in <see cref="_afterWrite"/>, and the core then
/// takes no input but the write's result, <see cref="InputKind.Written"/>, which the loop gives it next, ahead of anything in the
/// inbox; only then does it decide what follows. With no journal there is nothing to write, and what follows is decided in the same
/// call. So a run behaves the same with and without a journal, apart from the writes. A step's events share the time of the input
/// that began it, the events decided after a write included.
/// </para>
/// <para>
/// An event whose change a write records, a task's start or its settle, is published once that write has landed: its change is
/// closed as it is decided, so its snapshot is as of that instant, and its publish is held until the write's result. Any other
/// event decided before a write is published before it.
/// </para>
/// <para>
/// Each event's snapshot is built by the loop as it publishes the event, from the change the event carries, never for every event
/// of a step beforehand: a step that makes twenty thousand tasks ready would otherwise hold twenty thousand snapshots.
/// </para>
/// </remarks>
internal sealed class StepCore
{
    // What follows the write under way. Nothing while none is.
    private enum AfterWrite
    {
        Nothing,
        RunStart,
        Settles,
        Starts,
        End,
    }

    // Where the run is in its life: the one answer to "may anything start?" and "has the run ended?". The snapshot's milestones
    // say the same to readers, and are set beside it.
    private enum Life
    {
        NotStarted,
        Running,
        Draining,
        Stopping,
        Ended,
    }

    private readonly Graph _graph;
    private readonly ISnapshotSource _source;
    private readonly RollUp _rollUp;
    private readonly UnmetDependencies _dependencies;
    private readonly SortedSet<int> _ready = [];
    private readonly HeldTasks _held;
    private readonly TaskInstance?[] _instances;
    private readonly Slots _slots;
    private readonly SnapshotTaker _snapshots;
    private readonly List<Effect> _effects = [];
    private readonly bool _onAJournal;
    private readonly int _maxRunningTasks;
    private readonly TimeSpan _gracePeriod;
    private readonly Dictionary<string, int> _limits;
    private readonly string _user = Environment.UserName;
    private readonly string _machine = Environment.MachineName;

    // Reused at every settle, so working out what follows from it allocates no collection of its own.
    private readonly List<BuiltTask> _becomingReady = [];
    private readonly List<BuiltTask> _blocked = [];
    private readonly Func<BuiltTask, bool> _isPending;

    // What the write under way holds back: the events it records, whose changes are already closed, the tasks it settles, and the
    // tasks whose bodies start once it lands.
    private readonly List<StepEvent> _heldEvents = [];
    private readonly List<BuiltTask> _settling = [];
    private readonly List<BuiltTask> _starting = [];

    private AfterWrite _afterWrite;
    private Life _life;
    private AskedBeforeTheStart _askedBeforeTheStart = AskedBeforeTheStart.Nothing;
    private DateTimeOffset _now;
    private RunMilestones _milestones = new(Started: default, Drain: null, Stop: null, Ended: null, Outcome: null);

    /// <param name="graph">The graph the run runs.</param>
    /// <param name="settings">The settings of the run: the sizes of its slots and its grace period.</param>
    /// <param name="journal">
    /// The journal the run is on, as it was folded when opened, or <see langword="null"/> for a run with no journal. Only a new
    /// journal is recorded as yet: continuing one is still to come.
    /// </param>
    /// <param name="source">The source of snapshots that <see cref="RunStarted"/> hands to reporters.</param>
    public StepCore(Graph graph, RunSettings settings, JournalFold? journal, ISnapshotSource source)
    {
        _graph = graph;
        _source = source;
        _onAJournal = journal is not null;
        _maxRunningTasks = settings.Sizes.MaxRunningTasks;
        _gracePeriod = settings.GracePeriod;
        _limits = graph.LimitNames
            .Zip(settings.Sizes.Limits)
            .ToDictionary(limit => limit.First.Text, limit => limit.Second, LimitName.TextComparer);
        _slots = new Slots(graph.LimitNames, settings.Sizes);
        _held = new HeldTasks(graph, _slots);
        _dependencies = new UnmetDependencies(graph);
        _isPending = task => InstanceOf(task).Status is NodeStatus.Pending;
        _instances = new TaskInstance?[graph.Nodes.Length];
        var atTheStart = new NodeSnapshot?[graph.Nodes.Length];
        foreach (var task in graph.Tasks)
        {
            var instance = new TaskInstance(task);
            _instances[task.Position] = instance;
            atTheStart[task.Position] = instance.ToNodeSnapshot();
        }

        _rollUp = new RollUp(graph, atTheStart);
        _snapshots = new SnapshotTaker(
            graph.Nodes.Select(node => atTheStart[node.Position] ?? _rollUp.SnapshotOf((BuiltGroup)node)),
            graph.Positions,
            _slots,
            new SnapshotJournal(journal?.NextRun));
    }

    /// <summary>How many times the run has tried to take the slots of a ready task so far, as <see cref="Slots.Tries"/> counts them.</summary>
    public int SlotTries => _slots.Tries;

    private bool HasEnded => _life is Life.Ended;

    private bool HasStopped => _life is Life.Stopping;

    // A drain or a stop: either one starts nothing more, ends the run once nothing is running, and makes its outcome stopped.
    private bool IsEndingEarly => _life is Life.Draining or Life.Stopping;

    private bool NothingIsRunning => _rollUp.InRun(NodeStatus.Running) == 0;

    /// <summary>
    /// Applies one input and answers with its effects, in order. The list is reused: it holds until the next call.
    /// </summary>
    /// <param name="input">The input. While a write is under way, the only input allowed is its result.</param>
    /// <param name="now">
    /// When the loop took the input. The result of a write continues the step the write is part of, which keeps its time, so the
    /// time given with it is not read.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The input is not one the core takes now: a fault in Dagline, which faults the run.
    /// </exception>
    public IReadOnlyList<Effect> Apply(in Input input, DateTimeOffset now)
    {
        _effects.Clear();
        if (_afterWrite is not AfterWrite.Nothing)
        {
            Written(input);
            return _effects;
        }

        if (HasEnded)
        {
            return _effects;
        }

        _now = now;
        switch (input.Kind)
        {
            case InputKind.Start:
                Start(input.Asked);
                break;
            case InputKind.BodyEnded:
                BodyEnded(input.Task!, input.Outcome);
                break;
            case InputKind.Drain:
                Drain();
                break;
            case InputKind.Stop:
                Stop(input.Requester);
                break;
            case InputKind.GracePeriodOver:
                LetGo();
                break;
            default:
                throw new InvalidOperationException($"The step core does not take the input {input.Kind}.");
        }

        return _effects;
    }

    /// <summary>
    /// The snapshot of the run just after the event of a <see cref="EffectKind.Publish"/>. The loop builds each as it publishes the
    /// event, in the order the effects give them, since each is built on the one before.
    /// </summary>
    public Snapshot SnapshotAfter(SnapshotChange change) => _snapshots.Take(change);

    /// <summary>The run as it is now. The snapshot of every event published so far must have been built first.</summary>
    public Snapshot TakeSnapshot() => _snapshots.Take(ChangeSinceLastEvent());

    /// <summary>
    /// The run starts: its start is recorded, then published, and the tasks with no dependency become ready and those that fit start.
    /// A drain and a stop already asked for are applied once the tasks have become ready and before any starts, so none does.
    /// </summary>
    private void Start(AskedBeforeTheStart asked)
    {
        _life = Life.Running;
        _askedBeforeTheStart = asked;
        _milestones = _milestones with { Started = _now };
        Record(
            _onAJournal ? [new RunStartedEntry(_now, _user, _machine, _maxRunningTasks, _limits, 0, 0)] : [],
            JournalFlush.ToDisk,
            AfterWrite.RunStart);
    }

    /// <summary>
    /// The run's start is written: it is published, then each node already settled tells its dependents, the tasks whose
    /// dependencies are all met become ready, the groups settled so far are announced, a drain and a stop already asked for are
    /// applied, and the ready tasks that fit start.
    /// </summary>
    private void AfterTheRunStartIsWritten()
    {
        Publish(new RunStarted(_now, _source));
        foreach (var node in _graph.Nodes)
        {
            var status = StatusAt(node.Position);
            if (status.IsSettled())
            {
                _dependencies.Settled(node.Position, status, _becomingReady);
            }
        }

        // A task with no dependency is told by no node; one whose last dependency a settled node met is in the list already.
        foreach (var task in _graph.Tasks)
        {
            if (task.Upstreams.IsEmpty)
            {
                _becomingReady.Add(task);
            }
        }

        MakeReady();
        AnnounceSettledGroups(_rollUp.TakeSettled());
        ApplyWhatWasAskedBeforeTheStart();
        StartWhatFits();
    }

    /// <summary>
    /// A task's body has ended: the task settles, and its settle is recorded, flushed, before it is published and before what
    /// follows from it is decided. A task that is not running gives nothing.
    /// </summary>
    private void BodyEnded(BuiltTask task, TaskOutcome ended)
    {
        // Only a stop tells the tasks, so only after a stop is a cancellation exception a cancellation: during a drain it is a failure.
        var changes = InstanceOf(task).Settle(HasStopped ? ended.OnceStopped() : ended, _now);
        if (changes.Count == 0)
        {
            return;
        }

        List<JournalEntry> entries = [];
        Settle(task, changes, entries);
        if (InstanceOf(task).Status is NodeStatus.Failed)
        {
            Block(task, entries);
        }

        Record(entries, JournalFlush.ToDisk, AfterWrite.Settles);
    }

    /// <summary>
    /// Gives back the slots of a task that has just settled and applies the changes its instance made in settling, holding their
    /// events for the write that records the settle.
    /// </summary>
    private void Settle(BuiltTask task, IReadOnlyList<TaskChange> changes, List<JournalEntry> entries)
    {
        _slots.GiveBack(task);
        foreach (var limit in task.HeldLimits)
        {
            _held.LimitGivenBack(limit);
        }

        foreach (var change in changes)
        {
            ApplyAndHold(task, change, entries);
        }

        _settling.Add(task);
    }

    /// <summary>
    /// Applies a change that the write under way records, holding its event for that write, and adds the entry of a settle.
    /// </summary>
    private void ApplyAndHold(BuiltTask task, TaskChange change, List<JournalEntry> entries)
    {
        Apply(task, change);
        if (change.Event is { } produced)
        {
            Hold(produced);
        }

        if (_onAJournal && change.Event is TaskStatusChanged moved && moved.To.IsSettled())
        {
            entries.Add(TaskSettledEntry.Of(moved.Node, _now));
        }
    }

    /// <summary>
    /// Blocks every pending task the failure of <paramref name="failed"/> reaches, as <see cref="UnmetDependencies.Failed"/> finds
    /// them, in declaration order. Each records the failed task as its cause, and its settle is held for the write that records the
    /// failure.
    /// </summary>
    private void Block(BuiltTask failed, List<JournalEntry> entries)
    {
        _dependencies.Failed(failed, _isPending, _blocked);
        foreach (var task in _blocked)
        {
            ApplyAndHold(task, InstanceOf(task).Block(failed.Path, _now), entries);
            _settling.Add(task);
        }

        _blocked.Clear();
    }

    /// <summary>
    /// The settles are on disk: they are published, then each settled task and each group they settled tells its dependents, the
    /// tasks whose dependencies that met become ready, the groups are announced, the deepest first, and the ready tasks that fit
    /// start.
    /// </summary>
    private void AfterTheSettlesAreWritten()
    {
        PublishHeld();
        foreach (var task in _settling)
        {
            _dependencies.Settled(task.Position, InstanceOf(task).Status, _becomingReady);
        }

        _settling.Clear();
        var settledGroups = _rollUp.TakeSettled();
        foreach (var group in settledGroups)
        {
            _dependencies.Settled(group.Position, _rollUp.StatusOf(group.Position), _becomingReady);
        }

        MakeReady();
        AnnounceSettledGroups(settledGroups);
        StartWhatFits();
    }

    /// <summary>Publishes one event for each group settled, in the order given, each with the group's status and counts.</summary>
    private void AnnounceSettledGroups(IReadOnlyList<BuiltGroup> settledGroups)
    {
        foreach (var group in settledGroups)
        {
            Publish(new GroupSettled(_now, group.Path, _rollUp.StatusOf(group.Position), _rollUp.CountsOf(group)));
        }
    }

    /// <summary>
    /// Tries the ready tasks in declaration order and starts each one whose slots it can take, as the existing engine does. The
    /// starts are recorded, not flushed, and published and their bodies started once that write lands. With nothing to start,
    /// the run ends if nothing is running and nothing is ready.
    /// </summary>
    private void StartWhatFits()
    {
        if (IsEndingEarly)
        {
            EndIfDone();
            return;
        }

        _held.BeginWalk();
        while (NextToTry() is var (position, wasHeld))
        {
            var task = _graph.TaskAt(position);
            var answer = _slots.Take(task);
            if (answer is SlotsAnswer.MaxRunningTasksReached)
            {
                break;
            }

            if (!wasHeld)
            {
                _ready.Remove(position);
            }

            if (answer is SlotsAnswer.Took)
            {
                if (wasHeld)
                {
                    _held.StartedFirst();
                }

                // Moved at once, not after the walk, so the snapshot with each start counts only the slots taken so far.
                var change = InstanceOf(task).Start(_now);
                Apply(task, change);
                Hold(change.Event!);
                _starting.Add(task);
            }
            else
            {
                _held.Hold(task, _slots.FullLimitOf(task));
            }
        }

        _held.EndWalk();
        if (_starting.Count == 0)
        {
            EndIfDone();
            return;
        }

        Record(
            _onAJournal ? [.. _starting.Select(task => new TaskStartedEntry(task.Path.ToString(), _now))] : [],
            JournalFlush.None,
            AfterWrite.Starts);
    }

    /// <summary>The starts are written: they are published, then their bodies start, in declaration order.</summary>
    private void AfterTheStartsAreWritten()
    {
        PublishHeld();
        foreach (var task in _starting)
        {
            _effects.Add(Effect.StartBody(task));
        }

        _starting.Clear();
    }

    /// <summary>
    /// The position of the task to try next, the first among the ready tasks and the held tasks that fit, and whether it is held;
    /// <see langword="null"/> once there is none.
    /// </summary>
    private (int Position, bool WasHeld)? NextToTry()
    {
        var held = _held.First();
        if (_ready.Count > 0 && (held is not { } first || _ready.Min < first))
        {
            return (_ready.Min, false);
        }

        return held is { } position ? (position, true) : null;
    }

    /// <summary>
    /// Makes ready, in declaration order, each task whose dependencies have all just been met and that is still pending: one a
    /// failure has blocked stays blocked.
    /// </summary>
    private void MakeReady()
    {
        _becomingReady.Sort(static (one, other) => one.Position.CompareTo(other.Position));
        foreach (var task in _becomingReady)
        {
            if (InstanceOf(task).Status is not NodeStatus.Pending)
            {
                continue;
            }

            var change = InstanceOf(task).MakeReady(_now);
            Apply(task, change);
            Publish(change.Event!);
            _ready.Add(task.Position);
        }

        _becomingReady.Clear();
    }

    /// <summary>The status of the node at <paramref name="position"/>: a task's is its instance's, and a group's is the roll-up's.</summary>
    private NodeStatus StatusAt(int position)
        => _instances[position] is { } instance ? instance.Status : _rollUp.StatusOf(position);

    /// <summary>
    /// The run ends when no task is running and, unless a drain or a stop has been applied, none is ready: its end is recorded,
    /// flushed, then the journal is closed and the end is published. Its outcome is complete when every task succeeded, and
    /// otherwise stopped after a drain or a stop, and incomplete without one.
    /// </summary>
    private void EndIfDone()
    {
        if (HasEnded || !NothingIsRunning || (!IsEndingEarly && _rollUp.InRun(NodeStatus.Ready) > 0))
        {
            return;
        }

        var outcome = _rollUp.InRun(NodeStatus.Succeeded) == _graph.Tasks.Length ? RunOutcome.Complete
            : IsEndingEarly ? RunOutcome.Stopped
            : RunOutcome.Incomplete;
        _life = Life.Ended;
        _milestones = _milestones with { Ended = _now, Outcome = outcome };
        Record(_onAJournal ? [new RunEndedEntry(_now, outcome)] : [], JournalFlush.ToDisk, AfterWrite.End);
    }

    private void AfterTheEndIsWritten()
    {
        var outcome = _milestones.Outcome!.Value;
        if (_onAJournal)
        {
            _effects.Add(Effect.CloseJournal);
        }

        Publish(new RunEnded(_now, outcome));
        _effects.Add(Effect.EndRun(outcome));
    }

    /// <summary>Applies the drain and the stop already asked for when the run started, the drain first, each as if asked for then.</summary>
    private void ApplyWhatWasAskedBeforeTheStart()
    {
        if (_askedBeforeTheStart.Drained)
        {
            ApplyDrain();
        }

        if (_askedBeforeTheStart.Stopped)
        {
            ApplyStop(StopRequester.App);
        }
    }

    /// <summary>
    /// The app asks the run to drain: unless a drain or a stop has been applied, nothing starts from now on, no task is told, and
    /// the run ends once nothing is running. A drain asked for again, or during a stop, changes nothing.
    /// </summary>
    private void Drain()
    {
        if (_life is not Life.Running)
        {
            return;
        }

        ApplyDrain();
        EndIfDone();
    }

    private void ApplyDrain()
    {
        _life = Life.Draining;
        _milestones = _milestones with { Drain = new DrainState(_now) };
        Publish(new DrainRequested(_now));
    }

    /// <summary>
    /// A stop is asked for: unless one has been applied, nothing starts from now on, the tasks are signalled, and the run waits for
    /// those running for the grace period. A zero grace period lets go of them at once; with nothing running the run ends. A stop
    /// can follow a drain, and a stop asked for again changes nothing, whoever asks: only the grace period ends the wait.
    /// </summary>
    private void Stop(StopRequester by)
    {
        if (_life is not (Life.Running or Life.Draining))
        {
            return;
        }

        ApplyStop(by);
        if (_gracePeriod == TimeSpan.Zero && !NothingIsRunning)
        {
            LetGo();
            return;
        }

        EndIfDone();
    }

    /// <summary>
    /// Applies a stop: the tasks are signalled and, when tasks are running and the grace period is limited and more than zero, its
    /// timer is started, both before the stop is published, so the token is signalled before the run can end and a reporter that
    /// hears of the stop finds the grace period already running.
    /// </summary>
    private void ApplyStop(StopRequester by)
    {
        _life = Life.Stopping;
        _milestones = _milestones with
        {
            Stop = new StopState(_now, _gracePeriod, GracePeriodRules.DeadlineFrom(_now, _gracePeriod), by),
        };
        _effects.Add(Effect.SignalTheTasks);
        if (!NothingIsRunning && !GracePeriodRules.IsUnlimited(_gracePeriod) && _gracePeriod > TimeSpan.Zero)
        {
            _effects.Add(Effect.StartGracePeriod);
        }

        Publish(new StopRequested(_now, by, _gracePeriod));
    }

    /// <summary>
    /// The grace period is over, or was zero: unless nothing is running, the grace end is published at once, then each running
    /// task is let go of in declaration order, settled as left behind. Those settles are recorded, flushed, before they are
    /// published, as any settle is, and what follows from them is decided once that write lands.
    /// </summary>
    private void LetGo()
    {
        if (NothingIsRunning)
        {
            return;
        }

        Publish(new GraceEnded(_now));
        List<JournalEntry> entries = [];
        foreach (var task in _graph.Tasks)
        {
            if (InstanceOf(task).Status is NodeStatus.Running)
            {
                Settle(task, InstanceOf(task).LeaveBehind(_now), entries);
            }
        }

        Record(entries, JournalFlush.ToDisk, AfterWrite.Settles);
    }

    /// <summary>
    /// Asks for a write as the last effect of the list and keeps what follows it until its result is given; or, with nothing to
    /// write, goes straight on.
    /// </summary>
    private void Record(List<JournalEntry> entries, JournalFlush flush, AfterWrite follows)
    {
        if (entries.Count > 0)
        {
            _effects.Add(Effect.Record(entries, flush));
            _afterWrite = follows;
            return;
        }

        Continue(follows);
    }

    /// <summary>The result of the write under way: what follows it is decided now.</summary>
    private void Written(in Input input)
    {
        if (input.Kind is not InputKind.Written)
        {
            throw new InvalidOperationException($"The step core awaits the result of a write, and was given the input {input.Kind}.");
        }

        if (input.Failure is { } failure)
        {
            // A failed write faults the run until the core decides what follows one: nothing starts, a stop, and the run unrecorded.
            throw new InvalidOperationException("A journal write failed, and the new engine does not yet decide what follows one.", failure);
        }

        var follows = _afterWrite;
        _afterWrite = AfterWrite.Nothing;
        Continue(follows);
    }

    private void Continue(AfterWrite follows)
    {
        switch (follows)
        {
            case AfterWrite.RunStart:
                AfterTheRunStartIsWritten();
                break;
            case AfterWrite.Settles:
                AfterTheSettlesAreWritten();
                break;
            case AfterWrite.Starts:
                AfterTheStartsAreWritten();
                break;
            case AfterWrite.End:
                AfterTheEndIsWritten();
                break;
        }
    }

    /// <summary>Records a task as it is after a change to its instance, for the next snapshot, and tells the roll-up of the change.</summary>
    private void Apply(BuiltTask task, TaskChange change)
    {
        if (change.Task is { } node)
        {
            _snapshots.Record(task.Position, node);
        }
        else
        {
            _snapshots.Record(task.Position, InstanceOf(task));
        }

        _rollUp.TaskChanged(task, change.Event, InstanceOf(task).WarningCount);
    }

    /// <summary>Publishes an event now, with what has changed in the snapshot since the last event.</summary>
    private void Publish(RunEvent runEvent) => _effects.Add(Effect.Publish(new StepEvent(runEvent, ChangeSinceLastEvent())));

    /// <summary>Closes an event's change now, and holds its publish until the write under way has landed.</summary>
    private void Hold(RunEvent runEvent) => _heldEvents.Add(new StepEvent(runEvent, ChangeSinceLastEvent()));

    private void PublishHeld()
    {
        foreach (var held in _heldEvents)
        {
            _effects.Add(Effect.Publish(held));
        }

        _heldEvents.Clear();
    }

    /// <summary>What has changed in the snapshot since the last event, which from now on is part of the snapshot built next.</summary>
    private SnapshotChange ChangeSinceLastEvent()
    {
        _rollUp.HandOverChanged(_snapshots);
        return _snapshots.Close(_milestones, _rollUp.RunCounts);
    }

    // Indexed by position among the nodes, so a group's place is empty.
    private TaskInstance InstanceOf(BuiltTask task) => _instances[task.Position]!;
}
