namespace Dagline;

/// <summary>One node of a run at one instant. Immutable.</summary>
public sealed record NodeSnapshot
{
    internal NodeSnapshot(BuiltNode node, NodeStatus status, TaskCounts counts)
    {
        Path = node.Path;
        Title = node.Title;
        IsGroup = node is BuiltGroup;
        Dependencies = node.Dependencies;
        Limits = node.Limits;
        Status = status;
        Counts = counts;
    }

    /// <summary>The identity of the node.</summary>
    public NodePath Path { get; }

    /// <summary>
    /// The readable name the graph gave the node, or <see langword="null"/> when it gave none.
    /// It is separate from the node's segment and never part of <see cref="Path"/>.
    /// </summary>
    public string? Title { get; }

    /// <summary><see langword="true"/> for a group and <see langword="false"/> for a task.</summary>
    public bool IsGroup { get; }

    /// <summary>
    /// The dependencies declared on this node itself, one for each upstream, in the order each upstream was first named.
    /// An upstream that was given both kinds is listed once, as <see cref="DependencyKind.Requires"/>.
    /// </summary>
    /// <remarks>
    /// A dependency declared on a group is listed on that group and not on the nodes beneath it,
    /// although it applies to every task beneath the group.
    /// </remarks>
    public IReadOnlyList<Dependency> Dependencies { get; }

    /// <summary>
    /// The limits named on this node itself, each once, in the order first named.
    /// </summary>
    /// <remarks>
    /// A limit named on a group is listed on that group and not on the nodes beneath it,
    /// although every task beneath the group holds a slot of it while it runs.
    /// <see cref="Snapshot.Limits"/> gives the size of each limit and how many of its slots are in use.
    /// </remarks>
    public IReadOnlyList<string> Limits { get; }

    /// <summary>
    /// Where the node is in its life. A task has the status the run gave it. A group's status is derived from the tasks
    /// beneath it at any depth.
    /// </summary>
    /// <remarks>
    /// While any task beneath a group has not settled, the group is running if any of them is running, else ready if any is ready,
    /// else pending. Once all have settled it is failed if any failed, else cancelled if any was cancelled,
    /// else blocked if any is blocked, else succeeded. A group with no tasks beneath it is succeeded from the start of the run.
    /// </remarks>
    public NodeStatus Status { get; internal init; }

    /// <summary>
    /// How many tasks have each status, with their sums and sub-counts: for a group, the tasks beneath it at any depth,
    /// and for a task, the task itself.
    /// </summary>
    /// <remarks>
    /// A task's counts are those of one task: one for its status and zero for every other, with <see cref="TaskCounts.Waiting"/>
    /// one while it <see cref="IsWaiting"/> and <see cref="TaskCounts.EarlierSuccesses"/> one for an earlier success.
    /// A group's are kept up to date as each task beneath it changes status and begins or ends waiting, so a snapshot shows
    /// them as of its own instant.
    /// </remarks>
    public TaskCounts Counts { get; internal init; }

    /// <summary>
    /// <see langword="true"/> for an earlier success: a task that an earlier run of the journal recorded as succeeded, which this run
    /// kept and does not run, and a group with at least one task beneath it, all of them earlier successes.
    /// </summary>
    /// <remarks>
    /// An earlier success is succeeded from the start of the run, with the message, detail lines and warnings it succeeded with and
    /// the time it succeeded in <see cref="Settled"/>, and no <see cref="Started"/>. It never changes status in this run, and no event
    /// is published for it. A group made only of earlier successes is settled from the start and gets no <see cref="GroupSettled"/>.
    /// It is the same in every snapshot of the run. <see cref="TaskCounts.EarlierSuccesses"/> of <see cref="Counts"/> says how many
    /// earlier successes a group holds when only some of the tasks beneath it are.
    /// </remarks>
    public bool IsEarlierSuccess => Counts.EarlierSuccesses > 0 && Counts.EarlierSuccesses == Counts.Total;

    /// <summary>
    /// Why the task failed, or <see langword="null"/> for a task that has not failed and for a group.
    /// </summary>
    /// <remarks>
    /// A task that called <see cref="TaskContext.Fail"/> fails as <see cref="FailureKind.Reported"/>, however its body ended.
    /// Any other task whose body let an exception escape, a cancellation exception included, fails as
    /// <see cref="FailureKind.Unexpected"/>. The kind is set when the task settles: it is <see langword="null"/> while the task
    /// is running, even after it has called <see cref="TaskContext.Fail"/>.
    /// </remarks>
    public FailureKind? Failure { get; internal init; }

