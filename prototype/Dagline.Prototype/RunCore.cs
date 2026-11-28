namespace Dagline.Prototype;

/// <summary>
/// The reducer: every decision of one run. It takes one input and answers with the effects that input caused, in order.
/// It holds no lock, no thread and no clock, and it is given one input at a time by the loop.
/// </summary>
/// <remarks>
/// <para>
/// Adapted from Dagline's RunSteps. What is new: a step is cut wherever a journal write is. A Record effect is the last of its
/// list, the core remembers what follows that write in <see cref="_pending"/>, and the only input it then accepts is Written.
/// A run with no journal, or whose journal has failed, asks for no Record at all: the core goes straight on, in the same call.
/// </para>
/// <para>
/// It is pure but for one thing: it reads the progress slots of the running tasks when it decides events that are about to be
/// published (see <see cref="SampleRunning"/>).
/// </para>
/// </remarks>
internal sealed class RunCore
{
    private enum AfterWrite
    {
        Nothing,
        RunStart,
        Settles,
        Starts,
        Save,
        End,
    }

    private readonly Graph _graph;
    private readonly TimeSpan _gracePeriod;
    private readonly int _maxRunningTasks;
    private readonly IReadOnlyDictionary<string, int> _limits;
    private readonly ISnapshotSource _source;
    private readonly RollUp _rollUp;
    private readonly SortedSet<int> _ready = [];
    private readonly HeldTasks _held;
    private readonly SortedSet<int> _dependents = [];
    private readonly List<BuiltTask> _becomingReady = [];
    private readonly ProtoTask?[] _instances;
    private readonly Slots _slots;
    private readonly SnapshotTaker _snapshots;
    private readonly List<Effect> _effects = [];

    // The running tasks, whose slots are sampled. Replaced, never changed, so a reader of the current snapshot can hold one.
    private ProtoTask[] _running = [];

    // Tasks changed by an input that is not an event, or by a sample: their nodes are built once, when the next change is closed.
    private readonly List<int> _dirty = [];
    private readonly bool[] _isDirty;

    private RunMilestones _milestones = new(Started: default, Drain: null, Stop: null, Ended: null, Outcome: null);
    private DateTimeOffset _now;
    private bool _started;
    private bool _heldDrain;
    private bool _heldStop;

    // The journal: live while the run has one and no write to it has failed.
    private bool _journalLive;
    private bool _unrecorded;

    // What follows the write under way, and what it needs.
    private AfterWrite _pending;
    private readonly List<(BuiltTask Task, TaskChange Change)> _pendingChanges = [];
    private readonly List<BuiltTask> _pendingSettled = [];
    private readonly List<BuiltTask> _pendingRan = [];
    private readonly List<BuiltTask> _tentativeStarts = [];
    private (BuiltTask Task, string Text, SaveCompletion Completion) _pendingSave;
    private RunOutcome _pendingOutcome;

    public RunCore(Graph graph, RunSettings settings, bool hasJournal, ISnapshotSource source)
    {
        _graph = graph;
        _gracePeriod = settings.GracePeriod;
        _maxRunningTasks = settings.Sizes.MaxRunningTasks;
        _limits = graph.LimitNames.Zip(settings.Sizes.Limits).ToDictionary(limit => limit.First.Text, limit => limit.Second, LimitName.TextComparer);
        _source = source;
        _journalLive = hasJournal;
        _slots = new Slots(graph.LimitNames, settings.Sizes);
        _held = new HeldTasks(graph, _slots);
        _instances = new ProtoTask?[graph.Nodes.Length];
        _isDirty = new bool[graph.Nodes.Length];
        var atTheStart = new NodeSnapshot?[graph.Nodes.Length];
        foreach (var task in graph.Tasks)
        {
            _instances[task.Position] = new ProtoTask(task);
            atTheStart[task.Position] = _instances[task.Position]!.ToNodeSnapshot();
        }

        _rollUp = new RollUp(graph, atTheStart);
        _snapshots = new SnapshotTaker(
            graph.Nodes.Select(node => atTheStart[node.Position] ?? _rollUp.SnapshotOf((BuiltGroup)node)),
            graph.Positions,
            _slots,
            new SnapshotJournal(null));
    }

