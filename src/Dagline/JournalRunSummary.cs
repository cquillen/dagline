namespace Dagline;

/// <summary>One run of a journal as <see cref="JournalSummary.LatestRun"/> gives it: when it ran, and who ran it where.</summary>
public sealed record JournalRunSummary
{
    internal JournalRunSummary(DateTimeOffset started, DateTimeOffset? ended, string user, string machine)
    {
        Started = started;
        Ended = ended;
        User = user;
        Machine = machine;
    }

    /// <summary>When the run started, in UTC on the run's clock.</summary>
    public DateTimeOffset Started { get; }

    /// <summary>
    /// When the run ended, in UTC on the run's clock, or <see langword="null"/> when the journal records no end for it: the run is still
    /// going, or it died.
    /// </summary>
    public DateTimeOffset? Ended { get; }

    /// <summary>The operating system user the run's process ran as.</summary>
    public string User { get; }

    /// <summary>The name of the machine the run's process ran on.</summary>
    public string Machine { get; }
}
