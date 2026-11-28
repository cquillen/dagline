namespace Dagline;

/// <summary>What a run leaves behind once it has ended.</summary>
public sealed class RunResult
{
    internal RunResult(
        RunOutcome outcome,
        Snapshot snapshot,
        IReadOnlyList<ReporterError> reporterErrors,
        Exception? journalError)
    {
        Outcome = outcome;
        Snapshot = snapshot;
        ReporterErrors = reporterErrors;
        JournalError = journalError;
    }

    /// <summary>How the run ended. <see cref="RunOutcomeExtensions.ToExitCode"/> maps it to a process exit code.</summary>
    public RunOutcome Outcome { get; }

    /// <summary>
    /// The final snapshot of the run. It is the same snapshot that reporters were handed with <see cref="RunEnded"/>.
    /// </summary>
    public Snapshot Snapshot { get; }

    /// <summary>
    /// One entry for each reporter that failed during the run and was dropped, in the order the reporters were given
    /// in <see cref="RunOptions.Reporters"/>. Empty when no reporter failed.
    /// </summary>
    /// <remarks>
    /// A reporter's failure changes nothing else about the run: <see cref="Outcome"/> and <see cref="Snapshot"/>
    /// are what they would have been without it. A reporter can fail at most once, since it is handed nothing after it fails.
    /// </remarks>
    public IReadOnlyList<ReporterError> ReporterErrors { get; }

    /// <summary>
    /// The file system's failure that stopped the run's journal, the same as <see cref="RunEnded.JournalError"/>: an
    /// <see cref="IOException"/>, or an <see cref="UnauthorizedAccessException"/> when the process may no longer write the file.
    /// <see langword="null"/> when every write landed, and always <see langword="null"/> for a run with no journal.
    /// </summary>
    /// <remarks>
    /// When it is set, <see cref="Outcome"/> is <see cref="RunOutcome.Unrecorded"/>, unless only the entry for the run's end could
    /// not be written: every settle is then on disk and the run keeps the outcome it reached, though the journal lists it as
    /// <see cref="JournalEnding.Died"/>. Either way the journal can be continued once the cause is fixed.
    /// </remarks>
    public Exception? JournalError { get; }
}
