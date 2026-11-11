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

/// <summary>The run ended. This is the last event of every run.</summary>
/// <param name="At">When the run ended.</param>
/// <param name="Outcome">How the run ended.</param>
public sealed record RunEnded(DateTimeOffset At, RunOutcome Outcome) : RunEvent(At);
