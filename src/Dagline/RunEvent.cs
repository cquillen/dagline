namespace Dagline;

/// <summary>
/// A record of one thing that happened in a run, with the time it happened.
/// Every reporter receives a run's events in the same order, causes before effects.
/// </summary>
/// <remarks>
/// <para>
/// The set of event kinds is closed: a type outside Dagline cannot add one.
/// </para>
/// <para>
/// A change and everything that follows from it at once are one step, and the events of a step come in a fixed order,
/// all with the same <see cref="At"/>:
/// </para>
/// <list type="number">
/// <item><description>The change itself: the run starting, a task settling, or a stop being applied.</description></item>
/// <item><description>Every task that this blocks, pending to blocked, in declaration order.</description></item>
/// <item><description>Every task whose dependencies are now all met, pending to ready, in declaration order.</description></item>
/// <item><description>Every group that is now settled, the deepest first, then in declaration order.</description></item>
/// <item><description>Every task that now starts, ready to running, in declaration order.</description></item>
/// </list>
/// <para>
/// The events of one step are never mixed with those of another. A <see cref="WarningAdded"/> is a step of its own, and so is
/// a <see cref="StopRequested"/>, and so is a <see cref="WaitingChanged"/>, except the end of a wait that a task settling forces,
/// which comes just before that task's change. Once a stop has been applied, no step starts a task.
/// </para>
/// </remarks>
public abstract record RunEvent
{
    private protected RunEvent(DateTimeOffset at)
    {
        At = at;
    }

    /// <summary>When it happened, read from the run's <see cref="RunOptions.TimeProvider"/>.</summary>
    public DateTimeOffset At { get; }
}

/// <summary>The run started. This is the first event of every run.</summary>
public sealed record RunStarted : RunEvent
{
    internal RunStarted(DateTimeOffset at, ISnapshotSource source)
        : base(at)
    {
        Source = source;
    }

    /// <summary>A live source of snapshots of this run, which a reporter may keep and read at any time.</summary>
    public ISnapshotSource Source { get; }
}

/// <summary>
/// A task changed status. One is published for every change, pending to ready and pending to blocked included.
/// </summary>
public sealed record TaskStatusChanged : RunEvent
{
    internal TaskStatusChanged(DateTimeOffset at, NodeSnapshot node, NodeStatus from, NodeStatus to)
        : base(at)
    {
        Node = node;
        From = from;
        To = to;
    }

    /// <summary>The task as it is after the change. When the task has settled, this carries its whole outcome.</summary>
    public NodeSnapshot Node { get; }

    /// <summary>The status before the change.</summary>
    public NodeStatus From { get; }

    /// <summary>The status after the change.</summary>
    public NodeStatus To { get; }
}

/// <summary>
/// A running task added a warning to its outcome with <see cref="TaskContext.AddWarning"/>, and Dagline kept it.
/// </summary>
/// <remarks>
/// <para>
/// A warning is a step of its own: the event is never mixed with those of another step, and a warning a body adds
/// before it ends precedes the <see cref="TaskStatusChanged"/> of that task settling. The snapshot handed with the event
/// lists the warning in the task's <see cref="NodeSnapshot.Warnings"/>.
/// </para>
/// <para>
/// Dagline keeps the first 100 warnings of a task. A warning past that limit is counted in
/// <see cref="NodeSnapshot.WarningsOmitted"/> and gets no event, so one task never publishes more than 100 of these.
/// </para>
/// </remarks>
public sealed record WarningAdded : RunEvent
{
    internal WarningAdded(DateTimeOffset at, NodePath path, string warning)
        : base(at)
    {
        Path = path;
        Warning = warning;
    }

    /// <summary>The path of the task that added the warning.</summary>
    public NodePath Path { get; }

    /// <summary>The warning, on one line: each line break in the text the task gave is replaced by a space.</summary>
    public string Warning { get; }
}

