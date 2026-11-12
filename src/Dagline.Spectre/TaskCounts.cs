namespace Dagline.Spectre;

/// <summary>The one home of the sums the display and the summary make of a snapshot's counts.</summary>
internal static class TaskCounts
{
    /// <summary>How many tasks <paramref name="counts"/> counts, whatever their status.</summary>
    public static int Total(IReadOnlyDictionary<NodeStatus, int> counts) => counts.Values.Sum();

    /// <summary>How many of the tasks <paramref name="counts"/> counts have settled: succeeded, failed, blocked or cancelled.</summary>
    public static int Settled(IReadOnlyDictionary<NodeStatus, int> counts)
        => counts.Where(count => IsSettled(count.Key)).Sum(count => count.Value);

    /// <summary>Every warning <paramref name="task"/> was given, kept or not.</summary>
    public static int Warnings(NodeSnapshot task) => task.Warnings.Count + task.WarningsOmitted;

    // The core's own rule is internal to Dagline and this package must not change the core, so it is written again here,
    // once for the whole package. Nothing leaves a settled status, so the set is fixed (ADR 0003).
    private static bool IsSettled(NodeStatus status)
        => status is NodeStatus.Succeeded or NodeStatus.Failed or NodeStatus.Blocked or NodeStatus.Cancelled;
}
