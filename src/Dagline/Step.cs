namespace Dagline;

/// <summary>
/// One step of a run, as <see cref="RunSteps"/> decided it: everything the run does in response to one thing happening.
/// <see cref="Run"/> carries it out, and decides nothing.
/// </summary>
/// <param name="Events">
/// The events of the step in the order they are published, each with what it changed in the run's snapshot.
/// </param>
/// <param name="Starts">The tasks whose bodies the step starts, in declaration order. Each is already running in the step's snapshots.</param>
/// <param name="SignalsTheTasks">Whether the step signals the token every task sees: it applies a stop.</param>
/// <param name="GracePeriodTimer">
/// When the step starts the grace-period timer, how long until it fires; <see langword="null"/> when it starts none.
/// </param>
/// <param name="Ends">How the run ends, when the step ends it; <see langword="null"/> otherwise.</param>
/// <remarks>A step with no events is a report that changed only the snapshot, such as progress, or the end of a run with no outcome.</remarks>
internal sealed record Step(
    IReadOnlyList<StepEvent> Events,
    IReadOnlyList<BuiltTask> Starts,
    bool SignalsTheTasks,
    TimeSpan? GracePeriodTimer,
    RunEnd? Ends);

/// <summary>
/// One event of a step, with what it changed in the run's snapshot. <see cref="RunSteps.SnapshotAfter"/> takes the snapshot
/// just after the event from it, as the run publishes the event, so a step holds no snapshot of its own.
/// </summary>
/// <param name="Event">The event.</param>
/// <param name="Change">What the event changed in the run's snapshot.</param>
internal readonly record struct StepEvent(RunEvent Event, SnapshotChange Change);

/// <summary>
/// How a run ends: with a run outcome, or stopped by a failed journal write; and whether it applied a stop, since only a run that
/// did not has the wait for its reporters cut short by the app's token.
/// </summary>
/// <param name="Outcome">
/// The run outcome, which the step's <see cref="RunEnded"/> carries; <see langword="null"/> when a journal write failed. Then the
/// step has no event: the run publishes the <see cref="RunEnded"/> itself, with the <see cref="JournalWriteException"/> it ends
/// with, since that exception carries the final snapshot, which the run takes only then.
/// </param>
/// <param name="StopApplied">Whether the run applied a stop, by the app or by Dagline.</param>
internal sealed record RunEnd(RunOutcome? Outcome, bool StopApplied);