    public bool HasEnded => _milestones.Ended is not null;

    public bool AwaitsWrite => _pending is not AfterWrite.Nothing;

    public RunOutcome? Outcome => _milestones.Outcome;

    /// <summary>The write that failed, when one has: the run is unrecorded unless it was only the write of the run's end.</summary>
    public Exception? JournalFailure { get; private set; }

    /// <summary>The running tasks as of the last decision. A reader of the current snapshot samples their slots.</summary>
    public ProtoTask[] Running => _running;

    public TaskSlot SlotOf(BuiltTask task) => InstanceOf(task).Slot!;

    /// <summary>
    /// Applies one input and answers with its effects, in order. The list is reused: it is valid until the next call.
    /// </summary>
    /// <param name="input">The input.</param>
    /// <param name="now">
    /// The time of the happening. A Written input continues a step, and the step keeps the time of the happening that began it,
    /// so the time given with Written is ignored.
    /// </param>
    public IReadOnlyList<Effect> Apply(in Input input, DateTimeOffset now)
    {
        _effects.Clear();
        if (_pending is not AfterWrite.Nothing)
        {
            if (input.Kind is not InputKind.Written)
            {
                throw new InvalidOperationException($"The core awaits the result of a write, and was given {input.Kind}.");
            }

            var follows = _pending;
            _pending = AfterWrite.Nothing;
            var failure = (Exception?)input.Payload;
            if (failure is not null)
            {
                _journalLive = false;
                JournalFailure = failure;

                // The one exception: when only the write of the run's end fails, every settle is on disk and the run keeps its outcome.
                _unrecorded = follows is not AfterWrite.End;
            }

            Continue(follows, written: failure is null);
            return _effects;
        }

        if (input.Kind is InputKind.Written)
        {
            throw new InvalidOperationException("The core was given the result of a write it did not ask for.");
        }

        if (HasEnded)
        {
            // A save is the one input that must always be answered.
            if (input.Kind is InputKind.CheckpointSave)
            {
                _effects.Add(Effect.CompleteSave((SaveCompletion)input.Payload!, input.Task!, SaveAnswer.NotRunning, input.Text!));
            }

            return _effects;
        }

        _now = now;
        if (!_started)
        {
            BeforeTheStart(input);
            return _effects;
        }

        switch (input.Kind)
        {
            case InputKind.BodyEnded:
                BodyEnded(input.Task!, (TaskOutcome)input.Payload!);
                break;
            case InputKind.Warning:
                Reported(input.Task!, InstanceOf(input.Task!).Warn(input.Text!, _now));
                break;
            case InputKind.WaitOpened:
                Reported(input.Task!, InstanceOf(input.Task!).OpenWaitingScope(input.Text, _now));
                break;
            case InputKind.WaitClosed:
                Reported(input.Task!, InstanceOf(input.Task!).CloseWaitingScope(_now));
                break;
            case InputKind.Message:
                Reported(input.Task!, InstanceOf(input.Task!).SetMessage(input.Text!));
                break;
            case InputKind.Detail:
                Reported(input.Task!, InstanceOf(input.Task!).AddDetail((string[])input.Payload!));
                break;
            case InputKind.Failure:
                Reported(input.Task!, InstanceOf(input.Task!).Fail(input.Text!, (string[])input.Payload!));
                break;
            case InputKind.CheckpointSave:
                CheckpointSave(input.Task!, input.Text!, (SaveCompletion)input.Payload!);
                break;
            case InputKind.Drain:
                Drain();
                EndIfDone();
                break;
            case InputKind.Stop:
                StopThenGoOn(StopRequester.App);
                break;
            case InputKind.GraceElapsed:
                LetGoThenGoOn();
                break;
            default:
                throw new InvalidOperationException($"{input.Kind} is not an input of the core.");
        }

        return _effects;
    }

    /// <summary>The snapshot just after an event, as the loop publishes it. Each is built on the one before, so they are taken in order.</summary>
    public Snapshot SnapshotAfter(SnapshotChange change) => _snapshots.Take(change);

