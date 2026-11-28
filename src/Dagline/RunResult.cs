namespace Dagline;

/// <summary>What a run leaves behind once it has ended.</summary>
public sealed class RunResult
{
    internal RunResult(
        RunOutcome outcome,
        Snapshot snapshot,
        IReadOnlyList<ReporterError> reporterErrors,
        Exception? journalError = null,
        JournalWriteException? unrecorded = null)
    {
        Outcome = outcome;
        Snapshot = snapshot;
        ReporterErrors = reporterErrors;
        JournalError = journalError;
        Unrecorded = unrecorded;
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
    /// The file system's failure that stopped the run's journal, on the new engine, or <see langword="null"/> when every write landed:
    /// the cause of <see cref="Unrecorded"/> when the run ended unrecorded, and otherwise the failure of its end entry alone, which
    /// leaves the run the outcome it reached. Internal until the cutover (spec #139, #153), which makes it public.
    /// </summary>
    internal Exception? JournalError { get; }

    /// <summary>
    /// On the new engine, the exception the run ended with when its journal could no longer be written, the same one its
    /// <see cref="RunEnded"/> carried: the run outcome unrecorded, which takes precedence over <see cref="Outcome"/>, the outcome
    /// the run would otherwise have reached. <see langword="null"/> otherwise. Until the cutover (#153) makes unrecorded a run
    /// outcome, the engine switch throws it, as the existing engine does.
    /// </summary>
    internal JournalWriteException? Unrecorded { get; }
}
