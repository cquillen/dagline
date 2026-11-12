namespace Dagline;

/// <summary>What can be asked of a <see cref="NodeStatus"/>.</summary>
public static class NodeStatusExtensions
{
    /// <summary>
    /// Whether a status is settled: <see cref="NodeStatus.Succeeded"/>, <see cref="NodeStatus.Failed"/>,
    /// <see cref="NodeStatus.Blocked"/> or <see cref="NodeStatus.Cancelled"/>.
    /// </summary>
    /// <param name="status">The status of a task or a group.</param>
    /// <returns>
    /// <see langword="true"/> for a settled status, and <see langword="false"/> for <see cref="NodeStatus.Pending"/>,
    /// <see cref="NodeStatus.Ready"/>, <see cref="NodeStatus.Running"/> and any value that is not a defined status.
    /// </returns>
    /// <remarks>
    /// A status only moves forward, and nothing leaves a settled status: a task that has settled keeps its status for the rest
    /// of the run. A task that is still pending or ready when a stopped run ends never settles.
    /// </remarks>
    public static bool IsSettled(this NodeStatus status)
        => status is NodeStatus.Succeeded or NodeStatus.Failed or NodeStatus.Blocked or NodeStatus.Cancelled;
}
