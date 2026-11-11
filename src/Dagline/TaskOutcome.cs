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

    public NodeStatus Status { get; }

    public FailureKind? Failure { get; private init; }

    public NodePath? BlockedBy { get; private init; }

    private Exception? Escaped { get; init; }

    /// <summary>
    /// The outcome of a task whose body let an exception escape: the exception's message becomes the task's message,
    /// and its type, then each line of its stack trace, are added to the task's detail lines.
    /// </summary>
    public static TaskOutcome UnexpectedFailure(Exception exception) => new(NodeStatus.Failed)
    {
        Failure = FailureKind.Unexpected,
        Escaped = exception,
    };

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

        return Escaped is null
            ? settled
            : settled.WithMessage(Escaped.Message).WithDetail([Escaped.GetType().ToString(), .. StackTrace(Escaped)]);
    }

    private static string[] StackTrace(Exception exception) => exception.StackTrace is { } trace ? [trace] : [];
}
