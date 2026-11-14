namespace Dagline.Testing;

/// <summary>What a task left behind at the end of one run of a <see cref="TaskHarness"/>.</summary>
/// <remarks>
/// <para>
/// Most of it is read from the task's <see cref="NodeSnapshot"/> in the run's final snapshot, and it holds only the lines that
/// were kept: past the first 100 detail lines or warnings, the rest are counted by the run but not carried here.
/// <see cref="Waited"/> and <see cref="WaitingText"/> come from the <see cref="WaitingChanged"/> events of the run, since a
/// settled task is never waiting, and <see cref="Checkpoint"/> from what the task body saw as it ended.
/// </para>
/// <para>It has no times, no count and no total.</para>
/// </remarks>
public sealed record TaskOutcome
{
    private TaskOutcome(NodeSnapshot task, bool waited, string? waitingText, string checkpoint)
    {
        Status = task.Status;
        Failure = task.Failure;
        Message = task.Message;
        Detail = task.Detail;
        Warnings = task.Warnings;
        StatusText = task.StatusText;
        Waited = waited;
        WaitingText = waitingText;
        Checkpoint = checkpoint;
    }

    /// <summary>
    /// The status the task ended the run with: <see cref="NodeStatus.Succeeded"/>, <see cref="NodeStatus.Failed"/> or
    /// <see cref="NodeStatus.Cancelled"/>.
    /// </summary>
    public NodeStatus Status { get; }

    /// <summary>
    /// The kind of failure when <see cref="Status"/> is <see cref="NodeStatus.Failed"/>, otherwise <see langword="null"/>.
    /// <see cref="FailureKind.Reported"/> is a failure the body reported through <see cref="TaskContext.Fail"/>;
    /// <see cref="FailureKind.Unexpected"/> is an exception that escaped the body.
    /// </summary>
    public FailureKind? Failure { get; }

    /// <summary>
    /// The task's one-line message, or <see langword="null"/> when it has none. For an unexpected failure it is the exception's
    /// message.
    /// </summary>
    public string? Message { get; }

    /// <summary>
    /// The detail lines kept, in the order they were added. For an unexpected failure they include the exception's type name and its
    /// stack trace.
    /// </summary>
    public IReadOnlyList<string> Detail { get; }

    /// <summary>The warnings kept, in the order they were added, whatever status the task ended with.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>
    /// The status text the operator last saw for the task: the line it last set with <see cref="TaskContext.SetStatusText"/>,
    /// frozen when it settled, as <see cref="NodeSnapshot.StatusText"/> holds it, line breaks included.
    /// <see langword="null"/> when the task set none or cleared it.
    /// </summary>
    public string? StatusText { get; }

    /// <summary>
    /// <see langword="true"/> if the task began waiting at least once in the run, through <see cref="TaskContext.WaitUntilAsync"/> or
    /// <see cref="TaskContext.BeginWaiting"/>.
    /// </summary>
    public bool Waited { get; }

    /// <summary>
    /// The text of the last wait the task began in the run, as the operator saw it: on one line, as
    /// <see cref="WaitingChanged.Text"/> gives it. It is kept after the wait has ended. <see langword="null"/> when the task
    /// began no wait in the run, or when the last wait it began gave no text or empty text. While waits overlap, a scope that
    /// opens inside a wait already begun does not change it.
    /// </summary>
    public string? WaitingText { get; }

    /// <summary>
    /// The task's checkpoint as the run left it: what a later run of the harness hands the task in
    /// <see cref="TaskContext.Checkpoint"/>. It is the text of the task's latest save, or the checkpoint it was handed when it
    /// saved none in the run, and empty when it has none or cleared it. It is empty once the task has succeeded, since a
    /// success discards the checkpoint, so it is empty for an earlier success too.
    /// </summary>
    public string Checkpoint { get; }

    internal static TaskOutcome From(NodeSnapshot task, bool waited, string? waitingText, string checkpoint)
        => new(task, waited, waitingText, checkpoint);
}