/// <summary>
/// A running task began or stopped waiting: its waiting marker, opened with <see cref="TaskContext.BeginWaiting"/>, began or ended.
/// </summary>
/// <remarks>
/// <para>
/// The event follows the marker, not the scopes. It is published when the task's first open scope opens and when its last
/// open scope closes, so scopes that overlap give one begin and one end, with the text and the time of the scope that opened first.
/// Every begin has exactly one end, and a task may wait several times in one run, each wait with its own pair.
/// </para>
/// <para>
/// A change of the marker is a step of its own, like <see cref="WarningAdded"/>. When a task settles with a scope still open,
/// the end is published immediately before the <see cref="TaskStatusChanged"/> of that task settling, in the same step,
/// so a settled task is never waiting. The snapshot handed with a begin shows the marker on the task in
/// <see cref="NodeSnapshot.WaitingSince"/> and <see cref="NodeSnapshot.WaitingText"/>, and the one handed with an end shows neither.
/// </para>
/// <para>
/// Waiting is a marker and never a status: the task is running throughout, and keeps its slots.
/// </para>
/// </remarks>
public sealed record WaitingChanged : RunEvent
{
    internal WaitingChanged(DateTimeOffset at, NodePath path, bool waiting, string? text, DateTimeOffset since)
        : base(at)
    {
        Path = path;
        Waiting = waiting;
        Text = text;
        Since = since;
    }

    /// <summary>The path of the task.</summary>
    public NodePath Path { get; }

    /// <summary><see langword="true"/> when the wait began, <see langword="false"/> when it ended.</summary>
    public bool Waiting { get; }

    /// <summary>
    /// What the task is waiting for, as given to the scope that began the wait, on one line: each line break is a space.
    /// <see langword="null"/> when that scope gave no text or empty text.
    /// </summary>
    public string? Text { get; }

    /// <summary>When the wait began. On a begin it equals <see cref="RunEvent.At"/>; on an end it is the begin's time.</summary>
    public DateTimeOffset Since { get; }
}

/// <summary>
/// A group settled: the last task beneath it, at any depth, settled.
/// </summary>
/// <remarks>
/// <para>
/// The event comes after the last <see cref="TaskStatusChanged"/> of every task beneath the group. Within the step in which
/// a task settles, it follows the tasks that were blocked and those that became ready, and precedes the tasks that start.
/// When several groups settle in one step, the deepest comes first, then declaration order, so a group's event follows those
/// of the groups beneath it. One failure can settle many groups in one step, since every task it blocks settles with it.
/// </para>
/// <para>
/// A group that is settled from the start of the run, because no task is beneath it or every task beneath it is an earlier success,
/// gets no event.
/// A group's status before it settles is never an event: read it from a snapshot.
/// </para>
/// <para>Its <see cref="RunEvent.At"/> is when the group settled: the time its last task settled.</para>
/// </remarks>
public sealed record GroupSettled : RunEvent
{
    internal GroupSettled(DateTimeOffset at, NodePath path, NodeStatus status, TaskCounts counts)
        : base(at)
    {
        Path = path;
        Status = status;
        Counts = counts;
    }

    /// <summary>The path of the group.</summary>
    public NodePath Path { get; }

    /// <summary>
    /// The status the group settled with, derived from the tasks beneath it: failed if any failed, else cancelled if any was cancelled,
    /// else blocked if any is blocked, else succeeded.
    /// </summary>
    public NodeStatus Status { get; }

    /// <summary>
    /// How many tasks beneath the group, at any depth, have each status, with their sums and sub-counts, as the group's
    /// <see cref="NodeSnapshot.Counts"/> gives them in the snapshot handed with this event.
    /// <see cref="TaskCounts.EarlierSuccesses"/> counts the earlier successes among the succeeded, so that a reporter can show
    /// how much this run did, and none of the tasks is running, so none is waiting.
    /// </summary>
    /// <remarks>
    /// A group made only of earlier successes is settled from the start of the run and gets no event, so a group that settles in a
    /// run always has at least one task beneath it that settled in that run. The event does not carry the warnings beneath the group:
    /// read the group's <see cref="NodeSnapshot.WarningCount"/> from the snapshot handed with it.
    /// </remarks>
    public TaskCounts Counts { get; }
}

