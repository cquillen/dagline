namespace Dagline.Spectre;

/// <summary>
/// Builds the summary the display leaves on the ordinary screen once the run has ended: the same content as the plain-text
/// summary, in the display's own wording, glyphs and colours. Its lines are not cut to the width, so that nothing in it is lost.
/// </summary>
internal static class Summary
{
    /// <summary>
    /// The summary of a run that has ended: the rule, the outcome and the counts, then each part that has something to show,
    /// each after a blank line.
    /// </summary>
    /// <param name="ended">The last event of the run, which carries its run outcome or its error.</param>
    /// <param name="snapshot">The final snapshot, handed with <paramref name="ended"/>.</param>
    /// <param name="width">The width of the console, which the rule above the summary fills.</param>
    /// <param name="glyphs">The glyphs to draw with.</param>
    public static IReadOnlyList<StyledLine> Lines(RunEnded ended, Snapshot snapshot, int width, Glyphs glyphs) =>
    [
        StyledLine.Of(DisplayText.Repeat(glyphs.Rule, Math.Max(1, width)), Palette.Dim),
        Outcome(ended, snapshot, glyphs),
        // A run ends with an error only when its journal can no longer be written, and the exception's message says why.
        .. ended.Error is { } error ? [StyledLine.Of(DisplayText.Indent + glyphs.Text(error.Message), Palette.Failed)] : Array.Empty<StyledLine>(),
        Tally.Long(snapshot, glyphs),
        .. NotReached(snapshot),
        .. Part(Failures(snapshot, glyphs)),
        .. Part(Cancelled(snapshot, glyphs)),
        .. Part(Warnings(snapshot, glyphs)),
        .. Part(ForALaterRun(snapshot)),
    ];

    private static StyledLine Outcome(RunEnded ended, Snapshot snapshot, Glyphs glyphs)
    {
        var line = ended.Outcome switch
        {
            RunOutcome.Complete => StyledLine.Of("Run complete", Palette.Succeeded),
            RunOutcome.Incomplete => StyledLine.Of("Run incomplete", Palette.Failed),
            RunOutcome.Stopped => StyledLine.Of("Run stopped", Palette.Cancelled),
            _ => StyledLine.Of("Run ended with an error", Palette.Failed),
        };

        line = line.Add(DisplayText.Between + DisplayText.Duration((snapshot.Ended ?? ended.At) - snapshot.Started), Palette.Bold);
        return snapshot.Journal is { } journal
            ? line.Add($" {glyphs.Separator} journal {journal.Id} {glyphs.Separator} run {DisplayText.Number(journal.Run)}", Palette.Dim)
            : line;
    }

    /// <summary>A part after the blank line that sets it apart, or nothing at all when it has no lines.</summary>
    private static IEnumerable<StyledLine> Part(IReadOnlyList<StyledLine> lines) => lines.Count == 0 ? [] : [StyledLine.Empty, .. lines];

    private static StyledLine Heading(string title, int entries)
        => StyledLine.Of($"{title} ({DisplayText.Number(entries)})", Palette.Bold);

    private static IEnumerable<StyledLine> NotReached(Snapshot snapshot)
    {
        var tasks = snapshot.NeverReached.Count;
        return tasks == 0
            ? []
            : [StyledLine.Of($"{DisplayText.Count(tasks, "task was", "tasks were")} not reached", Palette.Dim)];
    }

    private static IReadOnlyList<StyledLine> Cancelled(Snapshot snapshot, Glyphs glyphs)
        => snapshot.Cancelled.Count == 0
            ? []
            :
            [
                Heading("Cancelled", snapshot.Cancelled.Count),
                .. snapshot.Cancelled.Select(task => StatusMark.For(NodeStatus.Cancelled, glyphs).Line(" ")
                    .Add(task.Path.ToString())
                    .Add(task.Message is { } message ? DisplayText.Between + glyphs.Text(message) : "", Palette.Dim)),
            ];

    /// <summary>
    /// The total and how many tasks have any, then each task with warnings added in this run, with those it kept beneath it,
    /// then one line for the warnings of earlier successes, which an earlier run's summary listed.
    /// </summary>
    private static IReadOnlyList<StyledLine> Warnings(Snapshot snapshot, Glyphs glyphs)
    {
        if (snapshot.WithWarnings.Count == 0)
        {
            return [];
        }

        var earlier = snapshot.WithWarnings.Where(task => task.IsEarlierSuccess).ToList();
        var title = $"Warnings ({DisplayText.Number(snapshot.WarningCount)} on {DisplayText.Count(snapshot.WithWarnings.Count, "task", "tasks")})";

        return
        [
            StyledLine.Of(title, Palette.Bold),
            .. snapshot.WithWarnings.Where(task => !task.IsEarlierSuccess).SelectMany(task => (IEnumerable<StyledLine>)
            [
                StatusMark.Warning(glyphs).Line(" ")
                    .Add(task.Path.ToString())
                    .Add(DisplayText.Between + DisplayText.Number(task.WarningCount), Palette.Warning),
                .. Kept(task.Warnings, task.WarningsOmitted, "warning", "warnings", glyphs),
            ]),
            .. earlier.Count == 0
                ? Array.Empty<StyledLine>()
                :
                [
                    StatusMark.Warning(glyphs).Line(" ").Add(
                        $"{DisplayText.Count(earlier.Sum(task => task.WarningCount), "warning", "warnings")} on "
                        + $"{DisplayText.Count(earlier.Count, "task", "tasks")} from earlier runs, not listed again",
                        Palette.Dim),
                ],
        ];
    }

