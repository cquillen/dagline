namespace Dagline;

/// <summary>
/// A run on the new engine as its tasks' contexts see it. What a task reports reaches the run either as an input posted to the
/// inbox, applied in the order the task made its calls, or as a sample the task overwrites in a place of its own (ADR 0015).
/// </summary>
/// <remarks>
/// None of a task's reports is taken as yet: a chain of tasks whose bodies report nothing is all the new engine runs so far. Each
/// member throws <see cref="NotSupportedException"/> until it is built, so that a test run on the new engine that reports fails
/// where it reports, rather than passing over a report the run never applied.
/// </remarks>
internal sealed class LoopTaskRun : ITaskRun
{
    // Each throw below is a departure from Liskov substitution for the length of the rebuild: the new engine is reachable only
    // from tests, and each member is built by a later part of it (progress, reports and waits; checkpoints; a dependency's status).
    public void SetStatusText(BuiltTask task, string? text) => throw NotYet(nameof(TaskContext.SetStatusText));

    public void SetCount(BuiltTask task, long current, long? total) => throw NotYet(nameof(TaskContext.SetCount));

    public void Warn(BuiltTask task, string warning) => throw NotYet(nameof(TaskContext.AddWarning));

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
