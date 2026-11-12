namespace Dagline;

/// <summary>One run of a journal as <see cref="JournalSummary.LatestRun"/> gives it: when it ran, and who ran it where.</summary>
/// <param name="Started">When the run started, in UTC on the run's clock.</param>
/// <param name="Ended">
/// When the run ended, in UTC on the run's clock, or <see langword="null"/> when the journal records no end for it: the run is still
/// going, or it died.
/// </param>
/// <param name="User">The operating system user the run's process ran as.</param>
/// <param name="Machine">The name of the machine the run's process ran on.</param>
public sealed record JournalRunSummary(DateTimeOffset Started, DateTimeOffset? Ended, string User, string Machine);