    /// <summary>
    /// The path of the failed task that blocked this task, or <see langword="null"/> for a task that is not blocked and for a group.
    /// </summary>
    /// <remarks>
    /// It is always a failed task, never a blocked one: a task blocked through a chain of blocked tasks names the one failure
    /// that started the cascade. It is the first failure to reach the task, and a later failure of another upstream does not change it.
    /// </remarks>
    public NodePath? BlockedBy { get; internal init; }

    /// <summary>
    /// The one-line message of the task's outcome, or <see langword="null"/> when it has none and for a group.
    /// </summary>
    /// <remarks>
    /// It is the message the task last set with <see cref="TaskContext.Message"/> or <see cref="TaskContext.Fail"/>,
    /// and shows from the moment it is set, while the task is still running. For an unexpected failure it is the message
    /// of the exception that escaped the task body, which replaces any the task had set. When the exception's
    /// <see cref="Exception.Message"/> itself throws or gives <see langword="null"/>, it is the full name of the exception's type.
    /// Each line break in the text given is replaced by a space, and empty text is no message: setting an empty message clears
    /// the one set before, and a failure reported with an empty message, or an exception whose message is empty, leaves none.
    /// An earlier success has the message an earlier run recorded, as it was recorded.
    /// </remarks>
    public string? Message { get; internal init; }

    /// <summary>
    /// The detail lines of the task's outcome, in the order added. Empty when it has none and for a group.
    /// </summary>
    /// <remarks>
    /// They are the lines the task added with <see cref="TaskContext.Detail"/> and <see cref="TaskContext.Fail"/>,
    /// each shown from the moment it is added. An unexpected failure adds, after any lines the task had already added,
    /// the full name of the exception's type, then each line of its stack trace as the runtime wrote it.
    /// The stack trace is left out when the exception has none or when its <see cref="Exception.StackTrace"/> itself throws.
    /// Only the first 100 detail lines of a task are kept, whichever way they were added:
    /// <see cref="DetailOmitted"/> counts the rest.
    /// </remarks>
    public IReadOnlyList<string> Detail { get; internal init; } = [];

    /// <summary>
    /// How many detail lines were added after the 100 that <see cref="Detail"/> keeps. Zero for a group.
    /// </summary>
    public int DetailOmitted { get; internal init; }

    /// <summary>
    /// The warnings the task added with <see cref="TaskContext.Warn"/>, in the order added, each on one line.
    /// Empty when it added none and for a group.
    /// </summary>
    /// <remarks>
    /// A warning shows here from the moment it is added, while the task is still running, and stays whatever status the task
    /// settles with. Only the first 100 warnings of a task are kept: <see cref="WarningsOmitted"/> counts the rest.
    /// </remarks>
    public IReadOnlyList<string> Warnings { get; internal init; } = [];

    /// <summary>
    /// How many warnings the task added after the 100 that <see cref="Warnings"/> keeps. Zero for a group.
    /// </summary>
    public int WarningsOmitted { get; internal init; }

    /// <summary>
    /// How many warnings the node holds. For a task, every warning it was given: those kept in <see cref="Warnings"/> and those
    /// only counted in <see cref="WarningsOmitted"/>. For a group, the sum of that number for the tasks beneath it at any depth.
    /// </summary>
    /// <remarks>
    /// It counts every call of <see cref="TaskContext.Warn"/> a task made while it ran, and for an earlier success the warnings
    /// it succeeded with. A group's number is kept up to date as each task beneath it is given a warning, one beyond the number
    /// kept included, so a snapshot shows it as of its own instant. A group lists no warnings of its own: read them from the tasks
    /// beneath it. <see cref="GroupSettled"/> does not carry this number.
    /// </remarks>
    public int WarningCount => IsGroup ? WarningsBeneath : Warnings.Count + WarningsOmitted;

    /// <summary>For a group, how many warnings the tasks beneath it hold, as <see cref="WarningCount"/> gives it.</summary>
    internal int WarningsBeneath { get; init; }

    /// <summary>
    /// The status text of the task's progress: the line the task last set with <see cref="TaskContext.Status"/>.
    /// <see langword="null"/> when the task has set none or has cleared it, with <see langword="null"/> or empty text, and for a group.
    /// </summary>
    /// <remarks>
    /// Progress is never an event: read it from a snapshot. It is frozen when the task settles, so a settled task
    /// still shows how far it got. Unlike <see cref="Message"/> and <see cref="WaitingText"/>, it is kept as the task gave it,
    /// line breaks included: a reporter that shows it on one line replaces each line break itself.
    /// </remarks>
    public string? StatusText { get; internal init; }

