namespace Dagline;

/// <summary>
/// A run on the new engine as its tasks' contexts see it. What a task reports reaches the run either as an input posted to the
/// inbox, applied in the order the task made its calls, or as a sample the task overwrites in a place of its own (ADR 0015).
/// </summary>
/// <remarks>
/// A post returns at once, so what a task reports as an input shows in the run's current snapshot once the loop has applied it,
/// not when the call returns. A member not yet built throws <see cref="NotSupportedException"/>, so that a test run on the new
/// engine fails where it calls it, rather than passing over a report the run never applied.
/// </remarks>
/// <param name="inbox">The run's inbox, to which the inputs are posted.</param>
/// <param name="samples">The tasks' samples, which the reports that are samples overwrite.</param>
internal sealed class LoopTaskRun(Inbox inbox, TaskSamples samples) : ITaskRun
{
    // Each throw below is a departure from Liskov substitution for the length of the rebuild: the new engine is reachable only
    // from tests, and each member is built by a later part of it (checkpoints).
    public void SetStatusText(BuiltTask task, string? text) => throw NotYet(nameof(TaskContext.SetStatusText));

    public void SetCount(BuiltTask task, long current, long? total) => throw NotYet(nameof(TaskContext.SetCount));

    /// <summary>Posts a warning that is kept; one beyond the kept is only counted on the task's sample.</summary>
    public void Warn(BuiltTask task, string warning)
    {
        if (samples[task].KeepWarning())
        {
            inbox.Post(Input.WarningKept(task, warning));
        }
    }

    public void SetMessage(BuiltTask task, string message) => throw NotYet(nameof(TaskContext.SetMessage));

    public void AddDetail(BuiltTask task, IEnumerable<string> texts) => throw NotYet(nameof(TaskContext.AddDetail));

    public void Fail(BuiltTask task, string message, IEnumerable<string> detail) => throw NotYet(nameof(TaskContext.Fail));

    public bool OpenWaitingScope(BuiltTask task, string? text) => throw NotYet(nameof(TaskContext.BeginWaiting));

    public void CloseWaitingScope(BuiltTask task) => throw NotYet(nameof(TaskContext.BeginWaiting));

    public string CheckpointOf(BuiltTask task) => throw NotYet(nameof(TaskContext.Checkpoint));

    public Task SaveCheckpointAsync(BuiltTask task, string text) => throw NotYet(nameof(TaskContext.SaveCheckpointAsync));

    public NodeStatus StatusOf(BuiltTask task, Node dependency) => throw NotYet(nameof(TaskContext.StatusOf));

    private static NotSupportedException NotYet(string member) => new($"The new engine does not yet take {member} from a task.");
}