    /// <summary>The run as it is now. The snapshot of every event decided so far must have been taken first.</summary>
    public Snapshot TakeSnapshot()
    {
        SampleRunning();
        return _snapshots.Take(ChangeSinceLastEvent());
    }

    // A drain or a stop that arrives before the start is held, and applied once the tasks have become ready and before any starts.
    private void BeforeTheStart(in Input input)
    {
        switch (input.Kind)
        {
            case InputKind.Drain:
                _heldDrain = true;
                break;
            case InputKind.Stop:
                _heldStop = true;
                break;
            case InputKind.Start:
                _started = true;
                _milestones = _milestones with { Started = _now };
                Record(
                    _journalLive ? [new RunStartedEntry(_now, Environment.UserName, Environment.MachineName, _maxRunningTasks, _limits, 0, 0)] : [],
                    JournalFlush.ToDisk,
                    AfterWrite.RunStart);
                break;
            default:
                throw new InvalidOperationException($"The run has not started, and was given {input.Kind}.");
        }
    }

    /// <summary>
    /// Asks for a write and remembers what follows it; or, when there is nothing to write to, goes straight on. Whoever calls this
    /// decides nothing after it: what follows the write is decided only once its result is known.
    /// </summary>
    private void Record(IReadOnlyList<JournalEntry> entries, JournalFlush flush, AfterWrite follows)
    {
        if (_journalLive && entries.Count > 0)
        {
            _effects.Add(Effect.Record(entries, flush));
            _pending = follows;
            return;
        }

        Continue(follows, written: true);
    }

    private void Continue(AfterWrite follows, bool written)
    {
        switch (follows)
        {
            case AfterWrite.RunStart:
                AfterRunStartWritten(written);
                break;
            case AfterWrite.Settles:
                AfterSettlesWritten(written);
                break;
            case AfterWrite.Starts:
                AfterStartsWritten(written);
                break;
            case AfterWrite.Save:
                AfterSaveWritten(written);
                break;
            case AfterWrite.End:
                AfterEndWritten();
                break;
        }
    }

    private void AfterRunStartWritten(bool written)
    {
        Publish(new RunStarted(_now, _source));
        MakeReady(_graph.Tasks.Select(task => task.Position));
        AnnounceSettledGroups(_rollUp.TakeSettled());
        if (_heldDrain)
        {
            Drain();
        }

        if (_heldStop)
        {
            StopThenGoOn(StopRequester.App);
        }
        else if (!written)
        {
            StopThenGoOn(StopRequester.Dagline);
        }
        else
        {
            StartWhatFitsThenEndIfDone();
        }
    }

    private void BodyEnded(BuiltTask task, TaskOutcome ended)
    {
        var instance = InstanceOf(task);

        // Only a stop tells the tasks, so only after a stop is a cancellation exception a cancellation.
        var changes = instance.Settle(HasStopped ? ended.OnceStopped() : ended, _now);
        if (changes.Count == 0)
        {
            return;
        }

        HoldSettle(task, changes, ran: true);
        if (instance.Status is NodeStatus.Failed)
        {
            foreach (var blocked in PendingTasksThatRequire(task).Order().Select(_graph.TaskAt))
            {
                HoldSettle(blocked, [InstanceOf(blocked).Block(task.Path, _now)], ran: false);
            }
        }

        Record(SettleEntries(), JournalFlush.ToDisk, AfterWrite.Settles);
    }

    /// <summary>
    /// A settle is decided before its write and published after it: the instance has moved, and everything else that follows from
    /// the move (the slots, the roll-up, the snapshot, the events, the dependents) waits in the pending lists for the write's result.
    /// </summary>
    private void HoldSettle(BuiltTask task, IReadOnlyList<TaskChange> changes, bool ran)
    {
        _pendingSettled.Add(task);
        if (ran)
        {
            _pendingRan.Add(task);
            _running = [.. _running.Where(running => running.Task != task)];
        }

        foreach (var change in changes)
        {
            _pendingChanges.Add((task, change));
        }
    }

    private IReadOnlyList<JournalEntry> SettleEntries()
    {
        if (!_journalLive)
        {
            return [];
        }

        List<JournalEntry> entries = [];
        foreach (var (_, change) in _pendingChanges)
        {
            if (change.Event is TaskStatusChanged moved && moved.To.IsSettled())
            {
                entries.Add(TaskSettledEntry.Of(moved.Node, _now));
            }
        }

        return entries;
    }

