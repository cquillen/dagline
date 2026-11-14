namespace Dagline;

/// <summary>
/// A reporter that failed during a run and was dropped. A run never throws a reporter's exception:
/// it records one of these on <see cref="RunResult.ReporterErrors"/> and carries on.
/// </summary>
public sealed record ReporterError
{
    internal ReporterError(IReporter reporter, RunEvent? runEvent, Exception exception)
    {
        Reporter = reporter;
        Event = runEvent;
        Exception = exception;
    }

    /// <summary>The reporter that failed, as it was given in <see cref="RunOptions.Reporters"/>.</summary>
    public IReporter Reporter { get; }

    /// <summary>
    /// The event the reporter was handling when it failed. It was handed no event after this one.
    /// <see langword="null"/> when the reporter was dropped for a reason that belongs to no one event, which no run does yet.
    /// </summary>
    public RunEvent? Event { get; }

    /// <summary>
    /// What the reporter threw from <see cref="IReporter.OnEventAsync"/>, or what the <see cref="ValueTask"/> it returned faulted with.
    /// </summary>
    public Exception Exception { get; }
}
