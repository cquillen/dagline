using static System.FormattableString;

namespace Dagline;

/// <summary>The one home of how counts of tasks are written for people.</summary>
internal static class CountsText
{
    private static readonly NodeStatus[] _lifecycleOrder = Enum.GetValues<NodeStatus>();

    /// <summary>A number of tasks, as in <c>1 task</c> or <c>5 tasks</c>.</summary>
    public static string Tasks(int count) => Invariant($"{count} {Noun(count)}");

    /// <summary>A number of tasks of one kind, as in <c>1 running task</c> or <c>2 running tasks</c>.</summary>
    public static string Tasks(int count, string kind) => Invariant($"{count} {kind} {Noun(count)}");

    private static string Noun(int count) => count == 1 ? "task" : "tasks";

    /// <summary>
    /// A list of counts, as in <c>1 running, 2 succeeded</c>: each status that has at least one task, in lifecycle order.
    /// When no status has a task it reads <c>no tasks</c>.
    /// </summary>
    /// <param name="counts">How many tasks have each status. A status that is absent counts as zero.</param>
    public static string PerStatus(IReadOnlyDictionary<NodeStatus, int> counts)
    {
        string[] present =
        [
            .. _lifecycleOrder
                .Select(status => (Status: status, Count: counts.GetValueOrDefault(status)))
                .Where(entry => entry.Count > 0)
                .Select(entry => Invariant($"{entry.Count} {NameText.Of(entry.Status)}")),
        ];

        return present.Length == 0 ? "no tasks" : string.Join(", ", present);
    }
}
