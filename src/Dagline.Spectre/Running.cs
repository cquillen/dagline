namespace Dagline.Spectre;

/// <summary>The one home of telling a running task that is working from one that is waiting on an external system.</summary>
internal static class Running
{
    /// <summary>Whether <paramref name="node"/> is a running task that is waiting. A task that is not running never waits.</summary>
    public static bool IsWaiting(NodeSnapshot node) => !node.IsGroup && node.WaitingSince is not null;

    /// <summary>Whether <paramref name="node"/> is a running task that is not waiting.</summary>
    public static bool IsWorking(NodeSnapshot node) => !node.IsGroup && node.Status == NodeStatus.Running && node.WaitingSince is null;
}
