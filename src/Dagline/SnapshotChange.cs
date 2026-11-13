namespace Dagline;

/// <summary>
/// What changed in a run's snapshot with one event, or since the last event: how many node changes it takes from those
/// <see cref="SnapshotTaker"/> has recorded, and everything else a snapshot holds, as it is just after the change.
/// </summary>
/// <remarks>
/// It holds no node and no dictionary, so a step with many events holds little until each is published.
/// <see cref="SnapshotTaker"/> builds each snapshot on the nodes of the one before, so changes are taken in the order they were made.
/// </remarks>
/// <param name="NodeChanges">How many of the node changes recorded but not yet taken belong to this change, from the oldest.</param>
/// <param name="Milestones">When the run started, the stop it has applied, and when and how it ended.</param>
/// <param name="Tally">
/// The run's counts by status, as <see cref="RollUp.CopyRunCountsTo"/> writes them, then its slots in use, as <see cref="Slots.CopyTo"/>
/// writes them. Nothing changes it.
/// </param>
internal readonly record struct SnapshotChange(int NodeChanges, RunMilestones Milestones, int[] Tally);

/// <summary>
/// When a run started, the stop it has applied, and when and how it ended, as a snapshot shows them. Each changes at most once,
/// so the changes of the events between two milestones share one of these.
/// </summary>
/// <param name="Started">When the run started.</param>
/// <param name="Stop">The stop the run has applied, or <see langword="null"/> while none has been.</param>
/// <param name="Ended">When the run ended, or <see langword="null"/> while it has not.</param>
/// <param name="Outcome">How the run ended, or <see langword="null"/> while it has not.</param>
internal sealed record RunMilestones(DateTimeOffset Started, StopState? Stop, DateTimeOffset? Ended, RunOutcome? Outcome);

/// <summary>One node as it is after a change, and its position among the graph's nodes.</summary>
internal readonly record struct NodeChange(int Position, NodeSnapshot Node);
