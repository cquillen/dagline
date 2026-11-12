namespace Dagline;

/// <summary>
/// How a task settled: the status it settled with and what goes with that status.
/// What the task reported about itself while it ran is already on the task, and stays.
/// </summary>
internal sealed record TaskOutcome
{
    private TaskOutcome(NodeStatus status) => Status = status;

    /// <summary>The outcome of a task whose body ended without a failure.</summary>
    public static TaskOutcome Succeeded { get; } = new(NodeStatus.Succeeded);

    /// <summary>
    /// The outcome of a task that recorded a reported failure. Its message and detail lines are those the task reported.
    /// </summary>
    public static TaskOutcome ReportedFailure { get; } = new(NodeStatus.Failed) { Failure = FailureKind.Reported };

    /// <summary>The outcome of a task whose body ended with a cancellation exception after the run was stopped.</summary>
    public static TaskOutcome Cancelled { get; } = new(NodeStatus.Cancelled);

    /// <summary>
    /// The outcome of a task still running when the run stopped waiting for it after a stop: cancelled, with a message that says so
    /// in place of any message the task set.
    /// </summary>
    public static TaskOutcome LeftBehind { get; }
        = new(NodeStatus.Cancelled) { ReplacingMessage = "still running when the run stopped waiting", IsLeftBehind = true };

    public NodeStatus Status { get; }

    public FailureKind? Failure { get; private init; }

    public NodePath? BlockedBy { get; private init; }

    /// <summary>
    /// <see langword="true"/> for <see cref="LeftBehind"/>: the run let go of the task, which records that over anything it reported.
    /// </summary>
    public bool IsLeftBehind { get; private init; }

    /// <summary><see langword="true"/> when the body ended with an <see cref="OperationCanceledException"/>, whichever token caused it.</summary>
    private bool EndedByCancellation { get; init; }

    /// <summary>The message that replaces the task's own, or <see langword="null"/> to keep the task's.</summary>
    public string? ReplacingMessage { get; private init; }

    /// <summary>The detail lines added after the task's own when <see cref="ReplacingMessage"/> replaces its message.</summary>
    public IReadOnlyList<string> AddedDetail { get; private init; } = [];

    /// <summary>
    /// The outcome of a task whose body let an exception escape: the exception's message becomes the task's message,
    /// and its type, then each line of its stack trace, are added to the task's detail lines.
    /// </summary>
    /// <remarks>
    /// The exception is read here, once, before anything about the run changes, and not again when the task settles.
    /// A message that cannot be read is replaced by the name of the exception's type, and a stack trace that cannot be read is left out.
    /// </remarks>
    public static TaskOutcome UnexpectedFailure(Exception exception)
    {
        var type = exception.GetType().ToString();

        return new(NodeStatus.Failed)
        {
            Failure = FailureKind.Unexpected,
            EndedByCancellation = exception is OperationCanceledException,
            ReplacingMessage = Read(exception, static escaped => escaped.Message) ?? type,
            AddedDetail = Read(exception, static escaped => escaped.StackTrace) is { } trace ? [type, trace] : [type],
        };
    }

    /// <summary>
    /// How a body that ended this way ends once the run has been stopped: a cancellation is cancelled,
    /// and anything else keeps its real outcome.
    /// </summary>
    public TaskOutcome OnceStopped() => EndedByCancellation ? Cancelled : this;

    /// <summary>
    /// The outcome of a task that will not run because the task at <paramref name="cause"/> failed and this one requires it,
    /// directly, through a group or through a chain of blocked tasks.
    /// </summary>
    public static TaskOutcome Blocked(NodePath cause) => new(NodeStatus.Blocked) { BlockedBy = cause };

    /// <returns>What the member gave, or <see langword="null"/> when it gave nothing or threw.</returns>
    private static string? Read(Exception exception, Func<Exception, string?> member)
    {
        // Message and StackTrace are virtual, so reading one runs code of the consumer's exception type.
        // Whatever that code throws stops here: it must not cost the task its outcome, or the run would never end.
        try
        {
            return member(exception);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
