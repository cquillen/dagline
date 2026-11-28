namespace Dagline.Prototype;

internal enum InputKind
{
    Start,
    BodyEnded,
    Warning,
    WaitOpened,
    WaitClosed,
    Message,
    Detail,
    Failure,
    CheckpointSave,
    Drain,
    Stop,
    GraceElapsed,
    Written,

    // Not inputs of the core: the loop reads these two after the core has ended, while it waits for reporters.
    ReportersDone,
    ReporterWaitElapsed,
}

/// <summary>
/// One thing that happened to a run, as it sits in the inbox. A struct, so that a post allocates nothing but its payload.
/// </summary>
internal readonly record struct Input(InputKind Kind, BuiltTask? Task = null, string? Text = null, object? Payload = null)
{
    public static readonly Input Start = new(InputKind.Start);
    public static readonly Input Drain = new(InputKind.Drain);
    public static readonly Input Stop = new(InputKind.Stop);
    public static readonly Input GraceElapsed = new(InputKind.GraceElapsed);
    public static readonly Input ReportersDone = new(InputKind.ReportersDone);
    public static readonly Input ReporterWaitElapsed = new(InputKind.ReporterWaitElapsed);

    public static Input BodyEnded(BuiltTask task, TaskOutcome ended) => new(InputKind.BodyEnded, task, Payload: ended);

    public static Input Warning(BuiltTask task, string warning) => new(InputKind.Warning, task, warning);

    public static Input WaitOpened(BuiltTask task, string? text) => new(InputKind.WaitOpened, task, text);

    public static Input WaitClosed(BuiltTask task) => new(InputKind.WaitClosed, task);

    public static Input Message(BuiltTask task, string message) => new(InputKind.Message, task, message);

    /// <summary>Detail lines the task's side decided to keep: those beyond the 100 kept are counted in the task's slot instead.</summary>
    public static Input Detail(BuiltTask task, string[] keptLines) => new(InputKind.Detail, task, Payload: keptLines);

    public static Input Failure(BuiltTask task, string message, string[] keptLines) => new(InputKind.Failure, task, message, keptLines);

    public static Input CheckpointSave(BuiltTask task, string text, SaveCompletion completion)
        => new(InputKind.CheckpointSave, task, text, completion);

    /// <summary>The result of the write the last Record effect asked for: <see langword="null"/> when it landed.</summary>
    public static Input Written(Exception? failure) => new(InputKind.Written, Payload: failure);

    public override string ToString() => Task is null ? Kind.ToString() : $"{Kind}({Task.Path})";
}
