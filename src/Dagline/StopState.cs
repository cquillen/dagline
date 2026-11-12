namespace Dagline;

/// <summary>The facts of a stop that a run has applied, as <see cref="Snapshot.Stop"/> gives them.</summary>
/// <param name="RequestedAt">When the run applied the stop: the time of its <see cref="StopRequested"/> event.</param>
/// <param name="GracePeriod">
/// How long the run waits for its running tasks to end. <see cref="Timeout.InfiniteTimeSpan"/> means the run waits until they have.
/// </param>
/// <param name="Deadline">
/// When the wait for the running tasks ends at the latest, or <see langword="null"/> when the grace period is unlimited.
/// </param>
/// <param name="By">
/// Who asked for the stop: the app, through its <see cref="StopSignal"/>, or Dagline itself, because the run's journal could no
/// longer be written. It is the same as <see cref="StopRequested.By"/>, and is set in the same step that applies the stop, so a
/// reporter can read it from a snapshot before it has handled that event.
/// </param>
public sealed record StopState(DateTimeOffset RequestedAt, TimeSpan GracePeriod, DateTimeOffset? Deadline, StopRequester By);
