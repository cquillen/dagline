namespace Dagline;

/// <summary>
/// What a settled task leaves behind: the status it settled with and the data that goes with that status.
/// </summary>
internal sealed record TaskOutcome
{
    private TaskOutcome(NodeStatus status) => Status = status;

    /// <summary>The outcome of a task whose body ended without a failure.</summary>
    public static TaskOutcome Succeeded { get; } = new(NodeStatus.Succeeded);

    public NodeStatus Status { get; }

    public FailureKind? Failure { get; private init; }

    public NodePath? BlockedBy { get; private init; }

    public string? Message { get; private init; }

    public IReadOnlyList<string> Detail { get; private init; } = [];

    /// <summary>
    /// The outcome of a task whose body let an exception escape: the exception's message, on one line, is the task's message,
    /// and its type, then each line of its stack trace, are the detail lines.
    /// </summary>
    public static TaskOutcome UnexpectedFailure(Exception exception) => new(NodeStatus.Failed)
    {
        Failure = FailureKind.Unexpected,
        Message = exception.Message.ReplaceLineEndings(" "),
        Detail = [exception.GetType().ToString(), .. StackTraceLines(exception)],
    };

    /// <summary>
    /// The outcome of a task that will not run because the task at <paramref name="cause"/> failed and this one requires it,
    /// directly, through a group or through a chain of blocked tasks.
    /// </summary>
    public static TaskOutcome Blocked(NodePath cause) => new(NodeStatus.Blocked) { BlockedBy = cause };

    private static string[] StackTraceLines(Exception exception)
        => exception.StackTrace?.ReplaceLineEndings("\n").Split('\n') ?? [];
}
