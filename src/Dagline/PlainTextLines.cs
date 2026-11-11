using System.Globalization;
using static System.FormattableString;

namespace Dagline;

/// <summary>
/// The one home of every line the plain-text reporter prints: what is said about each event, and the summary.
/// When a line is printed is the reporter's business.
/// </summary>
internal static class PlainTextLines
{
    private const string _timestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";
    private const string _detailIndent = "    ";
    private const string _summaryDetailIndent = "      ";
    private const string _summaryWarningIndent = "    ";

    /// <summary>
    /// The lines about one event: none for an event that is not printed, and several for a failure with detail lines
    /// and for the end of the run, which the summary follows.
    /// </summary>
    /// <param name="runEvent">The event. A task that becomes blocked is not handled here: see <see cref="Blocked"/>.</param>
    /// <param name="snapshot">The run as of the event.</param>
    public static IReadOnlyList<string> About(RunEvent runEvent, Snapshot snapshot) => runEvent switch
    {
        RunStarted => [At(runEvent.At, $"run started: {CountsText.Tasks(snapshot.Counts.Values.Sum())}")],
        TaskStatusChanged { To: NodeStatus.Running } changed => [At(runEvent.At, $"started {changed.Node.Path}")],
        TaskStatusChanged { To: NodeStatus.Succeeded } changed => [At(runEvent.At, Settled(changed.Node))],
        TaskStatusChanged { To: NodeStatus.Failed } changed =>
        [
            At(runEvent.At, Settled(changed.Node)),
            .. Kept(changed.Node.Detail, changed.Node.DetailOmitted, _detailIndent),
        ],
        WarningAdded added => [At(runEvent.At, $"warning {added.Path}: {added.Warning}")],
        GroupSettled settled =>
        [
            At(runEvent.At, $"group {settled.Path} {NameText.Of(settled.Status)}: {CountsText.PerStatus(settled.Counts)}"),
        ],
        RunEnded ended =>
        [
            At(runEvent.At, $"run ended: {NameText.Of(ended.Outcome)} in {DurationText.Of(ended.At - snapshot.Started)}"),
            .. Summary(snapshot),
        ],
        _ => [],
    };

    /// <summary>The one line for a streak of consecutive blocked events with one cause.</summary>
    /// <param name="at">The time of the events.</param>
    /// <param name="tasks">How many tasks were blocked.</param>
    /// <param name="cause">The path of the failed task that blocked them.</param>
    public static string Blocked(DateTimeOffset at, int tasks, NodePath cause)
        => At(at, $"blocked {CountsText.Tasks(tasks)} by {cause}");

    /// <summary>The line of counts printed when nothing else has been for a while.</summary>
    /// <param name="at">The time the line is printed.</param>
    /// <param name="snapshot">The run as it is now.</param>
    public static string Idle(DateTimeOffset at, Snapshot snapshot) => At(at, Counts(snapshot));

    private static string At(DateTimeOffset at, string text) => $"{Timestamp(at)} {text}";

    private static string Timestamp(DateTimeOffset at) => at.UtcDateTime.ToString(_timestampFormat, CultureInfo.InvariantCulture);

    private static string Counts(Snapshot snapshot) => $"tasks: {CountsText.PerStatus(snapshot.Counts)}";

    private static string Settled(NodeSnapshot task)
    {
        var unexpected = task.Failure is FailureKind.Unexpected ? $" ({NameText.Of(FailureKind.Unexpected)})" : "";
        var message = task.Message is { Length: > 0 } set ? $": {set}" : "";

        return $"{NameText.Of(task.Status)} {task.Path} in {Ran(task)}{unexpected}{message}";
    }

    private static string Ran(NodeSnapshot task) => DurationText.Of((task.Settled - task.Started) ?? TimeSpan.Zero);

    private static IEnumerable<string> Summary(Snapshot snapshot)
        => [Counts(snapshot), .. Failures(snapshot), .. Warnings(snapshot)];

    private static IEnumerable<string> Failures(Snapshot snapshot)
    {
        if (snapshot.Failed.Count == 0)
        {
            return [];
        }

        var blockedBy = snapshot.Blocked.ToDictionary(blocked => blocked.Cause);

        return
        [
            Invariant($"failed: {snapshot.Failed.Count}"),
            .. snapshot.Failed.SelectMany(task => Failure(task, blockedBy.GetValueOrDefault(task.Path))),
        ];
    }

    private static IEnumerable<string> Failure(NodeSnapshot task, BlockedTasks? blocked)
    {
        var kind = task.Failure is { } failure ? $" ({NameText.Of(failure)})" : "";
        var settled = task.Settled is { } at ? $" at {Timestamp(at)}" : "";
        yield return $"  {task.Path}{kind}{settled} after {Ran(task)}";

        if (Progress(task) is { } progress)
        {
            yield return $"    progress: {progress}";
        }

        if (task.Message is { Length: > 0 } message)
        {
            yield return $"    message: {message}";
        }

        if (task.Detail.Count > 0)
        {
            yield return "    detail:";
            foreach (var line in Kept(task.Detail, task.DetailOmitted, _summaryDetailIndent))
            {
                yield return line;
            }
        }

        if (blocked is not null)
        {
            var roots = string.Join(", ", blocked.Roots.Select(root => root.Path));
            yield return $"    blocked {CountsText.Tasks(blocked.Tasks.Count)}: {roots}";
        }
    }

    /// <summary>The status text, the count, or both joined by a comma. <see langword="null"/> when the task reported neither.</summary>
    private static string? Progress(NodeSnapshot task)
    {
        var count = task switch
        {
            { Count: { } current, Total: { } total } => Invariant($"{current} of {total}"),
            { Count: { } current } => Invariant($"{current}"),
            _ => null,
        };

        if (task.StatusText is not { Length: > 0 } given)
        {
            return count;
        }

        // Status text is kept as the task gave it, line breaks included, and here it has one line to fit on.
        var text = TaskReportRules.OneLine(given);
        return count is null ? text : $"{text}, {count}";
    }

    private static IEnumerable<string> Warnings(Snapshot snapshot)
    {
        if (snapshot.WithWarnings.Count == 0)
        {
            return [];
        }

        return
        [
            Invariant($"warnings: {snapshot.WarningCount}"),
            .. snapshot.WithWarnings.SelectMany(task => (IEnumerable<string>)
            [
                $"  {task.Path}",
                .. Kept(task.Warnings, task.WarningsOmitted, _summaryWarningIndent),
            ]),
        ];
    }

    /// <summary>The lines that were kept, then how many were not, all at one indent.</summary>
    private static IEnumerable<string> Kept(IReadOnlyList<string> lines, int omitted, string indent)
    {
        foreach (var line in lines)
        {
            yield return indent + line;
        }

        if (omitted > 0)
        {
            yield return Invariant($"{indent}and {omitted} more");
        }
    }
}
