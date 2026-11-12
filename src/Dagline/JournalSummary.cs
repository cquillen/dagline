namespace Dagline;

/// <summary>
/// One journal as <see cref="Journals.ListAsync"/> gives it: enough about it for an app's policy, or an operator, to choose one.
/// </summary>
/// <remarks>
/// A summary is read from the journal's file at the moment it was listed, and does not change after. It is folded from the
/// journal's entries by the same code that a run continuing the journal uses.
/// </remarks>
public sealed record JournalSummary
{
    internal JournalSummary(JournalFold fold, bool inUse)
    {
        Id = fold.Id;
        Created = fold.Created;
        Description = fold.Description;
        Runs = fold.Runs;
        LatestRun = fold.LatestRun;
        Ending = EndingOf(fold.LatestOutcome, inUse);
        Counts = fold.LastStatusCounts();
        Finished = fold.LatestOutcome is RunOutcome.Complete;
    }

    /// <summary>The journal's id, which <see cref="Journal.Continue"/> and <see cref="Journals.DeleteAsync"/> take.</summary>
    public string Id { get; }

    /// <summary>When the journal was created, in UTC on the clock of its first run.</summary>
    public DateTimeOffset Created { get; }

    /// <summary>The description the journal was created with, or <see langword="null"/> when it has none.</summary>
    public string? Description { get; }

    /// <summary>How many runs the journal records as started, the latest included.</summary>
    public int Runs { get; }

    /// <summary>
    /// The latest run the journal records. <see langword="null"/> only when no run was ever recorded: the journal was created and its
    /// first run never got as far as recording its start.
    /// </summary>
    public JournalRunSummary? LatestRun { get; }

    /// <summary>
    /// How the latest run ended: the run outcome it recorded; <see cref="JournalEnding.InUse"/> when a run holds the journal at the
    /// moment it was listed, whatever the file records; and <see cref="JournalEnding.Died"/> when the latest run recorded no end and
    /// nothing holds the journal, which covers a journal whose first run was never recorded.
    /// </summary>
    public JournalEnding Ending { get; }

    /// <summary>
    /// How many tasks the journal records in each status, with every status present. Each path the journal has an entry for counts
    /// once, by its last recorded status: its latest settle, pending once a later set-aside names it, and running when its latest
    /// entry is a start, as for a task that was running when its run died. Paths no longer in a graph count as well, since the
    /// listing has no graph. Ready is always zero.
    /// </summary>
    public IReadOnlyDictionary<NodeStatus, int> Counts { get; }

    /// <summary>
    /// Whether the journal is finished: its latest run recorded that it was <see cref="RunOutcome.Complete"/>. A finished journal can
    /// still be continued: with an unchanged graph the run is complete at once.
    /// </summary>
    public bool Finished { get; }

    private static JournalEnding EndingOf(RunOutcome? latestOutcome, bool inUse)
    {
        if (inUse)
        {
            return JournalEnding.InUse;
        }

        return latestOutcome switch
        {
            RunOutcome.Complete => JournalEnding.Complete,
            RunOutcome.Incomplete => JournalEnding.Incomplete,
            RunOutcome.Stopped => JournalEnding.Stopped,
            null => JournalEnding.Died,
            _ => throw new ArgumentOutOfRangeException(nameof(latestOutcome), latestOutcome, "Not a run outcome."),
        };
    }
}
