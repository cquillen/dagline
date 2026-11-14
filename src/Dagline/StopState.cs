namespace Dagline;

/// <summary>The facts of a stop that a run has applied, as <see cref="Snapshot.Stop"/> gives them.</summary>
public sealed record StopState
{
    internal StopState(DateTimeOffset requestedAt, TimeSpan gracePeriod, DateTimeOffset? deadline, StopRequester by)
    {
        RequestedAt = requestedAt;
        GracePeriod = gracePeriod;
        Deadline = deadline;
        By = by;
    }

    /// <summary>When the run applied the stop: the time of its <see cref="StopRequested"/> event.</summary>
    public DateTimeOffset RequestedAt { get; }

    /// <summary>
    /// How long the run waits for its running tasks to end. <see cref="Timeout.InfiniteTimeSpan"/> means the run waits until they have.
    /// </summary>
    public TimeSpan GracePeriod { get; }

    /// <summary>
    /// When the wait for the running tasks ends at the latest, or <see langword="null"/> when the grace period is unlimited.
    /// </summary>
    public DateTimeOffset? Deadline { get; }

    /// <summary>
    /// Who asked for the stop: the app, through its <see cref="StopSignal"/>, or Dagline itself, because the run's journal could no
    /// longer be written. It is the same as <see cref="StopRequested.By"/>, and is set in the same step that applies the stop, so a
    /// reporter can read it from a snapshot before it has handled that event.
    /// </summary>
    public StopRequester By { get; }
}
