namespace Dagline;

/// <summary>What a run leaves behind once it has ended.</summary>
public sealed record RunResult
{
    internal RunResult(RunOutcome outcome)
    {
        Outcome = outcome;
    }

    /// <summary>How the run ended.</summary>
    public RunOutcome Outcome { get; }
}
