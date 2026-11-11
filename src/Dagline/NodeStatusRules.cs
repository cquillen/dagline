namespace Dagline;

/// <summary>The one home of each question asked of a status.</summary>
internal static class NodeStatusRules
{
    public static bool IsSettled(this NodeStatus status)
        => status is NodeStatus.Succeeded or NodeStatus.Failed or NodeStatus.Blocked or NodeStatus.Cancelled;

    public static bool Meets(this NodeStatus upstream, DependencyKind kind) => kind switch
    {
        DependencyKind.Requires => upstream is NodeStatus.Succeeded,
        DependencyKind.After => upstream.IsSettled(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
