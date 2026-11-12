namespace Dagline;

/// <summary>How the latest run of a journal ended, as <see cref="JournalSummary.Ending"/> gives it.</summary>
public enum JournalEnding
{
    /// <summary>The latest run ended <see cref="RunOutcome.Complete"/>: every task succeeded. The journal is finished.</summary>
    Complete,

    /// <summary>The latest run ended <see cref="RunOutcome.Incomplete"/>: a task failed or was blocked.</summary>
    Incomplete,

    /// <summary>The latest run ended <see cref="RunOutcome.Stopped"/>: it was asked to stop before every task succeeded.</summary>
    Stopped,

    /// <summary>A run holds the journal at the moment it was listed. Continuing or deleting it now throws <see cref="JournalInUseException"/>.</summary>
    InUse,

    /// <summary>
    /// The latest run recorded no end and no run holds the journal: its process was killed, its machine went down, a write to the
    /// journal failed, or the journal's first run was never recorded at all. The journal can be continued like any other.
    /// </summary>
    Died,
}
