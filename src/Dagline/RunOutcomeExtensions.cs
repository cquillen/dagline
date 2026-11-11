namespace Dagline;

/// <summary>What can be asked of a <see cref="RunOutcome"/>.</summary>
public static class RunOutcomeExtensions
{
    /// <summary>
    /// Maps a run outcome to a process exit code: 0 for complete, 1 for incomplete and 2 for stopped.
    /// </summary>
    /// <param name="outcome">How the run ended.</param>
    /// <returns>The exit code.</returns>
    /// <remarks>
    /// This is a default the app is free not to use: Dagline never sets the exit code of the process itself.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="outcome"/> is not a defined run outcome.</exception>
    public static int ToExitCode(this RunOutcome outcome) => outcome switch
    {
        RunOutcome.Complete => 0,
        RunOutcome.Incomplete => 1,
        RunOutcome.Stopped => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Not a run outcome."),
    };
}
