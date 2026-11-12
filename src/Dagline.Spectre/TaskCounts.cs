namespace Dagline.Spectre;

/// <summary>The one home of the sums the display and the summary make of a snapshot's counts.</summary>
internal static class TaskCounts
{
    /// <summary>How many tasks <paramref name="counts"/> counts, whatever their status.</summary>
    public static int Total(IReadOnlyDictionary<NodeStatus, int> counts) => counts.Values.Sum();

    /// <summary>How many of the tasks <paramref name="counts"/> counts have settled: succeeded, failed, blocked or cancelled.</summary>
    public static int Settled(IReadOnlyDictionary<NodeStatus, int> counts)
        => counts.Where(count => count.Key.IsSettled()).Sum(count => count.Value);
}
