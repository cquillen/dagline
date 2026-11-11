using System.Globalization;

namespace Dagline;

/// <summary>The one home of how counts of tasks are written for people.</summary>
internal static class CountsText
{
    private static readonly NodeStatus[] _lifecycleOrder = Enum.GetValues<NodeStatus>();

    /// <summary>A number of tasks, as in <c>1 task</c> or <c>5 tasks</c>.</summary>
    public static string Tasks(int count)
        => string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? "task" : "tasks")}");

    /// <summary>
    /// A list of counts, as in <c>1 running, 2 succeeded</c>: each status that has at least one task, in lifecycle order.
    /// </summary>
    public static string PerStatus(IReadOnlyDictionary<NodeStatus, int> counts)
        => string.Join(
            ", ",
            _lifecycleOrder
                .Select(status => (Status: status, Count: counts.GetValueOrDefault(status)))
                .Where(entry => entry.Count > 0)
                .Select(entry => string.Create(CultureInfo.InvariantCulture, $"{entry.Count} {Name(entry.Status)}")));

    /// <summary>A status in lower case, as in <c>succeeded</c>.</summary>
    public static string Name(NodeStatus status) => status.ToString().ToLowerInvariant();
}
