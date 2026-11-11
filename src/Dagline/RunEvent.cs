namespace Dagline;

/// <summary>
/// A record of one thing that happened in a run, with the time it happened.
/// Every reporter receives a run's events in the same order, causes before effects.
/// </summary>
/// <remarks>The set of event kinds is closed: a type outside Dagline cannot add one.</remarks>
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
public sealed record RunStarted(DateTimeOffset At, ISnapshotSource Source) : RunEvent(At);

/// <summary>
/// A task changed status. One is published for every change, pending to ready included.
/// </summary>
/// <param name="At">When the status changed.</param>
/// <param name="Node">The task as it is after the change. When the task has settled, this carries its whole outcome.</param>
/// <param name="From">The status before the change.</param>
/// <param name="To">The status after the change.</param>
public sealed record TaskStatusChanged(DateTimeOffset At, NodeSnapshot Node, NodeStatus From, NodeStatus To) : RunEvent(At);

/// <summary>
/// A group settled: the last task beneath it, at any depth, settled.
/// </summary>
/// <remarks>
/// <para>
/// The event comes after the last <see cref="TaskStatusChanged"/> of every task beneath the group. Within the step in which
/// a task settles, it follows the tasks that became ready and precedes the tasks that start. When several groups settle in one step,
/// the deepest comes first, then declaration order, so a group's event follows those of the groups beneath it.
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
    : RunEvent(At);

/// <summary>The run ended. This is the last event of every run.</summary>
/// <param name="At">When the run ended.</param>
/// <param name="Outcome">How the run ended.</param>
public sealed record RunEnded(DateTimeOffset At, RunOutcome Outcome) : RunEvent(At);
