using static System.FormattableString;

namespace Dagline;

/// <summary>The one home of how counts of tasks are written for people.</summary>
internal static class CountsText
{
    private static readonly NodeStatus[] _lifecycleOrder = Enum.GetValues<NodeStatus>();

    /// <summary>A number of tasks, as in <c>1 task</c> or <c>5 tasks</c>.</summary>
    public static string Tasks(int count) => Of(count, "task", "tasks");

    /// <summary>A number of tasks of one kind, as in <c>1 running task</c> or <c>2 running tasks</c>.</summary>
    public static string Tasks(int count, string kind) => Of(count, $"{kind} task", $"{kind} tasks");

    /// <summary>A number of anything, singular for one, as in <c>1 earlier success</c> or <c>2 earlier successes</c>.</summary>
    public static string Of(int count, string singular, string plural) => Invariant($"{count} {(count == 1 ? singular : plural)}");

    /// <summary>
    /// A list of counts, as in <c>1 running, 2 succeeded</c>: each status that has at least one task, in lifecycle order.
    /// When no status has a task it reads <c>no tasks</c>. Earlier successes, which are among the succeeded, follow that count in
    /// brackets, as in <c>3 succeeded (1 earlier)</c>, when there are any.
    /// </summary>
    /// <param name="counts">How many tasks have each status. A status that is absent counts as zero.</param>
    /// <param name="earlierSuccesses">How many of the succeeded are earlier successes.</param>
    public static string PerStatus(IReadOnlyDictionary<NodeStatus, int> counts, int earlierSuccesses)
    {
        string[] present =
        [
            .. _lifecycleOrder
                .Select(status => (Status: status, Count: counts.GetValueOrDefault(status)))
                .Where(entry => entry.Count > 0)
                .Select(entry => Invariant($"{entry.Count} {NameText.Of(entry.Status)}{Earlier(entry.Status, earlierSuccesses)}")),
        ];

        return present.Length == 0 ? "no tasks" : string.Join(", ", present);
    }

    private static string Earlier(NodeStatus status, int earlierSuccesses)
        => status is NodeStatus.Succeeded && earlierSuccesses > 0 ? Invariant($" ({earlierSuccesses} earlier)") : "";
}
