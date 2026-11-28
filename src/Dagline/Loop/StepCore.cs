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

    private readonly Graph _graph;
    private readonly ISnapshotSource _source;
    private readonly RollUp _rollUp;
    private readonly SortedSet<int> _ready = [];
    private readonly HeldTasks _held;
    private readonly TaskInstance?[] _instances;
    private readonly Slots _slots;
    private readonly SnapshotTaker _snapshots;
    private readonly List<Effect> _effects = [];
    private readonly bool _onAJournal;
    private readonly int _maxRunningTasks;
    private readonly Dictionary<string, int> _limits;
    private readonly string _user = Environment.UserName;
    private readonly string _machine = Environment.MachineName;

    // Reused at every settle, so working out what follows from it allocates no collection of its own.
    private readonly SortedSet<int> _dependents = [];
    private readonly List<BuiltTask> _becomingReady = [];

    // What the write under way holds back: the events it records, whose changes are already closed, the tasks it settles, and the
    // tasks whose bodies start once it lands.
    private readonly List<StepEvent> _heldEvents = [];
    private readonly List<BuiltTask> _settling = [];
    private readonly List<BuiltTask> _starting = [];

    private AfterWrite _afterWrite;
    private DateTimeOffset _now;
    private RunMilestones _milestones = new(Started: default, Drain: null, Stop: null, Ended: null, Outcome: null);

    /// <param name="graph">The graph the run runs.</param>
    /// <param name="settings">The settings of the run: the sizes of its slots.</param>
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
        _limits = graph.LimitNames
            .Zip(settings.Sizes.Limits)
            .ToDictionary(limit => limit.First.Text, limit => limit.Second, LimitName.TextComparer);
        _slots = new Slots(graph.LimitNames, settings.Sizes);
        _held = new HeldTasks(graph, _slots);
        _instances = new TaskInstance?[graph.Nodes.Length];
        Samples = new TaskSamples(graph);
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

    /// <summary>
    /// The sample of each task: its progress and its counts beyond the kept, which its context writes from the task's own threads,
    /// and which the core reads as it builds a snapshot.
    /// </summary>
    public TaskSamples Samples { get; }

    /// <summary>How many times the run has tried to take the slots of a ready task so far, as <see cref="Slots.Tries"/> counts them.</summary>
    public int SlotTries => _slots.Tries;

    private bool HasEnded => _milestones.Ended is not null;

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
                Start();
                break;
            case InputKind.BodyEnded:
                BodyEnded(input.Task!, input.Outcome);
                break;
            case InputKind.WarningKept:
                Report(input.Task!, InstanceOf(input.Task!).Warn(input.Text!, _now));
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

    /// <summary>The run starts: its start is recorded, then published, and the tasks with no dependency become ready and those that fit start.</summary>
    private void Start()
    {
        _milestones = _milestones with { Started = _now };
        Record(
            _onAJournal ? [new RunStartedEntry(_now, _user, _machine, _maxRunningTasks, _limits, 0, 0)] : [],
            JournalFlush.ToDisk,
            AfterWrite.RunStart);
    }

    private void AfterTheRunStartIsWritten()
    {
        Publish(new RunStarted(_now, _source));
        MakeReady(_graph.Tasks.Select(task => task.Position));
        StartWhatFits();
    }

    /// <summary>
    /// A task's body has ended: the task settles, and its settle is recorded, flushed, before it is published and before what
    /// follows from it is decided. A task that is not running gives nothing.
    /// </summary>
    private void BodyEnded(BuiltTask task, TaskOutcome ended)
    {
        var changes = InstanceOf(task).Settle(ended, _now);
        if (changes.Count == 0)
        {
            return;
        }

        List<JournalEntry> entries = [];
        Settle(task, changes, entries);
        Record(entries, JournalFlush.ToDisk, AfterWrite.Settles);
    }

    /// <summary>
    /// Applies what a task reported: published when it is an event, and otherwise marking the current snapshot as out of date. A
    /// report on a task that is not running changes nothing, and gives nothing.
    /// </summary>
    private void Report(BuiltTask task, TaskChange? reported)
    {
        if (reported is not { } change)
        {
            return;
        }

        Apply(task, change);
        if (change.Event is { } produced)
        {
            Publish(produced);
        }
        else
        {
            _effects.Add(Effect.MarkCurrentOutOfDate);
        }
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

        _settling.Add(task);
    }

    /// <summary>The settles are on disk: they are published, then their dependents whose dependencies are met become ready and those that fit start.</summary>
    private void AfterTheSettlesAreWritten()
    {
        PublishHeld();
        MakeReady(DependentsOf(_settling));
        _settling.Clear();
        StartWhatFits();
    }

    /// <summary>
    /// Tries the ready tasks in declaration order and starts each one whose slots it can take, as the existing engine does. The
    /// starts are recorded, not flushed, and published and their bodies started once that write lands. With nothing to start,
    /// the run ends if nothing is running and nothing is ready.
    /// </summary>
    private void StartWhatFits()
    {
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
    /// Makes ready each pending task among <paramref name="candidates"/> whose dependencies are all met. Every candidate is judged
    /// before any is made ready, as the statuses are before this change.
    /// </summary>
    private void MakeReady(IEnumerable<int> candidates)
    {
        foreach (var position in candidates)
        {
            var task = _graph.TaskAt(position);
            if (InstanceOf(task).Status is NodeStatus.Pending && DependenciesAreMet(task))
            {
                _becomingReady.Add(task);
            }
        }

        foreach (var task in _becomingReady)
        {
            var change = InstanceOf(task).MakeReady(_now);
            Apply(task, change);
            Publish(change.Event!);
            _ready.Add(task.Position);
        }

        _becomingReady.Clear();
    }

    private bool DependenciesAreMet(BuiltTask task)
        => task.Upstreams.All(upstream => StatusAt(upstream.Position).Meets(upstream.Kind));

    /// <summary>The status of the node at <paramref name="position"/>: a task's is its instance's, and a group's is the roll-up's.</summary>
    private NodeStatus StatusAt(int position)
        => _instances[position] is { } instance ? instance.Status : _rollUp.StatusOf(position);

    /// <summary>The positions of the tasks, in declaration order, that depend on one of the tasks that have just settled.</summary>
    private SortedSet<int> DependentsOf(IReadOnlyList<BuiltTask> settled)
    {
        _dependents.Clear();
        foreach (var task in settled)
        {
            foreach (var dependent in task.Dependents)
            {
                _dependents.Add(dependent);
            }
        }

        return _dependents;
    }

    /// <summary>
    /// The run ends when no task is running and none is ready: its end is recorded, flushed, then the journal is closed and the
    /// end is published.
    /// </summary>
    private void EndIfDone()
    {
        if (!NothingIsRunning || _rollUp.InRun(NodeStatus.Ready) > 0)
        {
            return;
        }

        var outcome = _rollUp.InRun(NodeStatus.Succeeded) == _graph.Tasks.Length ? RunOutcome.Complete : RunOutcome.Incomplete;
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