    /// <summary>
    /// For a run with a journal: how many tasks a run that continues it has left, the earlier successes set aside, and the
    /// paths in the journal that are not in the graph, each only when there is something to say.
    /// </summary>
    private static IReadOnlyList<StyledLine> ForALaterRun(Snapshot snapshot)
    {
        if (snapshot.Journal is not { } journal)
        {
            return [];
        }

        var left = snapshot.Counts.Unsucceeded;
        var setAside = DisplayText.Count(snapshot.SetAside.Count, "earlier success", "earlier successes");
        var notInGraph = DisplayText.Count(snapshot.JournalPathsNotInGraph, "journal path", "journal paths");
        return
        [
            .. left == 0
                ? Array.Empty<StyledLine>()
                : [StyledLine.Of($"A run that continues journal {journal.Id} has {DisplayText.Count(left, "task", "tasks")} left to run.")],
            .. snapshot.SetAside.Count == 0
                ? Array.Empty<StyledLine>()
                :
                [
                    StyledLine.Of($"{setAside} set aside to run again:"),
                    .. snapshot.SetAside.Select(path => StyledLine.Of(DisplayText.Indent + path, Palette.Dim)),
                ],
            .. snapshot.JournalPathsNotInGraph == 0
                ? Array.Empty<StyledLine>()
                :
                [
                    StyledLine.Of(
                        $"{notInGraph} not in the graph ({DisplayText.Number(snapshot.JournalPathsNotInGraphUnsucceeded)} not succeeded)",
                        Palette.Dim),
                ],
        ];
    }

    private static IReadOnlyList<StyledLine> Failures(Snapshot snapshot, Glyphs glyphs)
    {
        if (snapshot.Failed.Count == 0)
        {
            return [];
        }

        return
        [
            Heading("Failures", snapshot.Failed.Count),
            .. snapshot.Failed.SelectMany(task => Failure(task, snapshot.TryGetBlocked(task.Path, out var blocked) ? blocked : null, glyphs)),
        ];
    }

    /// <summary>
    /// A failed task: its path with its kind, when it failed, how long it ran and how far it got, then its message, its detail
    /// lines and what it blocked beneath it.
    /// </summary>
    private static IEnumerable<StyledLine> Failure(NodeSnapshot task, BlockedTasks? blocked, Glyphs glyphs)
    {
        string[] facts =
        [
            task.Failure is FailureKind.Unexpected ? "unexpected failure" : "reported failure",
            .. task.Settled is { } at ? [DisplayText.Time(at)] : Array.Empty<string>(),
            $"ran {DisplayText.Duration(task.Ran ?? TimeSpan.Zero)}",
            .. Progress(task, glyphs) is { } progress ? [$"got to {progress}"] : Array.Empty<string>(),
        ];

        yield return StatusMark.For(NodeStatus.Failed, glyphs).Line(" ")
            .Add(task.Path.ToString())
            .Add(DisplayText.Between + string.Join($" {glyphs.Separator} ", facts), Palette.Dim);

        if (task.Message is { } message)
        {
            yield return StyledLine.Of(DisplayText.Indent + glyphs.Text(message));
        }

        foreach (var line in Kept(task.Detail, task.DetailOmitted, "line", "lines", glyphs))
        {
            yield return line;
        }

        if (blocked is not null)
        {
            var roots = string.Join(", ", blocked.Roots.Select(Root));
            var mark = StatusMark.For(NodeStatus.Blocked, glyphs);
            yield return StyledLine.Of($"{DisplayText.Indent}{mark.Glyph} ", mark.Style)
                .Add($"blocked {DisplayText.Count(blocked.Tasks.Count, "task", "tasks")}: {roots}");
        }
    }

    /// <summary>A root of what a failure blocked: its path, and for a group the number of tasks beneath it.</summary>
    private static string Root(NodeSnapshot root)
        => root.IsGroup ? $"{root.Path} ({DisplayText.Number(root.Counts.Total)})" : root.Path.ToString();

    /// <summary>How far a task got: its status text, its count, or both. <see langword="null"/> when it reported neither.</summary>
    private static string? Progress(NodeSnapshot task, Glyphs glyphs)
    {
        var count = DisplayText.Progress(task);
        if (task.StatusText is not { } given)
        {
            return count;
        }

        var text = glyphs.Text(given);
        return count is null ? text : $"{text}, {count}";
    }

    /// <summary>The lines a task kept, indented, then how many more it gave that were not kept.</summary>
    private static IEnumerable<StyledLine> Kept(IReadOnlyList<string> lines, int omitted, string one, string many, Glyphs glyphs)
    {
        foreach (var line in lines)
        {
            yield return StyledLine.Of(DisplayText.Indent + glyphs.Text(line), Palette.Dim);
        }

        if (omitted > 0)
        {
            var more = DisplayText.Count(omitted, $"more {one} that was", $"more {many} that were");
            yield return StyledLine.Of($"{DisplayText.Indent}and {more} counted but not kept", Palette.Dim);
        }
    }
}
