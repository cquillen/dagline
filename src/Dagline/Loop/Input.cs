namespace Dagline;

/// <summary>The kinds of <see cref="Input"/>: a closed set, each answered by one case of <see cref="StepCore.Apply(in Input, DateTimeOffset)"/> or of the loop.</summary>
internal enum InputKind
{
    /// <summary>The run starts, with whether a drain and a stop were already asked for, which the core applies with the start.</summary>
    Start,

    /// <summary>A task's body has ended, with the outcome its end gives.</summary>
    BodyEnded,

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
/// What the input carries beyond its task: the <see cref="TaskOutcome"/> of <see cref="InputKind.BodyEnded"/>, or the failure of
/// <see cref="InputKind.Written"/>, which is <see langword="null"/> when the write landed, the <see cref="StopRequester"/> of
/// <see cref="InputKind.Stop"/>, or the <see cref="AskedBeforeTheStart"/> of a <see cref="InputKind.Start"/> that carries any.
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

    /// <summary>The result of a write: <paramref name="failure"/> is what it threw, or <see langword="null"/> when it landed.</summary>
    public static Input Written(Exception? failure) => new(InputKind.Written, Payload: failure);

    public TaskOutcome Outcome => (TaskOutcome)Payload!;

    public Exception? Failure => (Exception?)Payload;

    public StopRequester Requester => (StopRequester)Payload!;

    /// <summary>What was asked for before the start, for <see cref="InputKind.Start"/>: nothing, unless the start carries it.</summary>
    public AskedBeforeTheStart Asked => Payload as AskedBeforeTheStart ?? AskedBeforeTheStart.Nothing;
}

/// <summary>Whether a drain and a stop had been asked for when the run started.</summary>
internal sealed record AskedBeforeTheStart(bool Drained, bool Stopped)
{
    public static AskedBeforeTheStart Nothing { get; } = new(Drained: false, Stopped: false);
}
