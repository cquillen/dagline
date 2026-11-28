namespace Dagline.Prototype;

internal enum EffectKind
{
    /// <summary>Write journal entries. Always the last effect of its list: the result is the next input.</summary>
    Record,

    /// <summary>Publish an event with the snapshot just after it.</summary>
    Publish,

    /// <summary>Start a task body.</summary>
    Start,

    /// <summary>Signal the token every task sees.</summary>
    SignalTasks,

    /// <summary>Start the grace-period timer.</summary>
    ArmGrace,

    /// <summary>Answer a checkpoint save.</summary>
    CompleteSave,

    /// <summary>Close the journal and let go of it. Invented: the sketch had no effect for it. Comes before the publish of RunEnded.</summary>
    CloseJournal,

    /// <summary>
    /// Invented: something changed that is not an event (a message, a detail line, a reported failure), so the current snapshot is
    /// out of date. Nobody but the loop can build a snapshot, so the loop builds one when it next goes idle.
    /// </summary>
    Stale,
}

internal enum SaveAnswer
{
    Saved,
    NotRunning,
    WriteFailed,
}

/// <summary>
/// One thing the loop is to carry out. A struct in a list the core reuses, so that deciding an input allocates no effect objects.
/// </summary>
internal readonly record struct Effect(
    EffectKind Kind,
    RunEvent? Event = null,
    SnapshotChange Change = default,
    BuiltTask? Task = null,
    IReadOnlyList<JournalEntry>? Entries = null,
    JournalFlush Flush = JournalFlush.None,
    TimeSpan Period = default,
    SaveCompletion? Save = null,
    SaveAnswer Answer = default,
    string? Text = null,
    Exception? Cause = null)
{
    public static readonly Effect SignalTasks = new(EffectKind.SignalTasks);
    public static readonly Effect CloseJournal = new(EffectKind.CloseJournal);
    public static readonly Effect Stale = new(EffectKind.Stale);

    public static Effect Record(IReadOnlyList<JournalEntry> entries, JournalFlush flush) => new(EffectKind.Record, Entries: entries, Flush: flush);

    public static Effect Publish(RunEvent runEvent, SnapshotChange change) => new(EffectKind.Publish, runEvent, change);

    public static Effect Start(BuiltTask task, string checkpoint) => new(EffectKind.Start, Task: task, Text: checkpoint);

    public static Effect ArmGrace(TimeSpan period) => new(EffectKind.ArmGrace, Period: period);

    public static Effect CompleteSave(SaveCompletion save, BuiltTask task, SaveAnswer answer, string text, Exception? cause = null)
        => new(EffectKind.CompleteSave, Task: task, Save: save, Answer: answer, Text: text, Cause: cause);
}

/// <summary>
/// A checkpoint save waiting for its answer. The context that made it and the loop that answers it share the cell that holds the
/// task's checkpoint as the task reads it, since nothing outside the loop may read the run's state.
/// </summary>
internal sealed class SaveCompletion(CheckpointCell cell, TaskCreationOptions options)
{
    private readonly TaskCompletionSource _source = new(options);

    public Task Task => _source.Task;

    public void Saved(string text)
    {
        cell.Text = text;
        _source.TrySetResult();
    }

    public void Refused(Exception why) => _source.TrySetException(why);
}

internal sealed class CheckpointCell(string text)
{
    private volatile string _text = text;

    public string Text
    {
        get => _text;
        set => _text = value;
    }
}