    /// <summary>
    /// The settles are published whether or not their write landed, and their dependents become ready. When it did not land,
    /// Dagline applies a stop, so nothing starts.
    /// </summary>
    private void AfterSettlesWritten(bool written)
    {
        SampleRunning();
        foreach (var task in _pendingRan)
        {
            _slots.GiveBack(task);
            foreach (var limit in task.HeldLimits)
            {
                _held.LimitGivenBack(limit);
            }
        }

        foreach (var (task, change) in _pendingChanges)
        {
            ApplyChange(task, change);
        }

        var settledGroups = _rollUp.TakeSettled();
        MakeReady(DependentsOf(_pendingSettled, settledGroups));
        AnnounceSettledGroups(settledGroups);
        _pendingChanges.Clear();
        _pendingSettled.Clear();
        _pendingRan.Clear();

        if (written)
        {
            StartWhatFitsThenEndIfDone();
        }
        else
        {
            StopThenGoOn(StopRequester.Dagline);
        }
    }

    private void Reported(BuiltTask task, TaskChange? reported)
    {
        if (reported is not { } change)
        {
            return;
        }

        if (change.Event is null)
        {
            // Not an event, so nothing is published; the loop is told the current snapshot is out of date.
            ApplyChange(task, change);
            _effects.Add(Effect.Stale);
            return;
        }

        SampleRunning();
        ApplyChange(task, change);
    }

    private void CheckpointSave(BuiltTask task, string text, SaveCompletion completion)
    {
        var instance = InstanceOf(task);
        if (instance.Status is not NodeStatus.Running)
        {
            _effects.Add(Effect.CompleteSave(completion, task, SaveAnswer.NotRunning, text));
            return;
        }

        if (text.Length == 0 && instance.Checkpoint.Length == 0)
        {
            _effects.Add(Effect.CompleteSave(completion, task, SaveAnswer.Saved, text));
            return;
        }

        if (_unrecorded)
        {
            // An earlier write failed: nothing more is written, and the task must not believe the checkpoint was kept.
            _effects.Add(Effect.CompleteSave(completion, task, SaveAnswer.WriteFailed, text, JournalFailure));
            return;
        }

        _pendingSave = (task, text, completion);
        var path = task.Path.ToString();
        Record(
            _journalLive ? [text.Length == 0 ? new CheckpointClearedEntry(path, _now) : new CheckpointSavedEntry(path, _now, text)] : [],
            JournalFlush.ToDisk,
            AfterWrite.Save);
    }

    private void AfterSaveWritten(bool written)
    {
        var (task, text, completion) = _pendingSave;
        _pendingSave = default;
        if (written)
        {
            InstanceOf(task).Checkpoint = text;
            _effects.Add(Effect.CompleteSave(completion, task, SaveAnswer.Saved, text));
            return;
        }

        _effects.Add(Effect.CompleteSave(completion, task, SaveAnswer.WriteFailed, text, JournalFailure));
        StopThenGoOn(StopRequester.Dagline);
    }

    private void Drain()
    {
        if (_milestones.Drain is null && !HasStopped)
        {
            _milestones = _milestones with { Drain = new DrainState(_now) };
            Publish(new DrainRequested(_now));
        }
    }

    /// <summary>
    /// Applies a stop, unless one has been applied, then goes on: lets go at once when the grace period is zero, and otherwise
    /// starts nothing and ends the run if nothing is running. The effects of the stop come in this order: signal the tasks, start
    /// the grace period, publish the stop. So the token is signalled and the timer exists before any reporter hears of the stop.
    /// </summary>
    private void StopThenGoOn(StopRequester by)
    {
        if (!HasStopped)
        {
            SampleRunning();
            _milestones = _milestones with { Stop = new StopState(_now, _gracePeriod, GracePeriodRules.DeadlineFrom(_now, _gracePeriod), by) };
            _effects.Add(Effect.SignalTasks);
            var letGoAtOnce = false;
            if (!NothingIsRunning && !GracePeriodRules.IsUnlimited(_gracePeriod))
            {
                if (_gracePeriod == TimeSpan.Zero)
                {
                    letGoAtOnce = true;
                }
                else
                {
                    _effects.Add(Effect.ArmGrace(_gracePeriod));
                }
            }

            Publish(new StopRequested(_now, by, _gracePeriod));
            if (letGoAtOnce)
            {
                LetGoThenGoOn();
                return;
            }
        }

        StartWhatFitsThenEndIfDone();
    }

