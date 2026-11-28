namespace Dagline;

/// <summary>
/// An event the step core has decided, with what it changed in the run's snapshot. <see cref="StepCore.SnapshotAfter"/> takes the
/// snapshot just after the event from it, as the loop publishes the event, so the core holds no snapshot of its own.
/// </summary>
/// <param name="Event">The event.</param>
/// <param name="Change">What the event changed in the run's snapshot.</param>
internal readonly record struct StepEvent(RunEvent Event, SnapshotChange Change);
