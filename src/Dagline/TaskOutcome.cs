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

    public NodeStatus Status { get; }

    public FailureKind? Failure { get; private init; }

    public NodePath? BlockedBy { get; private init; }

    /// <summary><see langword="true"/> when the body ended with an <see cref="OperationCanceledException"/>, whichever token caused it.</summary>
    private bool EndedByCancellation { get; init; }

    private string? EscapedMessage { get; init; }

    private IReadOnlyList<string> EscapedDetail { get; init; } = [];

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
            EscapedMessage = Read(exception, static escaped => escaped.Message) ?? type,
            EscapedDetail = Read(exception, static escaped => escaped.StackTrace) is { } trace ? [type, trace] : [type],
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

    /// <summary>The task as it is once it has settled with this outcome.</summary>
    /// <param name="task">The task as it was just before it settled, with everything it reported.</param>
    /// <param name="now">When it settled.</param>
    public NodeSnapshot Settle(NodeSnapshot task, DateTimeOffset now)
    {
        var settled = task with { Status = Status, Settled = now, Failure = Failure, BlockedBy = BlockedBy };

        return EscapedMessage is null
            ? settled
            : settled.WithMessage(EscapedMessage).WithDetail(EscapedDetail);
    }

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
