namespace Dagline;

/// <summary>What a run leaves behind once it has ended.</summary>
public sealed class RunResult
{
    internal RunResult(RunOutcome outcome, Snapshot snapshot, IReadOnlyList<ReporterError> reporterErrors)
    {
        Outcome = outcome;
        Snapshot = snapshot;
        ReporterErrors = reporterErrors;
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
}
