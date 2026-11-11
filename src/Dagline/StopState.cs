namespace Dagline;

/// <summary>The facts of a stop that a run has applied, as <see cref="Snapshot.Stop"/> gives them.</summary>
/// <param name="RequestedAt">When the run applied the stop: the time of its <see cref="StopRequested"/> event.</param>
/// <param name="GracePeriod">
/// How long the run waits for its running tasks to end. <see cref="Timeout.InfiniteTimeSpan"/> means the run waits until they have.
/// </param>
/// <param name="Deadline">
/// When the wait for the running tasks ends at the latest, or <see langword="null"/> when the grace period is unlimited.
/// </param>
public sealed record StopState(DateTimeOffset RequestedAt, TimeSpan GracePeriod, DateTimeOffset? Deadline);
