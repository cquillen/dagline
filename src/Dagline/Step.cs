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
/// <param name="Entries">
/// The journal entries the step records, in order, which go out in one write before anything else the step does.
/// Empty for a run with no journal.
/// </param>
/// <param name="Flushes">Whether the write of <paramref name="Entries"/> is flushed to the disk.</param>
/// <remarks>A step with no events is a report that changed only the snapshot, such as progress.</remarks>
internal sealed record Step(
    IReadOnlyList<StepEvent> Events,
    IReadOnlyList<BuiltTask> Starts,
    bool SignalsTheTasks,
    TimeSpan? GracePeriodTimer,
    RunEnd? Ends,
    IReadOnlyList<JournalEntry> Entries,
    bool Flushes);

/// <summary>
/// One event of a step, with what it changed in the run's snapshot. <see cref="RunSteps.SnapshotAfter"/> takes the snapshot
/// just after the event from it, as the run publishes the event, so a step holds no snapshot of its own.
/// </summary>
internal readonly record struct StepEvent(RunEvent Event, SnapshotChange Change);

/// <summary>
/// How a run ends: exactly one of its outcome and the error it ends with, and how many stop requests it applied, so that the wait
/// for reporters hears only later ones.
/// </summary>
internal readonly record struct RunEnd(RunOutcome? Outcome, JournalWriteException? Error, int RequestsApplied);
