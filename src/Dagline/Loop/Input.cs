namespace Dagline;

/// <summary>The kinds of <see cref="Input"/>: a closed set, each answered by one case of <see cref="StepCore.Apply(in Input, DateTimeOffset)"/> or of the loop.</summary>
internal enum InputKind
{
    /// <summary>The run starts.</summary>
    Start,

    /// <summary>A task's body has ended, with the outcome its end gives.</summary>
    BodyEnded,

    /// <summary>A task added a warning that is kept: one of its first <see cref="TaskSample.Kept"/>, which its sample counts.</summary>
    WarningKept,

    /// <summary>A task opened a waiting scope, with its text. Always posted: the core ignores it for a task that is not running.</summary>
    WaitOpened,

    /// <summary>A task closed a waiting scope.</summary>
    WaitClosed,

    /// <summary>
    /// The result of the write the last <see cref="EffectKind.Record"/> asked for. The loop gives it to the core next, ahead of
    /// anything in the inbox, and never posts it.
    /// </summary>
    Written,

    /// <summary>
    /// Every reporter has handled every event, or has been dropped. Not an input of the core: the loop reads it, once the run has
    /// ended, as the end of its wait for reporters.
    /// </summary>
    ReportersDone,
}

/// <summary>
/// One thing that happened to a run, as it sits in the run's <see cref="Inbox"/>. A struct, so that a post allocates nothing
/// beyond what it carries.
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="Task">The task it happened to, for an input about a task.</param>
/// <param name="Payload">
/// What the input carries beyond its task: the <see cref="TaskOutcome"/> of <see cref="InputKind.BodyEnded"/>, the text of a
/// task's report, or the failure of <see cref="InputKind.Written"/>, which is <see langword="null"/> when the write landed.
/// </param>
internal readonly record struct Input(InputKind Kind, BuiltTask? Task = null, object? Payload = null)
{
    public static Input Start { get; } = new(InputKind.Start);

    public static Input ReportersDone { get; } = new(InputKind.ReportersDone);

    public static Input BodyEnded(BuiltTask task, TaskOutcome ended) => new(InputKind.BodyEnded, task, ended);

    public static Input WarningKept(BuiltTask task, string warning) => new(InputKind.WarningKept, task, warning);

    public static Input WaitOpened(BuiltTask task, string? text) => new(InputKind.WaitOpened, task, text);

    public static Input WaitClosed(BuiltTask task) => new(InputKind.WaitClosed, task);

    /// <summary>The result of a write: <paramref name="failure"/> is what it threw, or <see langword="null"/> when it landed.</summary>
    public static Input Written(Exception? failure) => new(InputKind.Written, Payload: failure);

    public TaskOutcome Outcome => (TaskOutcome)Payload!;

    public Exception? Failure => (Exception?)Payload;

    /// <summary>The text a task reported, for a report that carries one.</summary>
    public string? Text => (string?)Payload;
}
