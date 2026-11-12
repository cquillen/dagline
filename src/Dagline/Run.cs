using System.Diagnostics.CodeAnalysis;

namespace Dagline;

/// <summary>
/// One run of a graph: the shell that carries out the steps its <see cref="RunSteps"/> decides, and owns every effect.
/// Every input is given to the steps under one lock, one at a time, so the order of the run's events
/// is a function of the order in which things happen to it.
/// </summary>
/// <remarks>
/// <para>
/// The run owns the lock, the current snapshot, the grace-period timer, the registration on the stop signal, the source of the token
/// every task sees, the task bodies, delivery to reporters and the end result. It reads the clock once each time it takes the lock,
/// and passes that time to the steps, except for a report that is never an event, which needs no time. No consumer code runs under the lock: task bodies are started on the thread pool,
/// and reporters are called by <see cref="ReporterDelivery"/>.
/// </para>
/// <para>
/// Each step is carried out under the lock, in this order: the step's journal entries are written in one write, flushed to the disk
/// if the step asks; the tasks' token is signalled, if the step asks; if the step ends the run, the registration on the signal,
/// the timer and the journal are released; the events are published in order, each with its snapshot, which is
/// taken then and made the current snapshot before the event is handed to reporters; the bodies of the
/// tasks the step starts are started; the grace-period timer is started, if the step asks; and if the step ends the run, the wait for
/// reporters listens for stop requests beyond those applied, delivery is completed and the end result is set.
/// So the token is signalled before the run can end, and the wait for reporters listens before the run's end is set.
/// </para>
/// <para>
/// Each input that can settle a task or apply a stop is followed, under the same lock and with the same time, by the journal write
/// failed input when a write failed while it was carried out, and then by the end input, which ends the run when nothing is left to do.
/// After a write fails nothing more is written, and the run ends with a <see cref="JournalWriteException"/>.
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
    private readonly RunSteps _steps;
    private readonly TaskCompletionSource<RunOutcome> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StopSignal? _signal;
    private readonly CancellationTokenSource _stopping = new();

    private JournalFile? _journal;
    private Exception? _writeFailure;
    private bool _writeFailureGiven;
    private IDisposable? _hearsTheSignal;
    private ITimer? _gracePeriodTimer;
    private volatile Snapshot? _current;

    /// <param name="graph">The graph to run.</param>
    /// <param name="settings">The settings of the run.</param>
    /// <param name="delivery">Delivery of the run's events to its reporters.</param>
    /// <param name="signal">The signal through which the app stops the run, if it can.</param>
    /// <param name="journal">
    /// The journal the run is on, already held, opened and read, or <see langword="null"/> for none. The run owns it from here,
    /// and closes it and lets go of its lock in the step that ends the run, so that a later run can continue it as soon as this one
    /// has ended.
    /// </param>
    public Run(Graph graph, RunSettings settings, ReporterDelivery delivery, StopSignal? signal, JournalFile? journal)
    {
        _signal = signal;
        _graph = graph;
        _timeProvider = settings.TimeProvider;
        _delivery = delivery;
        _journal = journal;
        _steps = new RunSteps(graph, settings, journal is null ? null : RunJournal.Of(journal));
    }

    public Snapshot Current => _current ?? TakeCurrent();

    /// <summary>
    /// Completes with the run outcome once no task is running and none is ready, or faults with the <see cref="JournalWriteException"/>
    /// the run ended with.
    /// </summary>
    public Task<RunOutcome> Ended => _ended.Task;

    /// <summary>
    /// Starts the run: the tasks with no dependency become ready and those that fit start. A signal already requested
    /// is applied once the tasks have become ready and before any starts, so the run starts nothing.
    /// </summary>
    public void Start()
    {
        Give((steps, now) => steps.Start(now, _signal?.Requests ?? 0, this));
        HearTheSignal();
    }

    /// <summary>
    /// Listens to the run's signal until the run ends. It registers once the run has started, outside the lock,
    /// since a signal already requested calls back at once: a request made in between is heard then,
    /// and one already applied changes nothing.
    /// </summary>
    private void HearTheSignal()
    {
        if (_signal is null)
        {
            return;
        }

        var hearing = _signal.OnRequest(Hear);
        lock (_gate)
        {
            if (!_ended.Task.IsCompleted)
            {
                _hearsTheSignal = hearing;
                return;
            }
        }

        // The run ended before it could listen, so nothing else will let the signal forget it.
        hearing.Dispose();
    }

    private void Hear() => Give((steps, now) => steps.StopHeard(_signal!.Requests, now));

    private void GracePeriodOver() => Give(static (steps, now) => steps.GracePeriodOver(now));

    /// <summary>Sets the status text of a task's progress.</summary>
    public void SetStatusText(BuiltTask task, string? text) => Report(task, instance => instance.SetStatusText(text));

    /// <summary>Sets the count of a task's progress, and the total it counts towards.</summary>
    public void SetCount(BuiltTask task, long current, long? total) => Report(task, instance => instance.SetCount(current, total));

    /// <summary>Adds a warning to a task. A warning that is kept is published.</summary>
    public void Warn(BuiltTask task, string warning) => Report(task, (instance, now) => instance.Warn(warning, now));

    /// <summary>Sets the message of a task's outcome.</summary>
    public void SetMessage(BuiltTask task, string message) => Report(task, instance => instance.SetMessage(message));

    /// <summary>Adds detail lines to a task's outcome.</summary>
    public void AddDetail(BuiltTask task, IEnumerable<string> texts) => Report(task, instance => instance.AddDetail(texts));

    /// <summary>
    /// Records a reported failure: from then on the task settles failed as a reported failure, however its body ends.
    /// </summary>
    public void Fail(BuiltTask task, string message, IEnumerable<string> detail)
        => Report(task, instance => instance.Fail(message, detail));

    /// <summary>
    /// Opens a waiting scope of a task. The task's first open scope begins its waiting marker, with the scope's text and the time.
    /// </summary>
    /// <returns><see langword="true"/> when the scope was opened: the task is running.</returns>
    public bool OpenWaitingScope(BuiltTask task, string? text) => Report(task, (instance, now) => instance.OpenWaitingScope(text, now));

    /// <summary>
    /// Closes one waiting scope of a task. Its last open scope ends the marker. Once the task has settled this changes nothing,
    /// since settling ended the marker.
    /// </summary>
    public void CloseWaitingScope(BuiltTask task) => Report(task, (instance, now) => instance.CloseWaitingScope(now));

    /// <summary>
    /// Gives the steps a report that is never an event, such as progress, and carries out the step it caused, if any.
    /// It reads no clock, so a task that reports often leaves the run's clock alone.
    /// </summary>
    private void Report(BuiltTask task, Func<TaskInstance, TaskChange?> report)
    {
        // Not through Give: a task may report progress millions of times, and Give would cost each report a second closure.
        lock (_gate)
        {
            CarryOut(_steps.Report(task, report));
        }
    }

    /// <summary>
    /// Gives the steps a report that may be an event, with the time read once for it, and carries out the step it caused, if any.
    /// </summary>
    /// <returns><see langword="true"/> when the report was applied: the task is running.</returns>
    private bool Report(BuiltTask task, Func<TaskInstance, DateTimeOffset, TaskChange?> report)
    {
        lock (_gate)
        {
            var steps = _steps.Report(task, report, _timeProvider.GetUtcNow());
            CarryOut(steps);

            return steps.Count > 0;
        }
    }

    /// <summary>The checkpoint of a task as it is now: empty when there is none.</summary>
    public string CheckpointOf(BuiltTask task)
    {
        lock (_gate)
        {
            return _steps.CheckpointOf(task);
        }
    }

    /// <summary>
    /// Saves a task's checkpoint, or clears it when <paramref name="text"/> is empty. Its entry is written and flushed to the disk
    /// under the lock, so that the order of the file is the order of the run's decisions and a save never lands after its task's settle.
    /// The clock is read once for the entry, and not at all for a run with no journal.
    /// </summary>
    /// <exception cref="InvalidOperationException">The task is not running: it has settled, or the run has let go of it.</exception>
    /// <exception cref="JournalWriteException">
    /// The run is on a journal that can no longer be written: this save failed, or an earlier write did. The run stops, as for any
    /// failed write.
    /// </exception>
    public void ChangeCheckpoint(BuiltTask task, string text)
    {
        Exception? failed;
        lock (_gate)
        {
            var now = _journal is null ? default : _timeProvider.GetUtcNow();
            if (_steps.ChangeCheckpoint(task, text, now) is { } entry)
            {
                Write([entry], JournalFlush.ToDisk);
            }

            failed = _journal is null ? null : _writeFailure;
            if (failed is not null)
            {
                EndTheInput(now);
            }
        }

        if (failed is not null)
        {
            throw new JournalWriteException(failed, Current);
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
            return _steps.StatusAt(position);
        }
    }

    /// <summary>
    /// Gives the steps one input, under the lock and with the time read once for it, and carries out the steps it caused, in order.
    /// </summary>
    private void Give(Func<RunSteps, DateTimeOffset, IReadOnlyList<Step>> input)
    {
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            CarryOut(input(_steps, now));
            EndTheInput(now);
        }
    }

    /// <summary>
    /// Ends an input, under the lock and with its time: gives the steps the journal write failed input, once, if a write has failed,
    /// then the end input, which ends the run when nothing is left to do.
    /// </summary>
    private void EndTheInput(DateTimeOffset now)
    {
        if (_writeFailure is { } cause && !_writeFailureGiven)
        {
            _writeFailureGiven = true;
            CarryOut(_steps.JournalWriteFailed(cause, now));
        }

        CarryOut(_steps.EndIfDone(now));
    }

    /// <summary>
    /// Writes entries to the journal, if the run has one and no write to it has failed. When the write fails, nothing more is
    /// written, though the journal stays held until the run ends, and the failure waits for the input to end. Whatever else the
    /// input decided is still carried out, since its events and starts are already facts of the run.
    /// </summary>
    private void Write(IReadOnlyList<JournalEntry> entries, JournalFlush flush)
    {
        if (_journal is null || _writeFailure is not null)
        {
            return;
        }

        try
        {
            _journal.Append(entries, flush);
        }
        catch (Exception cause) when (FileSystemFailure.Is(cause))
        {
            _writeFailure = cause;
        }
    }

    /// <summary>Carries out the steps one input caused, under the lock and in order.</summary>
    private void CarryOut(IReadOnlyList<Step> steps)
    {
        // Indexed, not enumerated, so that no enumerator is allocated for each input.
        for (var i = 0; i < steps.Count; i++)
        {
            CarryOut(steps[i]);
        }
    }

    /// <summary>Carries out one step, under the lock, in the order the remarks of this type give.</summary>
    private void CarryOut(Step step)
    {
        if (step.Entries.Count > 0)
        {
            Write(step.Entries, step.Flush);
        }

        if (step.SignalsTheTasks)
        {
            SignalTheTasks();
        }

        if (step.Ends is not null)
        {
            // Neither Dispose waits for a callback that is running, which may be waiting for this lock.
            _hearsTheSignal?.Dispose();
            _gracePeriodTimer?.Dispose();
            _journal?.Dispose();
            _journal = null;
        }

        Publish(step.Events);
        foreach (var task in step.Starts)
        {
            _ = Task.Run(() => RunBodyAsync(task));
        }

        if (step.GracePeriodTimer is { } gracePeriod)
        {
            _gracePeriodTimer = _timeProvider.CreateTimer(
                static run => ((Run)run!).GracePeriodOver(),
                this,
                gracePeriod,
                Timeout.InfiniteTimeSpan);
        }

        if (step.Ends is { } end)
        {
            // Only once RunEnded is queued, as ListenBeyond requires: a request heard while a reporter has handled every event
            // but RunEnded is still to come would leave that reporter to be waited for.
            _delivery.ListenBeyond(end.RequestsApplied);
            _delivery.Complete();
            if (end.Error is { } error)
            {
                _ended.SetException(error);
            }
            else
            {
                _ended.SetResult(end.Outcome!.Value);
            }
        }
    }

    /// <summary>
    /// Signals the token every task sees. The token reads as cancelled at once, while the callbacks that task bodies
    /// registered on it run later on the thread pool, so none runs under the lock or holds up the end of the run.
    /// </summary>
    private void SignalTheTasks()
    {
        _ = _stopping.CancelAsync().ContinueWith(
            // A callback a task body registered on its token threw. That is the body's affair, and the stop goes on.
            static signalled => _ = signalled.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Publishes a step's events in order. Each event's snapshot is taken as it is published, and is the current one before the
    /// event is handed to reporters, so a reporter never reads a current snapshot older than an event it holds.
    /// A step with no events changed only the snapshot, so it only marks the current snapshot as out of date.
    /// </summary>
    private void Publish(IReadOnlyList<StepEvent> events)
    {
        if (events.Count == 0)
        {
            _current = null;
            return;
        }

        // Indexed, not enumerated, so that no enumerator is allocated for each step.
        for (var i = 0; i < events.Count; i++)
        {
            var snapshot = _steps.SnapshotAfter(events[i]);
            _current = snapshot;
            if (events[i].Event is RunEnded { Error: JournalWriteException failed })
            {
                failed.Snapshot = snapshot;
            }

            _delivery.Publish(events[i].Event, snapshot);
        }
    }

    // No token: a run is stopped through its signal, and a task body takes none.
    private async Task RunBodyAsync(BuiltTask task)
    {
        var outcome = await InvokeBodyAsync(task).ConfigureAwait(false);
        Give((steps, now) => steps.BodyEnded(task, outcome, now));
    }

    /// <summary>
    /// Invokes the body of a task and says how it ended. This is the one place a task body is invoked,
    /// and so the one catch-all for it: whatever escapes the body is a failure of that task and of nothing else,
    /// or its cancellation when it is a cancellation exception and the run has been stopped by the time the task settles.
    /// The exception is read here, outside the lock and before the task settles, because its members are consumer code too.
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

    // A progress report only marks the current snapshot as out of date, so a task that reports often costs
    // one snapshot for each read and not one for each report.
    private Snapshot TakeCurrent()
    {
        lock (_gate)
        {
            return _current ??= _steps.TakeSnapshot();
        }
    }
}
