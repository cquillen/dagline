namespace Dagline;

/// <summary>The kinds of <see cref="Input"/>: a closed set, each answered by one case of <see cref="StepCore.Apply(in Input, DateTimeOffset)"/> or of the loop.</summary>
internal enum InputKind
{
    /// <summary>The run starts, with whether a drain and a stop were already asked for, which the core applies with the start.</summary>
    Start,

    /// <summary>A task's body has ended, with the outcome its end gives.</summary>
    BodyEnded,

    /// <summary>A task added a warning that is kept: one of its first <see cref="TaskSample.Kept"/>, which its sample counts.</summary>
    WarningKept,

    /// <summary>A task opened a waiting scope, with its text. Always posted: the core ignores it for a task that is not running.</summary>
    WaitOpened,

    /// <summary>A task closed a waiting scope.</summary>
    WaitClosed,

    /// <summary>A task set its message.</summary>
    MessageSet,

    /// <summary>A task added detail lines that are kept, each one line, which its sample counted.</summary>
    DetailKept,

    /// <summary>A task reported a failure, with its message and the detail lines of it that are kept.</summary>
    FailureReported,

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

    /// <summary>The app asks the run to drain: start nothing new, and let the running tasks finish untold.</summary>
    Drain,

    /// <summary>A stop is asked for, by the requester the input carries: start nothing new, signal the tasks, wait the grace period.</summary>
    Stop,

    /// <summary>The grace period of the stop has run out: the run lets go of the tasks still running.</summary>
    GracePeriodOver,
}

/// <summary>
/// One thing that happened to a run, as it sits in the run's <see cref="Inbox"/>. A struct, so that a post allocates nothing
/// beyond what it carries.
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="Task">The task it happened to, for an input about a task.</param>
/// <param name="Payload">
/// What the input carries beyond its task: the <see cref="TaskOutcome"/> of <see cref="InputKind.BodyEnded"/>, the text of a
/// task's report, the failure of <see cref="InputKind.Written"/>, which is <see langword="null"/> when the write landed, the
/// <see cref="StopRequester"/> of <see cref="InputKind.Stop"/>, or the <see cref="AskedBeforeTheStart"/> of a
/// <see cref="InputKind.Start"/> that carries any.
/// </param>
internal readonly record struct Input(InputKind Kind, BuiltTask? Task = null, object? Payload = null)
{
    public static Input Start { get; } = new(InputKind.Start);

    public static Input ReportersDone { get; } = new(InputKind.ReportersDone);

    public static Input Drain { get; } = new(InputKind.Drain);

    public static Input GracePeriodOver { get; } = new(InputKind.GracePeriodOver);

    /// <summary>
    /// The run starts with a drain or a stop, or both, already asked for: a token already cancelled, or a drain signal already
    /// requested, which reaches the core with the start rather than after it, when tasks would already have started.
    /// </summary>
    public static Input StartAsked(bool drained, bool stopped)
        => drained || stopped ? new(InputKind.Start, Payload: new AskedBeforeTheStart(drained, stopped)) : Start;

    public static Input Stop(StopRequester by) => new(InputKind.Stop, Payload: by);

    public static Input BodyEnded(BuiltTask task, TaskOutcome ended) => new(InputKind.BodyEnded, task, ended);

    public static Input WarningKept(BuiltTask task, string warning) => new(InputKind.WarningKept, task, warning);

    public static Input WaitOpened(BuiltTask task, string? text) => new(InputKind.WaitOpened, task, text);

    public static Input WaitClosed(BuiltTask task) => new(InputKind.WaitClosed, task);

    public static Input MessageSet(BuiltTask task, string message) => new(InputKind.MessageSet, task, message);

    public static Input DetailKept(BuiltTask task, string[] lines) => new(InputKind.DetailKept, task, lines);

    public static Input FailureReported(BuiltTask task, string message, string[] lines)
        => new(InputKind.FailureReported, task, new ReportedFailure(message, lines));

    /// <summary>The result of a write: <paramref name="failure"/> is what it threw, or <see langword="null"/> when it landed.</summary>
    public static Input Written(Exception? failure) => new(InputKind.Written, Payload: failure);

    public TaskOutcome Outcome => (TaskOutcome)Payload!;

    public Exception? Failure => (Exception?)Payload;

    /// <summary>
    /// Whether the loop reads the run's clock as it takes the input. A report that is never an event, the message, detail lines
    /// and a reported failure, carries no time, so a task that reports them often leaves the clock alone.
    /// </summary>
    public bool IsTimed => Kind is not (InputKind.MessageSet or InputKind.DetailKept or InputKind.FailureReported);

    /// <summary>The text a task reported, for a report that carries one.</summary>
    public string? Text => (string?)Payload;

    /// <summary>The detail lines of <see cref="InputKind.DetailKept"/>.</summary>
    public string[] Lines => (string[])Payload!;

    /// <summary>The failure of <see cref="InputKind.FailureReported"/>.</summary>
    public ReportedFailure Failed => (ReportedFailure)Payload!;

    public StopRequester Requester => (StopRequester)Payload!;

    /// <summary>What was asked for before the start, for <see cref="InputKind.Start"/>: nothing, unless the start carries it.</summary>
    public AskedBeforeTheStart Asked => Payload as AskedBeforeTheStart ?? AskedBeforeTheStart.Nothing;
}

/// <summary>Whether a drain and a stop had been asked for when the run started.</summary>
internal sealed record AskedBeforeTheStart(bool Drained, bool Stopped)
{
    public static AskedBeforeTheStart Nothing { get; } = new(Drained: false, Stopped: false);
}

/// <summary>A failure a task reported, as <see cref="InputKind.FailureReported"/> carries it.</summary>
/// <param name="Message">The message the failure replaces the task's with.</param>
/// <param name="Lines">The detail lines of the failure that are kept, each one line.</param>
internal sealed record ReportedFailure(string Message, string[] Lines);
