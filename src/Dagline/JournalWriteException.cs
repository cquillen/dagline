namespace Dagline;

/// <summary>
/// Thrown when a run's journal can no longer be written, for instance because the disk is full or the file is gone: by
/// <see cref="Runner.RunAsync"/> once the run has ended, and by <see cref="TaskContext.SaveCheckpointAsync"/> and
/// <see cref="TaskContext.ClearCheckpointAsync"/> to the task whose save failed.
/// </summary>
/// <remarks>
/// <para>
/// When a write fails, Dagline stops the run itself: it publishes <see cref="StopRequested"/> with <see cref="StopRequester.Dagline"/>,
/// unless a stop was already in progress, starts nothing new, signals the running tasks and waits the grace period. Any request on the
/// app's <see cref="StopSignal"/> ends that wait. No further write to the journal is attempted, though events still describe what
/// happens. The run then ends with this exception: <see cref="RunEnded"/> carries it and no run outcome, and
/// <see cref="Runner.RunAsync"/> throws it after reporters have handled every event. The journal has no run-ended entry, so
/// <see cref="Journals.ListAsync"/> shows it as <see cref="JournalEnding.Died"/>, and it can be continued like any other.
/// </para>
/// <para>
/// <see cref="Exception.InnerException"/> is the file system's own exception.
/// </para>
/// </remarks>
public sealed class JournalWriteException : DaglineException
{
    internal JournalWriteException(Exception innerException)
        : base($"The run's journal can no longer be written: {innerException.Message}", innerException)
    {
    }

    internal JournalWriteException(Exception innerException, Snapshot snapshot)
        : this(innerException)
    {
        Snapshot = snapshot;
    }

    /// <summary>
    /// The run as it was: from <see cref="Runner.RunAsync"/> and on <see cref="RunEnded"/>, the final snapshot, which has an end time
    /// and no run outcome; from a checkpoint save, the snapshot as of the failed save.
    /// </summary>
    // Set by the run as it publishes RunEnded, since the final snapshot is taken only then, after the event that carries this exists.
    public Snapshot Snapshot { get; internal set; } = null!;
}
