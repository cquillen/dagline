namespace Dagline;

/// <summary>How a run ended.</summary>
public enum RunOutcome
{
    /// <summary>Every task succeeded. A graph with no tasks is complete.</summary>
    Complete,

    /// <summary>
    /// Nobody asked the run to drain or to stop, it went as far as it could, and at least one task failed or was blocked.
    /// </summary>
    Incomplete,

    /// <summary>The run was drained or stopped and at least one task had not succeeded.</summary>
    Stopped,

    /// <summary>
    /// The run's journal could no longer be written before the run had ended, so it is missing something the run did. It takes
    /// precedence over the other three: a run whose last settle could not be written is unrecorded, not complete.
    /// <see cref="RunResult.JournalError"/> and <see cref="RunEnded.JournalError"/> give the file system's failure. A failure to
    /// write the entry for the run's end alone leaves the run the outcome it reached, with the error beside it.
    /// </summary>
    Unrecorded,
}
