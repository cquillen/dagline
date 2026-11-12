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
/// such as one the run has let go of, each is ignored and gives <see langword="null"/>. A change of checkpoint on such a task throws instead.
/// </para>
/// <para>
/// It holds the task's data itself, and makes a <see cref="NodeSnapshot"/> of it for each change without keeping one.
/// An instance lives as long as its run, and one that held on to its latest snapshot would point from a long-lived object
/// at a new one with every change, which makes the garbage collector collect an older generation far more often.
/// </para>
/// <para>It is not thread-safe: the run calls it under its lock.</para>
/// </remarks>
/// <param name="task">The task this is an instance of.</param>
/// <param name="checkpoint">The checkpoint the journal hands back to the task, or empty for none.</param>
internal sealed class TaskInstance(BuiltTask task, string checkpoint = "")
{
    /// <summary>How many warnings, and separately how many detail lines, are kept for one task. The rest are counted.</summary>
    private const int _kept = 100;

    private bool _failureReported;
    private int _openWaitingScopes;

    private FailureKind? _failure;
    private NodePath? _blockedBy;
    private string? _message;
    private IReadOnlyList<string> _detail = [];
    private int _detailOmitted;
    private IReadOnlyList<string> _warnings = [];
    private int _warningsOmitted;
    private string? _statusText;
    private long? _count;
    private long? _total;
    private DateTimeOffset? _waitingSince;
    private string? _waitingText;
    private DateTimeOffset? _started;
    private DateTimeOffset? _settled;
    private bool _isEarlierSuccess;

    public NodeStatus Status { get; private set; } = NodeStatus.Pending;

    /// <summary>
    /// The task's checkpoint: what the journal handed back, or what the task last saved in this run. Empty when there is none.
    /// It is never part of a snapshot.
    /// </summary>
    public string Checkpoint { get; private set; } = checkpoint;

    /// <summary>
    /// The instance of a task that begins the run as an earlier success: succeeded, with the outcome and settle time an earlier run
    /// recorded, and no start time. Its status never changes, so it makes no change and no event in this run.
    /// </summary>
    public static TaskInstance EarlierSuccess(BuiltTask task, TaskSettledEntry recorded) => new(task)
    {
        Status = NodeStatus.Succeeded,
        _isEarlierSuccess = true,
        _message = recorded.Message,
        _detail = recorded.Detail,
        _detailOmitted = recorded.DetailOmitted,
        _warnings = recorded.Warnings,
        _warningsOmitted = recorded.WarningsOmitted,
        _settled = recorded.Time,
    };

    // Each report checks this itself: a helper that took the report as a lambda cost every report a closure.
    private bool IsRunning => Status is NodeStatus.Running;

    /// <summary>The task as a snapshot shows it now. Each call makes a new one.</summary>
    public NodeSnapshot ToNodeSnapshot() => new(task, Status, StatusCounts.OfOneTask(Status))
    {
        Failure = _failure,
        BlockedBy = _blockedBy,
        Message = _message,
        Detail = _detail,
        DetailOmitted = _detailOmitted,
        Warnings = _warnings,
        WarningsOmitted = _warningsOmitted,
        StatusText = _statusText,
        Count = _count,
        Total = _total,
        WaitingSince = _waitingSince,
        WaitingText = _waitingText,
        Started = _started,
        Settled = _settled,
        IsEarlierSuccess = _isEarlierSuccess,
        EarlierSuccesses = _isEarlierSuccess ? 1 : 0,
    };

    /// <summary>Moves the task from pending to ready: every dependency is met.</summary>
    public TaskChange MakeReady(DateTimeOffset now) => Moved(from: MoveTo(NodeStatus.Ready), now);

    /// <summary>Moves the task from ready to running, as of <paramref name="now"/>.</summary>
    public TaskChange Start(DateTimeOffset now)
    {
        var from = MoveTo(NodeStatus.Running);
        _started = now;

        return Moved(from, now);
    }

