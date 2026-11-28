namespace Dagline;

/// <summary>
/// The run as a task's context sees it: everything a <see cref="TaskContext"/> asks of the run that started its task. Each call
/// names the task it is for, so one run serves every context it creates. The context checks its arguments before it calls.
/// </summary>
internal interface ITaskRun
{
    /// <summary>Sets the status text of a task's progress.</summary>
    void SetStatusText(BuiltTask task, string? text);

    /// <summary>Sets the count of a task's progress, and the total it counts towards.</summary>
    void SetCount(BuiltTask task, long current, long? total);

    /// <summary>Adds a warning to a task.</summary>
    void Warn(BuiltTask task, string warning);

    /// <summary>Sets the message of a task's outcome.</summary>
    void SetMessage(BuiltTask task, string message);

    /// <summary>Adds detail lines to a task's outcome.</summary>
    void AddDetail(BuiltTask task, IEnumerable<string> texts);

    /// <summary>Records a reported failure: from then on the task settles failed as a reported failure, however its body ends.</summary>
    void Fail(BuiltTask task, string message, IEnumerable<string> detail);

    /// <summary>Opens a waiting scope of a task.</summary>
    /// <returns><see langword="true"/> when the scope was opened, so that closing it must be told to the run.</returns>
    bool OpenWaitingScope(BuiltTask task, string? text);

    /// <summary>Closes one waiting scope of a task.</summary>
    void CloseWaitingScope(BuiltTask task);

    /// <summary>The checkpoint of a task as it is now: empty when there is none.</summary>
    string CheckpointOf(BuiltTask task);

    /// <summary>
    /// Saves a task's checkpoint, or clears it when <paramref name="text"/> is empty. The returned task completes once the save
    /// is made, and faults with what refused it rather than throwing.
    /// </summary>
    Task SaveCheckpointAsync(BuiltTask task, string text);

    /// <summary>The status, as it is now, of a node that a task depends on.</summary>
    /// <exception cref="ArgumentException">The task does not depend on the node.</exception>
    NodeStatus StatusOf(BuiltTask task, Node dependency);
}
