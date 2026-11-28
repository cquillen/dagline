namespace Dagline.Prototype;

/// <summary>
/// One task during one run, on the loop's side. Adapted from Dagline's TaskInstance: progress and the counts beyond those kept are
/// no longer reports applied here but a sample of the task's <see cref="TaskSlot"/>, taken by <see cref="Sample"/>.
/// </summary>
internal sealed class ProtoTask(BuiltTask task, string checkpoint = "")
{
    private bool _failureReported;
    private int _openWaitingScopes;

    private FailureKind? _failure;
    private NodePath? _blockedBy;
    private string? _message;
    private IReadOnlyList<string> _detail = [];
    private int _detailOmittedAtSettle;
    private IReadOnlyList<string> _warnings = [];
    private SlotSample _sample;
    private DateTimeOffset? _waitingSince;
    private string? _waitingText;
    private DateTimeOffset? _started;
    private DateTimeOffset? _settled;

    public BuiltTask Task => task;

    public NodeStatus Status { get; private set; } = NodeStatus.Pending;

    public string Checkpoint { get; set; } = checkpoint;

    /// <summary>The task's side of the run, made when the task starts.</summary>
    public TaskSlot? Slot { get; private set; }

    private bool IsRunning => Status is NodeStatus.Running;

    // A count beyond those kept is sampled, and can be read before the kept ones, which are inputs, have all been applied.
    // Shown only once the kept are all here, so a snapshot never says "omitted" while fewer than the kept are listed.
    private int WarningsOmitted => _warnings.Count >= TaskSlot.Kept ? _sample.WarningsBeyondKept : 0;

    private int DetailOmitted => _detailOmittedAtSettle + (_detail.Count >= TaskSlot.Kept ? _sample.DetailBeyondKept : 0);

    public int WarningCount => _warnings.Count + WarningsOmitted;

    public NodeSnapshot ToNodeSnapshot() => new(task, Status, TaskCounts.OfOneTask(Status, waiting: _waitingSince is not null, earlierSuccess: false))
    {
        Failure = _failure,
        BlockedBy = _blockedBy,
        Message = _message,
        Detail = _detail,
        DetailOmitted = DetailOmitted,
        Warnings = _warnings,
        WarningsOmitted = WarningsOmitted,
        StatusText = _sample.StatusText,
        Count = _sample.Count,
        Total = _sample.Total,
        WaitingSince = _waitingSince,
        WaitingText = _waitingText,
        Started = _started,
        Settled = _settled,
    };

    /// <summary>
    /// Reads the task's slot. With <paramref name="freeze"/>, the slot takes no report after this read, so the sample taken as the
    /// task settles is the progress every later reader sees.
    /// </summary>
    /// <returns>Whether anything the snapshot shows changed.</returns>
    public bool Sample(bool freeze = false) => Slot is { } slot && slot.Read(ref _sample, freeze);

    public TaskChange MakeReady(DateTimeOffset now) => Moved(from: MoveTo(NodeStatus.Ready), now);

    public TaskChange Start(DateTimeOffset now)
    {
        var from = MoveTo(NodeStatus.Running);
        _started = now;
        Slot = new TaskSlot();

        return Moved(from, now);
    }

    public TaskChange Block(NodePath cause, DateTimeOffset now) => SettleWith(TaskOutcome.Blocked(cause), now);

    public IReadOnlyList<TaskChange> Settle(TaskOutcome ended, DateTimeOffset now)
        => SettleRunning(_failureReported ? TaskOutcome.ReportedFailure : ended, now);

    public IReadOnlyList<TaskChange> LeaveBehind(DateTimeOffset now) => SettleRunning(TaskOutcome.LeftBehind, now);

    private IReadOnlyList<TaskChange> SettleRunning(TaskOutcome outcome, DateTimeOffset now)
    {
        if (!IsRunning)
        {
            return [];
        }

        Sample(freeze: true);
        _failureReported = false;

        if (_openWaitingScopes == 0)
        {
            return [SettleWith(outcome, now)];
        }

        _openWaitingScopes = 0;
        return [EndWaiting(now), SettleWith(outcome, now)];
    }

    /// <summary>Adds a kept warning. The task's side decided it is one of those kept, and counts the rest in its slot.</summary>
    public TaskChange? Warn(string warning, DateTimeOffset now)
    {
        if (!IsRunning)
        {
            return null;
        }

        var kept = LineBreaks.OneLine(warning);
        _warnings = [.. _warnings, kept];

        return new TaskChange(Task: null, new WarningAdded(now, task.Path, kept));
    }

    public TaskChange? SetMessage(string message)
    {
        if (!IsRunning)
        {
            return null;
        }

        _message = LineBreaks.OneLineOrNone(message);

        return Changed();
    }

    /// <summary>Adds kept detail lines, already split into lines by the task's side.</summary>
    public TaskChange? AddDetail(string[] keptLines)
    {
        if (!IsRunning)
        {
            return null;
        }

        _detail = [.. _detail, .. keptLines];

        return Changed();
    }

    public TaskChange? Fail(string message, string[] keptLines)
    {
        if (!IsRunning)
        {
            return null;
        }

        _message = LineBreaks.OneLineOrNone(message);
        _detail = [.. _detail, .. keptLines];
        _failureReported = true;

        return Changed();
    }

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
        _waitingText = LineBreaks.OneLineOrNone(text);

        return new TaskChange(Task: null, new WaitingChanged(now, task.Path, waiting: true, _waitingText, since: now));
    }

    public TaskChange? CloseWaitingScope(DateTimeOffset now)
    {
        if (!IsRunning || _openWaitingScopes == 0)
        {
            return null;
        }

        _openWaitingScopes--;

        return _openWaitingScopes > 0 ? Changed() : EndWaiting(now);
    }

    private TaskChange EndWaiting(DateTimeOffset now)
    {
        var since = _waitingSince!.Value;
        var text = _waitingText;
        _waitingSince = null;
        _waitingText = null;

        return new TaskChange(ToNodeSnapshot(), new WaitingChanged(now, task.Path, waiting: false, text, since));
    }

    private static TaskChange Changed() => new(Task: null, Event: null);

    private TaskChange SettleWith(TaskOutcome outcome, DateTimeOffset now)
    {
        var from = MoveTo(outcome.Status);
        _settled = now;
        _failure = outcome.Failure;
        _blockedBy = outcome.BlockedBy;
        if (outcome.ReplacingMessage is not null)
        {
            _message = LineBreaks.OneLineOrNone(outcome.ReplacingMessage);
            string[] lines = [.. outcome.AddedDetail.SelectMany(LineBreaks.Lines)];
            var kept = Math.Min(lines.Length, Math.Max(0, TaskSlot.Kept - _detail.Count));
            _detail = [.. _detail, .. lines.Take(kept)];
            _detailOmittedAtSettle += lines.Length - kept;
        }

        return Moved(from, now);
    }

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

    private TaskChange Moved(NodeStatus from, DateTimeOffset now)
    {
        var moved = ToNodeSnapshot();

        return new TaskChange(moved, new TaskStatusChanged(now, moved, from, Status));
    }
}