    /// <summary>Settles a pending task as blocked by the failed task at <paramref name="cause"/>.</summary>
    public TaskChange Block(NodePath cause, DateTimeOffset now) => SettleWith(TaskOutcome.Blocked(cause), now);

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
            return [SettleWith(outcome, now)];
        }

        _openWaitingScopes = 0;
        return [EndWaiting(now), SettleWith(outcome, now)];
    }

    /// <summary>Sets the status text of the task's progress. Progress is never an event.</summary>
    public TaskChange? SetStatusText(string? text)
    {
        if (!IsRunning)
        {
            return null;
        }

        _statusText = text;

        return Changed();
    }

    /// <summary>Sets the count of the task's progress, and the total it counts towards. Progress is never an event.</summary>
    public TaskChange? SetCount(long current, long? total)
    {
        if (!IsRunning)
        {
            return null;
        }

        _count = current;
        _total = total;

        return Changed();
    }

    /// <summary>
    /// Adds a warning, as one line, while the task has fewer than the number kept, and produces <see cref="WarningAdded"/>.
    /// A warning beyond the number kept is only counted, and produces nothing.
    /// </summary>
    public TaskChange? Warn(string warning, DateTimeOffset now)
    {
        if (!IsRunning)
        {
            return null;
        }

        if (_warnings.Count >= _kept)
        {
            _warningsOmitted++;

            return Changed();
        }

        var kept = LineBreaks.OneLine(warning);
        _warnings = [.. _warnings, kept];

        return new TaskChange(ToNodeSnapshot(), new WarningAdded(now, task.Path, kept));
    }

    /// <summary>Sets the message, as one line. It is never an event.</summary>
    public TaskChange? SetMessage(string message)
    {
        if (!IsRunning)
        {
            return null;
        }

        ReplaceMessage(message);

        return Changed();
    }

    /// <summary>Adds detail lines, one for each line of each text, up to the number kept. It is never an event.</summary>
    public TaskChange? AddDetail(IEnumerable<string> texts)
    {
        if (!IsRunning)
        {
            return null;
        }

        AddDetailLines(texts);

        return Changed();
    }

    /// <summary>
    /// Records a reported failure: sets the message and adds the detail lines, and from now on the task settles failed
    /// as a reported failure, however its body ends. It is never an event.
    /// </summary>
    public TaskChange? Fail(string message, IEnumerable<string> detail)
    {
        if (!IsRunning)
        {
            return null;
        }

        ReplaceMessage(message);
        AddDetailLines(detail);
        _failureReported = true;

        return Changed();
    }

    /// <summary>
    /// Opens one waiting scope. The first open scope begins the wait, with its text and <paramref name="now"/>,
    /// and produces <see cref="WaitingChanged"/>. A scope that overlaps one already open produces nothing.
    /// </summary>
    public TaskChange? OpenWaitingScope(string? text, DateTimeOffset now)
    {
        if (!IsRunning)
        {
            return null;
        }

        _openWaitingScopes++;
        if (_openWaitingScopes > 1)
        {
            return Changed();
        }

        _waitingSince = now;
        _waitingText = text;

        return new TaskChange(ToNodeSnapshot(), new WaitingChanged(now, task.Path, Waiting: true, text, Since: now));
    }

    /// <summary>
    /// Closes one waiting scope. The last open scope ends the wait and produces <see cref="WaitingChanged"/>.
    /// Closing a scope that leaves another open produces nothing.
    /// </summary>
    public TaskChange? CloseWaitingScope(DateTimeOffset now)
    {
        if (!IsRunning || _openWaitingScopes == 0)
        {
            return null;
        }

        _openWaitingScopes--;

        return _openWaitingScopes > 0 ? Changed() : EndWaiting(now);
    }

    /// <summary>
    /// Replaces the task's checkpoint with <paramref name="text"/>, which clears it when empty. Unlike a report, it throws on a task
    /// that is not running, since a caller must never believe a checkpoint was kept when it was not.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when nothing changed because the task cleared a checkpoint it did not have; <see langword="true"/> otherwise,
    /// a save of the text it already had included.
    /// </returns>
    /// <exception cref="InvalidOperationException">The task is not running: it has settled, or the run has let go of it.</exception>
    public bool ChangeCheckpoint(string text)
    {
        if (!IsRunning)
        {
            throw new InvalidOperationException(
                $"The task '{task.Path}' cannot change its checkpoint: it is {NameText.Of(Status)}, "
                + "so the run no longer keeps anything it does. A task changes its checkpoint only while it is running.");
        }

        if (text.Length == 0 && Checkpoint.Length == 0)
        {
            return false;
        }

        Checkpoint = text;
        return true;
    }

    private TaskChange EndWaiting(DateTimeOffset now)
    {
        var since = _waitingSince!.Value;
        var text = _waitingText;
        _waitingSince = null;
        _waitingText = null;

        return new TaskChange(ToNodeSnapshot(), new WaitingChanged(now, task.Path, Waiting: false, text, since));
    }

    /// <summary>The task as it is after a report that is never an event.</summary>
    private TaskChange Changed() => new(ToNodeSnapshot(), Event: null);

    /// <summary>
    /// Settles the task with an outcome. What it reported while it ran stays, except that an outcome
    /// with a message of its own replaces the task's message and adds its detail lines.
    /// </summary>
    private TaskChange SettleWith(TaskOutcome outcome, DateTimeOffset now)
    {
        var from = MoveTo(outcome.Status);
        _settled = now;
        _failure = outcome.Failure;
        _blockedBy = outcome.BlockedBy;
        if (outcome.ReplacingMessage is not null)
        {
            ReplaceMessage(outcome.ReplacingMessage);
            AddDetailLines(outcome.AddedDetail);
        }

        return Moved(from, now);
    }

    /// <summary>Moves the task to a status, if the status may move that way.</summary>
    /// <returns>The status it moved from.</returns>
    /// <exception cref="InvalidOperationException">The change would move the status backwards, or out of a settled status.</exception>
    private NodeStatus MoveTo(NodeStatus to)
    {
        var from = Status;
        if (!from.CanMoveTo(to))
        {
            throw new InvalidOperationException($"The task '{task.Path}' cannot move from {from} to {to}: a status only moves forward.");
        }

        Status = to;

        return from;
    }

    /// <summary>The task as it is after a change of status, with the event that says so.</summary>
    private TaskChange Moved(NodeStatus from, DateTimeOffset now)
    {
        var moved = ToNodeSnapshot();

        return new TaskChange(moved, new TaskStatusChanged(now, moved, from, Status));
    }

    private void ReplaceMessage(string message) => _message = LineBreaks.OneLine(message);

    private void AddDetailLines(IEnumerable<string> texts)
    {
        string[] lines = [.. texts.SelectMany(LineBreaks.Lines)];
        var kept = Math.Min(lines.Length, _kept - _detail.Count);

        _detail = [.. _detail, .. lines.Take(kept)];
        _detailOmitted += lines.Length - kept;
    }
}
