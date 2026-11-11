namespace Dagline;

/// <summary>What a run leaves behind once it has ended.</summary>
public sealed record RunResult
{
    internal RunResult(RunOutcome outcome, Snapshot snapshot)
    {
        Outcome = outcome;
        Snapshot = snapshot;
    }

    /// <summary>How the run ended. <see cref="RunOutcomeExtensions.ToExitCode"/> maps it to a process exit code.</summary>
    public RunOutcome Outcome { get; }

    /// <summary>
    /// The final snapshot of the run. It is the same snapshot that reporters were handed with <see cref="RunEnded"/>.
    /// </summary>
    public Snapshot Snapshot { get; }
}
