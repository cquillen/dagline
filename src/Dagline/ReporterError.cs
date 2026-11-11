namespace Dagline;

/// <summary>
/// A reporter that failed during a run and was dropped. A run never throws a reporter's exception:
/// it records one of these on <see cref="RunResult.ReporterErrors"/> and carries on.
/// </summary>
/// <param name="Reporter">The reporter that failed, as it was given in <see cref="RunOptions.Reporters"/>.</param>
/// <param name="Event">
/// The event the reporter was handling when it failed. It was handed no event after this one.
/// <see langword="null"/> when the reporter was dropped for a reason that belongs to no one event, which no run does yet.
/// </param>
/// <param name="Exception">
/// What the reporter threw from <see cref="IReporter.OnEventAsync"/>, or what the <see cref="ValueTask"/> it returned faulted with.
/// </param>
public sealed record ReporterError(IReporter Reporter, RunEvent? Event, Exception Exception);
