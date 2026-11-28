namespace Dagline;

/// <summary>The kinds of <see cref="Effect"/>: a closed set, each carried out by one case of the loop.</summary>
internal enum EffectKind
{
    /// <summary>
    /// Writes journal entries, flushed or not. Always the last effect of its list: the result of the write is the next input the
    /// loop gives the core, ahead of anything in the inbox.
    /// </summary>
    Record,

    /// <summary>Publishes an event, with the snapshot just after it, which the loop builds as it publishes.</summary>
    Publish,

    /// <summary>Starts a task's body. The entry for its start, if the run is on a journal, is already written.</summary>
    StartBody,

    /// <summary>Closes the journal and lets go of it, so that a later run can continue it. It comes before the run's end is published.</summary>
    CloseJournal,

    /// <summary>The run has ended with a run outcome: the loop waits for its reporters, then returns its result.</summary>
    EndRun,
}

/// <summary>
/// One thing the loop is to carry out, as <see cref="StepCore"/> decided it. A struct in a list the core reuses, so that deciding
/// an input allocates no effect objects, and an event carries what it changed in the snapshot and never a snapshot: the loop
/// builds each snapshot as it publishes its event, never for every event of a step beforehand.
/// </summary>
/// <param name="Kind">What to carry out.</param>
/// <param name="Event">The event to publish, for <see cref="EffectKind.Publish"/>.</param>
/// <param name="Change">What the event changed in the run's snapshot, for <see cref="EffectKind.Publish"/>.</param>
/// <param name="Task">The task whose body to start, for <see cref="EffectKind.StartBody"/>.</param>
/// <param name="Entries">The entries to write, for <see cref="EffectKind.Record"/>.</param>
/// <param name="Flush">Whether the entries are flushed to the disk, for <see cref="EffectKind.Record"/>.</param>
/// <param name="Outcome">The run outcome, for <see cref="EffectKind.EndRun"/>.</param>
internal readonly record struct Effect(
    EffectKind Kind,
    RunEvent? Event = null,
    SnapshotChange Change = default,
    BuiltTask? Task = null,
    IReadOnlyList<JournalEntry>? Entries = null,
    JournalFlush Flush = JournalFlush.None,
    RunOutcome Outcome = default)
{
    public static Effect CloseJournal { get; } = new(EffectKind.CloseJournal);

    public static Effect Record(IReadOnlyList<JournalEntry> entries, JournalFlush flush) => new(EffectKind.Record, Entries: entries, Flush: flush);

    public static Effect Publish(StepEvent stepEvent) => new(EffectKind.Publish, stepEvent.Event, stepEvent.Change);

    public static Effect StartBody(BuiltTask task) => new(EffectKind.StartBody, Task: task);

    public static Effect EndRun(RunOutcome outcome) => new(EffectKind.EndRun, Outcome: outcome);
}
