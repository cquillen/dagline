namespace Dagline.Testing;

/// <summary>What a task left behind at the end of one run of a <see cref="TaskHarness"/>.</summary>
/// <remarks>
/// It is read from the task's <see cref="NodeSnapshot"/> in the run's final snapshot, and holds only the lines that were kept:
/// past the first 100 detail lines or warnings, the rest are counted by the run but not carried here. It has no times and no
/// checkpoint.
/// </remarks>
/// <param name="Status">
/// The status the task ended the run with: <see cref="NodeStatus.Succeeded"/>, <see cref="NodeStatus.Failed"/> or
/// <see cref="NodeStatus.Cancelled"/>.
/// </param>
/// <param name="Failure">
/// The kind of failure when <paramref name="Status"/> is <see cref="NodeStatus.Failed"/>, otherwise <see langword="null"/>.
/// <see cref="FailureKind.Reported"/> is a failure the body reported through <see cref="TaskContext.Fail"/>;
/// <see cref="FailureKind.Unexpected"/> is an exception that escaped the body.
/// </param>
/// <param name="Message">
/// The task's one-line message, or <see langword="null"/> when it has none. For an unexpected failure it is the exception's
/// message.
/// </param>
/// <param name="Detail">
/// The detail lines kept, in the order they were added. For an unexpected failure they include the exception's type name and its
/// stack trace.
/// </param>
/// <param name="Warnings">The warnings kept, in the order they were added, whatever status the task ended with.</param>
/// <param name="Waited">
/// <see langword="true"/> if the task began waiting at least once in the run, through <see cref="TaskContext.WaitUntilAsync"/> or
/// <see cref="TaskContext.BeginWaiting"/>.
/// </param>
public sealed record TaskOutcome(
    NodeStatus Status,
    FailureKind? Failure,
    string? Message,
    IReadOnlyList<string> Detail,
    IReadOnlyList<string> Warnings,
    bool Waited)
{
    internal static TaskOutcome From(NodeSnapshot task, bool waited) =>
        new(task.Status, task.Failure, task.Message, task.Detail, task.Warnings, waited);
}
