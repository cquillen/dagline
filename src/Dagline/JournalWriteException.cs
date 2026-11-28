namespace Dagline;

/// <summary>
/// Thrown to a task by <see cref="TaskContext.SaveCheckpointAsync"/> and <see cref="TaskContext.ClearCheckpointAsync"/> when its
/// save could not be written to the run's journal, for instance because the disk is full or the file is gone, or when the save
/// comes after an earlier write of the run failed: it is then refused with that earlier failure, and nothing is written. It is
/// thrown to nobody else: the run itself throws nothing for a failed write.
/// </summary>
/// <remarks>
/// <para>
/// When a write fails, Dagline stops the run itself: it publishes <see cref="StopRequested"/> with <see cref="StopRequester.Dagline"/>,
/// unless a stop was already in progress, starts nothing new, signals the running tasks and waits the grace period. Neither a drain nor
/// the app's token cancelled shortens that wait. A save whose own write failed is answered with this exception before that stop is
/// applied. No further write to the journal is attempted, though events still describe what happens. The run then ends with the
/// run outcome <see cref="RunOutcome.Unrecorded"/>, and <see cref="RunEnded.JournalError"/> and <see cref="RunResult.JournalError"/>
/// give the same failure as this exception's <see cref="Exception.InnerException"/>. The journal has no run-ended entry, so
/// <see cref="Journals.ListAsync"/> shows it as <see cref="JournalEnding.Died"/>, and it can be continued like any other.
/// </para>
/// <para>
/// <see cref="Exception.InnerException"/> is the file system's own exception: an <see cref="IOException"/>, or an
/// <see cref="UnauthorizedAccessException"/> when the process may no longer write the file.
/// </para>
/// </remarks>
public sealed class JournalWriteException : DaglineException
{
    internal JournalWriteException(Exception innerException, Snapshot snapshot)
        : base($"The run's journal can no longer be written: {innerException.Message}", innerException)
    {
        Snapshot = snapshot;
    }

    /// <summary>
    /// The run as it was when the save was refused: the run's current snapshot as of the events published before the refusal. For
    /// a save whose own write failed, that is before the stop the failure leads to, so the snapshot has no stop from it; a save
    /// refused after an earlier write failed comes after that stop, and its snapshot has it.
    /// </summary>
    public Snapshot Snapshot { get; }
}
