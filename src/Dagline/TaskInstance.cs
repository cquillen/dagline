namespace Dagline;

/// <summary>
/// One task during one run: its status, everything attached to it, whether it has reported a failure, and how many
/// waiting scopes it has open. It changes only through the operations below, each of which says what it produced.
/// </summary>
/// <remarks>
/// <para>
/// A status only moves forward, as <see cref="NodeStatusRules.CanMoveTo"/> allows. A change it does not allow throws
/// <see cref="InvalidOperationException"/>, since only a fault in Dagline can ask for one.
/// </para>
/// <para>
/// A report, an opened waiting scope and a closed waiting scope apply only while the task is running. On a task that is not running,
/// such as one the run has let go of, each is ignored and gives <see langword="null"/>.
/// </para>
/// <para>It is not thread-safe: the run calls it under its lock.</para>
/// </remarks>
internal sealed class TaskInstance
{
    /// <summary>How many warnings, and separately how many detail lines, are kept for one task. The rest are counted.</summary>
    private const int _kept = 100;

    private bool _failureReported;
    private int _openWaitingScopes;

    public TaskInstance(BuiltTask task)
        => Snapshot = new NodeSnapshot(task, NodeStatus.Pending, StatusCounts.OfOneTask(NodeStatus.Pending));

    /// <summary>The task as a snapshot shows it now.</summary>
    public NodeSnapshot Snapshot { get; private set; }

    public NodeStatus Status => Snapshot.Status;

    private bool IsRunning => Status is NodeStatus.Running;

    /// <summary>Moves the task from pending to ready: every dependency is met.</summary>
    public TaskChange MakeReady(DateTimeOffset now) => Move(Snapshot with { Status = NodeStatus.Ready }, now);

    /// <summary>Moves the task from ready to running, as of <paramref name="now"/>.</summary>
    public TaskChange Start(DateTimeOffset now) => Move(Snapshot with { Status = NodeStatus.Running, Started = now }, now);

    /// <summary>Settles a pending task as blocked by the failed task at <paramref name="cause"/>.</summary>
    public TaskChange Block(NodePath cause, DateTimeOffset now) => Move(Settled(TaskOutcome.Blocked(cause), now), now);

    /// <summary>
    /// Settles a running task, and decides the outcome it records. <see cref="TaskOutcome.LeftBehind"/> wins over everything and
    /// clears the reported failure. Otherwise a reported failure wins. Otherwise, once the run has stopped, a body that ended by
    /// a cancellation exception is cancelled. Otherwise the body's own ending is recorded.
    /// </summary>
    /// <param name="ended">How the body ended, or <see cref="TaskOutcome.LeftBehind"/> when the run lets go of the task.</param>
    /// <param name="runHasStopped">Whether a stop has been applied to the run.</param>
    /// <param name="now">When the task settles.</param>
    /// <returns>
    /// The end of its wait, when it had a waiting scope open, then its change of status. Nothing at all when the task is not running,
    /// as when its body ends after the run has let go of it.
    /// </returns>
    public IReadOnlyList<TaskChange> Settle(TaskOutcome ended, bool runHasStopped, DateTimeOffset now)
    {
        if (!IsRunning)
        {
            return [];
        }

        var outcome = ended.IsLeftBehind ? ended
            : _failureReported ? TaskOutcome.ReportedFailure
            : runHasStopped ? ended.OnceStopped()
            : ended;
        _failureReported = false;

        if (_openWaitingScopes == 0)
        {
            return [Move(Settled(outcome, now), now)];
        }

        _openWaitingScopes = 0;
        return [EndWaiting(now), Move(Settled(outcome, now), now)];
    }

    /// <summary>Sets the status text of the task's progress. Progress is never an event.</summary>
    public TaskChange? SetStatusText(string? text) => Change(task => task with { StatusText = text });

    /// <summary>Sets the count of the task's progress, and the total it counts towards. Progress is never an event.</summary>
    public TaskChange? SetCount(long current, long? total) => Change(task => task with { Count = current, Total = total });

    /// <summary>
    /// Adds a warning, as one line, while the task has fewer than the number kept, and produces <see cref="WarningAdded"/>.
    /// A warning beyond the number kept is only counted, and produces nothing.
    /// </summary>
    public TaskChange? Warn(string warning, DateTimeOffset now) => WhileRunning(() =>
    {
        if (Snapshot.Warnings.Count >= _kept)
        {
            Snapshot = Snapshot with { WarningsOmitted = Snapshot.WarningsOmitted + 1 };
            return new TaskChange(Snapshot, Event: null);
        }

        var kept = LineBreaks.OneLine(warning);
        Snapshot = Snapshot with { Warnings = [.. Snapshot.Warnings, kept] };
        return new TaskChange(Snapshot, new WarningAdded(now, Snapshot.Path, kept));
    });