/// <summary>
/// A stop was applied to the run: from this event on the run starts nothing, <em>after</em> tasks included,
/// and every running task sees <see cref="TaskContext.CancellationToken"/> signalled.
/// </summary>
/// <remarks>
/// <para>
/// It is published once at most: when the run applies the first request on the <see cref="StopSignal"/> it was given, with
/// <see cref="By"/> set to <see cref="StopRequester.App"/>, or when the run's journal can no longer be written, with
/// <see cref="StopRequester.Dagline"/>, unless a stop was already in progress.
/// It is a step of its own. A signal already requested when the run starts gives <see cref="RunStarted"/>, the tasks that become ready,
/// this event, then <see cref="RunEnded"/>, with no task started.
/// </para>
/// <para>
/// After a stop that Dagline asked for, any request on the app's <see cref="StopSignal"/>, the first included, ends the wait for the
/// running tasks, as a second request does after a stop the app asked for.
/// </para>
/// <para>
/// The snapshot handed with this event, and every later one, has <see cref="Snapshot.Stop"/> set.
/// While the run waits for its running tasks to end, a task that succeeds still makes its dependents ready, where they stay,
/// and a task that fails still blocks its dependents.
/// </para>
/// </remarks>
public sealed record StopRequested : RunEvent
{
    internal StopRequested(DateTimeOffset at, StopRequester by, TimeSpan gracePeriod)
        : base(at)
    {
        By = by;
        GracePeriod = gracePeriod;
    }

    /// <summary>Who asked for the stop: the app, through its <see cref="StopSignal"/>, or Dagline, when a journal write failed.</summary>
    public StopRequester By { get; }

    /// <summary>
    /// How long the run waits for its running tasks to end. <see cref="Timeout.InfiniteTimeSpan"/> means the run waits until they have.
    /// </summary>
    public TimeSpan GracePeriod { get; }
}

/// <summary>
/// The grace ended: the run stopped waiting for its running tasks after a stop, with tasks still running, and lets go of them.
/// </summary>
/// <remarks>
/// <para>
/// It is published at most once, and only when tasks are still running as the wait ends. A wait that ends because the last
/// running task ended has no such event. In the same step, each task left behind follows in declaration order: the end of its wait
/// if it was waiting, then its <see cref="TaskStatusChanged"/> to <see cref="NodeStatus.Cancelled"/>, with the message
/// <c>still running when the run stopped waiting</c>. The tasks that this makes ready and the groups it settles come after them,
/// then <see cref="RunEnded"/>.
/// </para>
/// <para>
/// Nothing a task's body does after this changes the run, and nothing is published when the body ends.
/// </para>
/// </remarks>
public sealed record GraceEnded : RunEvent
{
    internal GraceEnded(DateTimeOffset at, GraceEndReason reason)
        : base(at)
    {
        Reason = reason;
    }

    /// <summary>Why the grace ended: a second stop, or the grace period ran out.</summary>
    public GraceEndReason Reason { get; }
}

/// <summary>The run ended. This is the last event of every run.</summary>
/// <remarks>
/// It carries exactly one of <see cref="Outcome"/> and <see cref="Error"/>. An ordinary run, stopped or not, ends with its run outcome.
/// A run whose journal could no longer be written ends with a <see cref="JournalWriteException"/> and no run outcome, and
/// <see cref="Runner.RunAsync"/> throws that same exception once reporters have handled every event.
/// </remarks>
public sealed record RunEnded : RunEvent
{
    internal RunEnded(DateTimeOffset at, RunOutcome outcome)
        : base(at)
    {
        Outcome = outcome;
    }

    internal RunEnded(DateTimeOffset at, Exception error)
        : base(at)
    {
        Error = error;
    }

    /// <summary>How the run ended, or <see langword="null"/> when it ended with an error.</summary>
    public RunOutcome? Outcome { get; }

    /// <summary>The error the run ended with, or <see langword="null"/> when it ended with a run outcome.</summary>
    public Exception? Error { get; }
}