    /// <summary>
    /// Lets go of the tasks still running: publishes that the grace period is over, at once, then settles each as cancelled, which
    /// is a write like any settle.
    /// </summary>
    private void LetGoThenGoOn()
    {
        if (NothingIsRunning || !HasStopped)
        {
            EndIfDone();
            return;
        }

        SampleRunning();
        Publish(new GraceEnded(_now));
        foreach (var running in _running.OrderBy(running => running.Task.Position).ToList())
        {
            HoldSettle(running.Task, running.LeaveBehind(_now), ran: true);
        }

        Record(SettleEntries(), JournalFlush.ToDisk, AfterWrite.Settles);
    }

    /// <summary>
    /// Starts the ready tasks that fit, unless a drain or a stop has been applied, then ends the run if nothing is left to do.
    /// A start is a journal entry too, written (not flushed) before the task starts, so on a journal the starts are tentative
    /// until that write's result is known: the slots are taken, and nothing else has changed.
    /// </summary>
    private void StartWhatFitsThenEndIfDone()
    {
        if (!IsEndingEarly)
        {
            Walk(tentative: _journalLive);
            if (_tentativeStarts.Count > 0)
            {
                Record([.. _tentativeStarts.Select(task => new TaskStartedEntry(task.Path.ToString(), _now))], JournalFlush.None, AfterWrite.Starts);
                return;
            }
        }

        EndIfDone();
    }

    private void AfterStartsWritten(bool written)
    {
        // Given back, then taken again one at a time, so the snapshot with each start counts only the slots taken so far.
        foreach (var task in _tentativeStarts)
        {
            _slots.GiveBack(task);
        }

        if (written)
        {
            SampleRunning();
            foreach (var task in _tentativeStarts)
            {
                _slots.Take(task);
                StartNow(task);
            }

            _tentativeStarts.Clear();
            EndIfDone();
            return;
        }

        // The tasks stay ready, and nothing starts: a run that can no longer record must not start work.
        _tentativeStarts.Clear();
        StopThenGoOn(StopRequester.Dagline);
    }

    private void Walk(bool tentative)
    {
        if (!tentative)
        {
            SampleRunning();
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

                if (tentative)
                {
                    _tentativeStarts.Add(task);
                }
                else
                {
                    StartNow(task);
                }
            }
            else
            {
                _held.Hold(task, _slots.FullLimitOf(task));
            }
        }

