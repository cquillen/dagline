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
/// <item><description>The change itself: the run starting, or a task settling.</description></item>
/// <item><description>Every task that this blocks, pending to blocked, in declaration order.</description></item>
/// <item><description>Every task whose dependencies are now all met, pending to ready, in declaration order.</description></item>
/// <item><description>Every group that is now settled, the deepest first, then in declaration order.</description></item>
/// <item><description>Every task that now starts, ready to running, in declaration order.</description></item>
/// </list>
/// <para>
/// The events of one step are never mixed with those of another. A <see cref="WarningAdded"/> is a step of its own.
/// </para>
/// </remarks>
public abstract record RunEvent
{
    private protected RunEvent(DateTimeOffset at)
    {
        At = at;
    }

    /// <summary>When it happened, read from the run's <see cref="RunOptions.TimeProvider"/>.</summary>
    public DateTimeOffset At { get; init; }
}

/// <summary>The run started. This is the first event of every run.</summary>
/// <param name="At">When the run started.</param>
/// <param name="Source">A live source of snapshots of this run, which a reporter may keep and read at any time.</param>
public sealed record RunStarted(DateTimeOffset At, ISnapshotSource Source) : RunEvent(At)
{
    /// <summary>A live source of snapshots of this run, which a reporter may keep and read at any time.</summary>
    /// <exception cref="ArgumentNullException">The value given is <see langword="null"/>.</exception>
    public ISnapshotSource Source { get; init => field = Arguments.NotNull(value); } = Arguments.NotNull(Source);
}

/// <summary>
/// A task changed status. One is published for every change, pending to ready and pending to blocked included.
/// </summary>
/// <param name="At">When the status changed.</param>
/// <param name="Node">The task as it is after the change. When the task has settled, this carries its whole outcome.</param>
/// <param name="From">The status before the change.</param>
/// <param name="To">The status after the change.</param>
public sealed record TaskStatusChanged(DateTimeOffset At, NodeSnapshot Node, NodeStatus From, NodeStatus To) : RunEvent(At)
{
    /// <summary>The task as it is after the change. When the task has settled, this carries its whole outcome.</summary>
    /// <exception cref="ArgumentNullException">The value given is <see langword="null"/>.</exception>
    public NodeSnapshot Node { get; init => field = Arguments.NotNull(value); } = Arguments.NotNull(Node);
}

/// <summary>
/// A running task added a warning to its outcome with <see cref="TaskContext.Warn"/>, and Dagline kept it.
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
/// <param name="At">When the warning was added.</param>
/// <param name="Path">The path of the task that added the warning.</param>
/// <param name="Warning">The warning, on one line: each line break in the text the task gave is replaced by a space.</param>
public sealed record WarningAdded(DateTimeOffset At, NodePath Path, string Warning) : RunEvent(At)
{
    /// <summary>The warning, on one line: each line break in the text the task gave is replaced by a space.</summary>
    /// <exception cref="ArgumentNullException">The value given is <see langword="null"/>.</exception>
    public string Warning { get; init => field = Arguments.NotNull(value); } = Arguments.NotNull(Warning);
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
/// A group that is settled from the start of the run, because no task is beneath it, gets no event.
/// A group's status before it settles is never an event: read it from a snapshot.
/// </para>
/// </remarks>
/// <param name="At">When the group settled: the time its last task settled.</param>
/// <param name="Path">The path of the group.</param>
/// <param name="Status">
/// The status the group settled with, derived from the tasks beneath it: failed if any failed, else cancelled if any was cancelled,
/// else blocked if any is blocked, else succeeded.
/// </param>
/// <param name="Counts">
/// How many tasks beneath the group, at any depth, have each status. Every status is present, in lifecycle order.
/// </param>
public sealed record GroupSettled(DateTimeOffset At, NodePath Path, NodeStatus Status, IReadOnlyDictionary<NodeStatus, int> Counts)
    : RunEvent(At)
{
    /// <summary>
    /// How many tasks beneath the group, at any depth, have each status. Every status is present, in lifecycle order.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value given is <see langword="null"/>.</exception>
    public IReadOnlyDictionary<NodeStatus, int> Counts { get; init => field = Arguments.NotNull(value); } = Arguments.NotNull(Counts);
}

/// <summary>The run ended. This is the last event of every run.</summary>
/// <param name="At">When the run ended.</param>
/// <param name="Outcome">How the run ended.</param>
public sealed record RunEnded(DateTimeOffset At, RunOutcome Outcome) : RunEvent(At);
