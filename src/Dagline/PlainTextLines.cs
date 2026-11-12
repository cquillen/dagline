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
    private const string _journalWriteFailed = "journal write failed";

    /// <summary>
    /// The lines about one event: none for an event that is not printed, and several for a failure with detail lines
    /// and for the end of the run, which the summary follows.
    /// </summary>
    /// <param name="runEvent">The event. A task that becomes blocked is not handled here: see <see cref="Blocked"/>.</param>
    /// <param name="snapshot">The run as of the event.</param>
    public static IReadOnlyList<string> About(RunEvent runEvent, Snapshot snapshot) => runEvent switch
    {
        RunStarted =>
        [
            At(runEvent.At, $"run started: {string.Join(", ", Starting(snapshot))}"),
            .. snapshot.Journal is { } journal ? [At(runEvent.At, $"journal {Named(journal)}")] : Array.Empty<string>(),
        ],
        TaskStatusChanged { To: NodeStatus.Running } changed => [At(runEvent.At, $"started {changed.Node.Path}")],
        TaskStatusChanged { To: NodeStatus.Succeeded or NodeStatus.Cancelled } changed => [At(runEvent.At, Settled(changed.Node))],
        TaskStatusChanged { To: NodeStatus.Failed } changed =>
        [
            At(runEvent.At, Settled(changed.Node)),
            .. Kept(changed.Node.Detail, changed.Node.DetailOmitted, _detailIndent),
        ],
        WarningAdded added => [At(runEvent.At, $"warning {added.Path}: {added.Warning}")],
        WaitingChanged { Waiting: true } began => [At(runEvent.At, Began(began))],
        WaitingChanged ended => [At(runEvent.At, $"waited {ended.Path} for {DurationText.Of(ended.At - ended.Since)}")],
        StopRequested requested => [At(runEvent.At, Requested(requested, Running(snapshot)))],
        StoppedWaiting stopped =>
        [
            At(runEvent.At, $"stopped waiting: {Why(stopped.Reason)}, {CountsText.Tasks(Running(snapshot))} still running"),
        ],
        GroupSettled settled =>
        [
            At(
                runEvent.At,
                $"group {settled.Path} {NameText.Of(settled.Status)}: {CountsText.PerStatus(settled.Counts)}"),
        ],
        RunEnded ended => [At(runEvent.At, Ended(ended, ended.At - snapshot.Started)), .. Summary(snapshot)],
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

    private static int Running(Snapshot snapshot) => snapshot.Counts[NodeStatus.Running];

    /// <summary>The parts of the <c>run started</c> line: the number of tasks, then each count of what the journal gave that is not zero.</summary>
    private static IEnumerable<string> Starting(Snapshot snapshot)
    {
        yield return CountsText.Tasks(snapshot.Counts.Total);

        if (snapshot.Counts.EarlierSuccesses > 0)
        {
            yield return CountsText.Of(snapshot.Counts.EarlierSuccesses, "earlier success", "earlier successes");
        }

        if (snapshot.SetAside.Count > 0)
        {
            yield return Invariant($"{snapshot.SetAside.Count} set aside");
        }

        if (snapshot.JournalPathsNotInGraph > 0)
        {
            var paths = CountsText.Of(snapshot.JournalPathsNotInGraph, "journal path", "journal paths");
            yield return $"{paths} not in graph {NotSucceeded(snapshot)}";
        }
    }

    private static string NotSucceeded(Snapshot snapshot) => Invariant($"({snapshot.JournalPathsNotInGraphUnsucceeded} not succeeded)");

    /// <summary>The journal as the line after <c>run started</c> and the summary name it: its id, the run, and its description if it has one.</summary>
    private static string Named(JournalInfo journal)
        => journal.Description is { Length: > 0 } description
            // A description is any text, and here it has one line to fit on.
            ? Invariant($"{journal.Id} run {journal.Run}: {LineBreaks.OneLine(description)}")
            : Invariant($"{journal.Id} run {journal.Run}");

    private static string Ended(RunEnded ended, TimeSpan duration)
        => ended.Outcome is { } outcome
            ? $"run ended: {NameText.Of(outcome)} in {DurationText.Of(duration)}"
            // The error is a JournalWriteException, whose own message only repeats what the line already says.
            : $"run ended: {_journalWriteFailed} after {DurationText.Of(duration)}: {Cause(ended.Error!).Message}";

    private static Exception Cause(Exception error) => error.InnerException ?? error;

    private static string Began(WaitingChanged began)
        => began.Text is { } text
            ? $"waiting {began.Path}: {text}"
            : $"waiting {began.Path}";

    private static string Requested(StopRequested requested, int running)
    {
        var heading = requested.By switch
        {
            StopRequester.App => "stop requested",
            StopRequester.Dagline => $"stop requested by Dagline, {_journalWriteFailed}",
            _ => throw new ArgumentOutOfRangeException(nameof(requested), requested.By, null),
        };

        return GracePeriodRules.IsUnlimited(requested.GracePeriod)
            ? $"{heading}: waiting for {CountsText.Tasks(running, "running")}"
            : $"{heading}: waiting up to {DurationText.Of(requested.GracePeriod)} for {CountsText.Tasks(running, "running")}";
    }

    private static string Why(StoppedWaitingReason reason) => reason switch
    {
        StoppedWaitingReason.SecondStop => "second stop",
        StoppedWaitingReason.GracePeriodOver => "grace period over",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    private static string Settled(NodeSnapshot task)
    {
        var unexpected = task.Failure is FailureKind.Unexpected ? $" ({NameText.Of(FailureKind.Unexpected)})" : "";
        var message = task.Message is { } set ? $": {set}" : "";

        return $"{NameText.Of(task.Status)} {task.Path} in {Ran(task)}{unexpected}{message}";
    }

    private static string Ran(NodeSnapshot task) => DurationText.Of(task.Ran ?? TimeSpan.Zero);

    private static IEnumerable<string> Summary(Snapshot snapshot)
    {
        return
        [
            .. snapshot.Journal is { } journal ? [$"journal: {Named(journal)}"] : Array.Empty<string>(),
            Counts(snapshot),
            .. Section("failed", snapshot.Failed, task => Entry(task, snapshot.TryGetBlocked(task.Path, out var blocked) ? blocked : null)),
            .. Section("cancelled", snapshot.Cancelled, task => Entry(task, blocked: null)),
            .. Warnings(snapshot),
            .. LeftForALaterRun(snapshot),
            .. Section("set aside", snapshot.SetAside, path => [$"  {path}"]),
            .. snapshot.JournalPathsNotInGraph > 0
                ? [Invariant($"journal paths not in graph: {snapshot.JournalPathsNotInGraph} {NotSucceeded(snapshot)}")]
                : Array.Empty<string>(),
        ];
    }

    /// <summary>A heading with the number of entries, then each entry's lines. Nothing at all when there are no entries.</summary>
    private static IEnumerable<string> Section<T>(string heading, IReadOnlyList<T> entries, Func<T, IEnumerable<string>> lines)
        => entries.Count == 0 ? [] : [Invariant($"{heading}: {entries.Count}"), .. entries.SelectMany(lines)];

    /// <summary>
    /// How many tasks have not succeeded, which a later run of the journal has left to do. Nothing for a run with no journal,
    /// which no later run continues, and nothing when every task succeeded.
    /// </summary>
    private static IEnumerable<string> LeftForALaterRun(Snapshot snapshot)
    {
        var left = snapshot.Counts.Unsucceeded;
        return snapshot.Journal is null || left == 0 ? [] : [$"left for a later run: {CountsText.Tasks(left)}"];
    }

    /// <summary>
    /// A failed or cancelled task in the summary: the kind of failure if it failed, then when it settled, how long it ran,
    /// its progress, message and detail lines, and what it blocked.
    /// </summary>
    private static IEnumerable<string> Entry(NodeSnapshot task, BlockedTasks? blocked)
    {
        var kind = task.Failure is { } failure ? $" ({NameText.Of(failure)})" : "";
        var settled = task.Settled is { } at ? $" at {Timestamp(at)}" : "";
        yield return $"  {task.Path}{kind}{settled} after {Ran(task)}";

        if (Progress(task) is { } progress)
        {
            yield return $"    progress: {progress}";
        }

        if (task.Message is { } message)
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

        if (task.StatusText is not { } text)
        {
            return count;
        }

        return count is null ? text : $"{text}, {count}";
    }

    /// <summary>
    /// The count of every warning in the graph, then the warnings added in this run under their tasks, then one line for those of
    /// earlier successes, which an earlier run's summary listed. Nothing at all when there are no warnings.
    /// </summary>
    private static IEnumerable<string> Warnings(Snapshot snapshot)
    {
        if (snapshot.WithWarnings.Count == 0)
        {
            return [];
        }

        var earlier = snapshot.WithWarnings.Where(task => task.IsEarlierSuccess).ToList();

        return
        [
            Invariant($"warnings: {snapshot.WarningCount}"),
            .. snapshot.WithWarnings.Where(task => !task.IsEarlierSuccess).SelectMany(task => (IEnumerable<string>)
            [
                $"  {task.Path}",
                .. Kept(task.Warnings, task.WarningsOmitted, _summaryWarningIndent),
            ]),
            .. earlier.Count == 0
                ? Array.Empty<string>()
                :
                [
                    Invariant(
                        $"  from earlier runs, not listed again: {earlier.Sum(task => task.WarningCount)} on {CountsText.Tasks(earlier.Count)}"),
                ],
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