    /// <summary>
    /// The count of the task's progress: the number the task last gave <see cref="TaskContext.Count"/>.
    /// <see langword="null"/> when the task has given none, and for a group.
    /// </summary>
    /// <remarks>Like <see cref="StatusText"/>, it is never an event and is frozen when the task settles.</remarks>
    public long? Count { get; internal init; }

    /// <summary>
    /// The total that <see cref="Count"/> is counting towards, as the task last gave it to <see cref="TaskContext.Count"/>.
    /// <see langword="null"/> when the last count came with no total, when the task has given no count, and for a group.
    /// </summary>
    /// <remarks>
    /// Dagline does not compare the two numbers, so the count can be above the total.
    /// </remarks>
    public long? Total { get; internal init; }

    /// <summary>
    /// When the task's current wait began, read from the run's <see cref="RunOptions.TimeProvider"/>,
    /// or <see langword="null"/> while the task is not waiting, and for a group.
    /// </summary>
    /// <remarks>
    /// A task waits from the moment its first <see cref="TaskContext.Waiting"/> scope opens until its last open scope closes.
    /// Waiting is a marker on a running task and never a status. It is live only: it is cleared when the wait ends, and a task
    /// that has settled is never waiting. <see cref="WaitingChanged"/> announces each begin and end.
    /// </remarks>
    public DateTimeOffset? WaitingSince { get; internal init; }

    /// <summary>
    /// What the task's current wait is for, as given to the scope that began it, or <see langword="null"/> when that scope gave no text
    /// or empty text, while the task is not waiting, and for a group.
    /// </summary>
    /// <remarks>
    /// Each line break in the text given is replaced by a space, so it is one line. While scopes overlap it stays the text of the one that opened first. Like <see cref="WaitingSince"/>, it is cleared when the wait ends.
    /// </remarks>
    public string? WaitingText { get; internal init; }

    /// <summary>
    /// <see langword="true"/> for a task that is running and waiting: one with a <see cref="TaskContext.Waiting"/> scope open.
    /// For a group, <see langword="true"/> when it has running tasks beneath it and every one of them is waiting.
    /// </summary>
    /// <remarks>
    /// A running task is either waiting or working, never both: <see cref="IsWorking"/> is the other half. Waiting is a marker
    /// on a running task, never a status. A group with no running task beneath it is neither waiting nor working.
    /// Both answers come from <see cref="Counts"/>: <see cref="TaskCounts.Waiting"/> and <see cref="TaskCounts.Working"/> say how many.
    /// </remarks>
    public bool IsWaiting => Counts.Waiting > 0 && Counts.Working == 0;

    /// <summary>
    /// <see langword="true"/> for a node whose status is running and that is not waiting, as <see cref="IsWaiting"/> says:
    /// a task with no waiting scope open, or a group with at least one working task beneath it.
    /// </summary>
    /// <remarks><see cref="IsWaiting"/> and <see cref="IsWorking"/> never both hold.</remarks>
    public bool IsWorking => Status is NodeStatus.Running && !IsWaiting;

    /// <summary>
    /// When the task started running, read from the run's <see cref="RunOptions.TimeProvider"/>,
    /// or <see langword="null"/> while it has not started. A blocked task never starts, so it has no start time.
    /// </summary>
    public DateTimeOffset? Started { get; internal init; }

    /// <summary>
    /// When the task settled, read from the run's <see cref="RunOptions.TimeProvider"/>,
    /// or <see langword="null"/> while it has not settled. A blocked task settles at the time the task that blocked it failed.
    /// </summary>
    public DateTimeOffset? Settled { get; internal init; }

    /// <summary>
    /// How long the task ran: <see cref="Settled"/> less <see cref="Started"/>. <see langword="null"/> unless the task has both,
    /// so for a task that has not settled, for a blocked task, for an earlier success and for a group.
    /// </summary>
    /// <remarks>
    /// Both times are read from the run's <see cref="RunOptions.TimeProvider"/>. A task the run let go of after a stop ran
    /// until the run let go of it, which is when it settled as cancelled.
    /// </remarks>
    public TimeSpan? Ran => Settled - Started;
}