    /// <summary>Sets the message, as one line. It is never an event.</summary>
    public TaskChange? SetMessage(string message) => Change(task => WithMessage(task, message));

    /// <summary>Adds detail lines, one for each line of each text, up to the number kept. It is never an event.</summary>
    public TaskChange? AddDetail(IEnumerable<string> texts) => Change(task => WithDetail(task, texts));

    /// <summary>
    /// Records a reported failure: sets the message and adds the detail lines, and from now on the task settles failed
    /// as a reported failure, however its body ends. It is never an event.
    /// </summary>
    public TaskChange? Fail(string message, IEnumerable<string> detail)
    {
        var change = Change(task => WithDetail(WithMessage(task, message), detail));
        _failureReported |= change is not null;
        return change;
    }

    /// <summary>
    /// Opens one waiting scope. The first open scope begins the wait, with its text and <paramref name="now"/>,
    /// and produces <see cref="WaitingChanged"/>. A scope that overlaps one already open produces nothing.
    /// </summary>
    public TaskChange? OpenWaitingScope(string? text, DateTimeOffset now) => WhileRunning(() =>
    {
        _openWaitingScopes++;
        if (_openWaitingScopes > 1)
        {
            return new TaskChange(Snapshot, Event: null);
        }

        Snapshot = Snapshot with { WaitingSince = now, WaitingText = text };
        return new TaskChange(Snapshot, new WaitingChanged(now, Snapshot.Path, Waiting: true, text, Since: now));
    });

    /// <summary>
    /// Closes one waiting scope. The last open scope ends the wait and produces <see cref="WaitingChanged"/>.
    /// Closing a scope that leaves another open produces nothing.
    /// </summary>
    public TaskChange? CloseWaitingScope(DateTimeOffset now) => WhileRunning(() =>
    {
        if (_openWaitingScopes == 0)
        {
            return null;
        }

        _openWaitingScopes--;
        return _openWaitingScopes > 0 ? new TaskChange(Snapshot, Event: null) : EndWaiting(now);
    });

    private TaskChange EndWaiting(DateTimeOffset now)
    {
        var waiting = Snapshot;
        Snapshot = Snapshot with { WaitingSince = null, WaitingText = null };
        return new TaskChange(Snapshot, new WaitingChanged(now, Snapshot.Path, Waiting: false, waiting.WaitingText, waiting.WaitingSince!.Value));
    }

    /// <summary>Applies a report that is never an event to a running task.</summary>
    private TaskChange? Change(Func<NodeSnapshot, NodeSnapshot> report) => WhileRunning(() =>
    {
        Snapshot = report(Snapshot);
        return new TaskChange(Snapshot, Event: null);
    });

    /// <summary>
    /// Carries out an operation on the task while it is running. On a task that is not running it is ignored, and gives
    /// <see langword="null"/>.
    /// </summary>
    private TaskChange? WhileRunning(Func<TaskChange?> operation) => IsRunning ? operation() : null;

    /// <summary>
    /// The task as it is once it has settled with an outcome. What it reported while it ran stays, except that an outcome
    /// with a message of its own replaces the task's message and adds its detail lines.
    /// </summary>
    private NodeSnapshot Settled(TaskOutcome outcome, DateTimeOffset now)
    {
        var settled = Snapshot with { Status = outcome.Status, Settled = now, Failure = outcome.Failure, BlockedBy = outcome.BlockedBy };

        return outcome.ReplacingMessage is null
            ? settled
            : WithDetail(WithMessage(settled, outcome.ReplacingMessage), outcome.AddedDetail);
    }

    /// <summary>Puts the task as it is after a change of status in place of the task as it was, if the status may move that way.</summary>
    /// <exception cref="InvalidOperationException">The change would move the status backwards, or out of a settled status.</exception>
    private TaskChange Move(NodeSnapshot changed, DateTimeOffset now)
    {
        var from = Status;
        var to = changed.Status;
        if (!from.CanMoveTo(to))
        {
            throw new InvalidOperationException($"The task '{Snapshot.Path}' cannot move from {from} to {to}: a status only moves forward.");
        }

        Snapshot = changed with { Counts = StatusCounts.OfOneTask(to) };
        return new TaskChange(Snapshot, new TaskStatusChanged(now, Snapshot, from, to));
    }

    private static NodeSnapshot WithMessage(NodeSnapshot task, string message) => task with { Message = LineBreaks.OneLine(message) };

    private static NodeSnapshot WithDetail(NodeSnapshot task, IEnumerable<string> texts)
    {
        string[] lines = [.. texts.SelectMany(LineBreaks.Lines)];
        var kept = Math.Min(lines.Length, _kept - task.Detail.Count);

        return task with
        {
            Detail = [.. task.Detail, .. lines.Take(kept)],
            DetailOmitted = task.DetailOmitted + lines.Length - kept,
        };
    }
}
