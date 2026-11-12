namespace Dagline;

/// <summary>
/// The one home of each question asked of a status that only Dagline asks. Whether a status is settled is public, in
/// <see cref="NodeStatusExtensions"/>.
/// </summary>
internal static class NodeStatusRules
{
    public static bool Meets(this NodeStatus upstream, DependencyKind kind) => kind switch
    {
        DependencyKind.Requires => upstream is NodeStatus.Succeeded,
        DependencyKind.After => upstream.IsSettled(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>
    /// Whether a task may move from one status to another. A status only moves forward (ADR 0003): pending to ready or blocked,
    /// ready to running, running to succeeded, failed or cancelled. Nothing leaves a settled status.
    /// </summary>
    public static bool CanMoveTo(this NodeStatus from, NodeStatus to) => (from, to) switch
    {
        (NodeStatus.Pending, NodeStatus.Ready or NodeStatus.Blocked) => true,
        (NodeStatus.Ready, NodeStatus.Running) => true,
        (NodeStatus.Running, NodeStatus.Succeeded or NodeStatus.Failed or NodeStatus.Cancelled) => true,
        _ => false,
    };
}
