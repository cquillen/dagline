namespace Dagline;

/// <summary>
/// Where a node is in its life during a run. A status only moves forward:
/// pending to ready or blocked, ready to running, and running to succeeded, failed or cancelled.
/// </summary>
/// <remarks>
/// A task has exactly one status at a time. A node is <em>settled</em> once it is succeeded, failed, blocked or cancelled:
/// it will not change status again in this run.
/// </remarks>
public enum NodeStatus
{
    /// <summary>The task has a dependency that is not yet met, and has not been blocked.</summary>
    Pending,

    /// <summary>Every dependency of the task is met and it has not started yet.</summary>
    Ready,

    /// <summary>The task's work has started and not ended.</summary>
    Running,

    /// <summary>The task finished its work without a failure.</summary>
    Succeeded,

    /// <summary>The task's work ended in a failure.</summary>
    Failed,

    /// <summary>The task will not run in this run, because a node it requires failed or is itself blocked.</summary>
    Blocked,

    /// <summary>The task was running when the run was stopped.</summary>
    Cancelled,
}