        _held.EndWalk();
    }

    private void StartNow(BuiltTask task)
    {
        var instance = InstanceOf(task);
        ApplyChange(task, instance.Start(_now));
        _running = [.. _running, instance];
        _effects.Add(Effect.Start(task, instance.Checkpoint));
    }

    private (int Position, bool WasHeld)? NextToTry()
    {
        var held = _held.First();
        if (_ready.Count > 0 && (held is not { } first || _ready.Min < first))
        {
            return (_ready.Min, false);
        }

        return held is { } position ? (position, true) : null;
    }

    private void EndIfDone()
    {
        if (HasEnded || !NothingIsRunning || !(IsEndingEarly || _rollUp.InRun(NodeStatus.Ready) == 0))
        {
            return;
        }

        _pendingOutcome = _unrecorded ? ProtoOutcome.Unrecorded
            : _rollUp.InRun(NodeStatus.Succeeded) == _graph.Tasks.Length ? RunOutcome.Complete
            : IsEndingEarly ? RunOutcome.Stopped
            : RunOutcome.Incomplete;
        Record(_journalLive ? [new RunEndedEntry(_now, _pendingOutcome)] : [], JournalFlush.ToDisk, AfterWrite.End);
    }

    // Whether or not the end's entry landed, the run keeps the outcome it reached.
    private void AfterEndWritten()
    {
        _milestones = _milestones with { Ended = _now, Outcome = _pendingOutcome };
        _effects.Add(Effect.CloseJournal);
        Publish(new RunEnded(_now, _pendingOutcome));
    }

    private bool HasStopped => _milestones.Stop is not null;

    private bool IsEndingEarly => HasStopped || _milestones.Drain is not null;

    private bool NothingIsRunning => _rollUp.InRun(NodeStatus.Running) == 0;

    /// <summary>
    /// Reads the slot of every running task, so that the events about to be published carry progress as it is now. This is the one
    /// place the core reads something that is not its own.
    /// </summary>
    private void SampleRunning()
    {
        foreach (var running in _running)
        {
            if (running.Sample())
            {
                MarkDirty(running.Task);
                _rollUp.TaskChanged(running.Task, null, running.WarningCount);
            }
        }
    }

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

    private SortedSet<int> DependentsOf(IReadOnlyList<BuiltTask> settled, IReadOnlyList<BuiltGroup> settledGroups)
    {
        _dependents.Clear();
        foreach (var task in settled)
        {
            foreach (var dependent in task.Dependents)
            {
                _dependents.Add(dependent);
            }
        }

        foreach (var group in settledGroups)
        {
            foreach (var dependent in group.Dependents)
            {
                _dependents.Add(dependent);
            }
        }

        return _dependents;
    }

    private void MakeReady(IEnumerable<int> candidates)
    {
        foreach (var position in candidates)
        {
            var task = _graph.TaskAt(position);
            if (IsPending(task) && DependenciesAreMet(task))
            {
                _becomingReady.Add(task);
            }
        }

        foreach (var task in _becomingReady)
        {
            ApplyChange(task, InstanceOf(task).MakeReady(_now));
            _ready.Add(task.Position);
        }

        _becomingReady.Clear();
    }

    private bool DependenciesAreMet(BuiltTask task)
        => task.Upstreams.All(upstream => StatusAt(upstream.Position).Meets(upstream.Kind));

    private NodeStatus StatusAt(int position)
        => _instances[position] is { } instance ? instance.Status : _rollUp.StatusOf(position);

    private void AnnounceSettledGroups(IReadOnlyList<BuiltGroup> settledGroups)
    {
        foreach (var group in settledGroups)
        {
            Publish(new GroupSettled(_now, group.Path, _rollUp.StatusOf(group.Position), _rollUp.CountsOf(group)));
        }
    }

    private void ApplyChange(BuiltTask task, TaskChange change)
    {
        if (change.Task is { } node)
        {
            _isDirty[task.Position] = false;
            _snapshots.Record(task.Position, node);
        }
        else
        {
            MarkDirty(task);
        }

        _rollUp.TaskChanged(task, change.Event, InstanceOf(task).WarningCount);
        if (change.Event is { } produced)
        {
            Publish(produced);
        }
    }

    private void MarkDirty(BuiltTask task)
    {
        if (!_isDirty[task.Position])
        {
            _isDirty[task.Position] = true;
            _dirty.Add(task.Position);
        }
    }

    private ProtoTask InstanceOf(BuiltTask task) => _instances[task.Position]!;

    private void Publish(RunEvent runEvent) => _effects.Add(Effect.Publish(runEvent, ChangeSinceLastEvent()));

    private SnapshotChange ChangeSinceLastEvent()
    {
        foreach (var position in _dirty)
        {
            if (_isDirty[position])
            {
                _isDirty[position] = false;
                _snapshots.Record(position, _instances[position]!.ToNodeSnapshot());
            }
        }

        _dirty.Clear();
        _rollUp.HandOverChanged(_snapshots);
        return _snapshots.Close(_milestones, _rollUp.RunCounts);
    }
}

internal static class ProtoOutcome
{
    /// <summary>
    /// The fourth run outcome. Dagline's enum has three, and the prototype changes nothing in src/Dagline but InternalsVisibleTo,
    /// so it is the next value of the enum, by a cast.
    /// </summary>
    public const RunOutcome Unrecorded = (RunOutcome)3;

    public static string Name(RunOutcome? outcome) => outcome is Unrecorded ? "Unrecorded" : outcome?.ToString() ?? "none";
}
