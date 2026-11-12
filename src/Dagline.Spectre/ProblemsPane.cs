using Spectre.Console;

namespace Dagline.Spectre;

/// <summary>
/// The pane of what has gone wrong so far: the failures, newest first, each with what it blocks; then the cancelled tasks,
/// newest first; then the latest few warnings. Every other warning is only counted, in the title.
/// </summary>
internal static class ProblemsPane
{
    private const string _indent = "    ";
    private const string _between = "  ";
    private static readonly Kind _failure = new("more failure", "more failures");
    private static readonly Kind _cancelled = new("more cancelled task", "more cancelled tasks");
    private static readonly Kind _warning = new("more warning", "more warnings");

    /// <summary>
    /// The lines of the pane, at most <paramref name="rows"/> of them with its title, or none when the run has no failure, no
    /// cancelled task and no warning, or when fewer than two rows are given. When its entries do not all fit, the pane shows
    /// those that do, in order and never half a failure, and ends with a line saying how many of each kind it left out.
    /// </summary>
    /// <param name="snapshot">The run.</param>
    /// <param name="latestWarnings">
    /// The latest warnings, newest first, from the events the display has handled: a snapshot does not say when a warning was added.
    /// </param>
    /// <param name="width">The width of the console.</param>
    /// <param name="glyphs">The glyphs to draw with.</param>
    /// <param name="rows">How many lines the pane may take.</param>
    public static IReadOnlyList<StyledLine> Lines(
        Snapshot snapshot,
        IEnumerable<WarningAdded> latestWarnings,
        int width,
        Glyphs glyphs,
        int rows)
    {
        if ((snapshot.Failed.Count == 0 && snapshot.Cancelled.Count == 0 && snapshot.WarningCount == 0) || rows < 2)
        {
            return [];
        }

        List<Entry> entries =
        [
            .. Failures(snapshot, width, glyphs),
            .. NewestFirst(snapshot.Cancelled).Select(task => new Entry(_cancelled, [Cancelled(task, width, glyphs)])),
            .. latestWarnings.Select(warning => new Entry(_warning, [Warning(warning, glyphs)])),
        ];
        List<StyledLine> lines = [PaneTitle.Line(Title(snapshot), width, glyphs)];
        if (entries.Sum(entry => entry.Lines.Count) <= rows - 1)
        {
            return [.. lines, .. entries.SelectMany(entry => entry.Lines)];
        }

        var shown = 0;
        while (shown < entries.Count && lines.Count + entries[shown].Lines.Count <= rows - 1)
        {
            lines.AddRange(entries[shown++].Lines);
        }

        lines.Add(TaskLine.More("and " + LeftOut(entries.Skip(shown))));
        return lines;
    }

    /// <summary>How many entries of each kind were left out, as in <c>4 more failures, 1 more warning</c>.</summary>
    private static string LeftOut(IEnumerable<Entry> left)
        => string.Join(", ", left.GroupBy(entry => entry.Kind).Select(kind => DisplayText.Count(kind.Count(), kind.Key.One, kind.Key.Many)));

    private static StyledLine Title(Snapshot snapshot)
    {
        string[] counts =
        [
            .. NonZero(snapshot.Failed.Count, "failed", "failed"),
            .. NonZero(snapshot.Cancelled.Count, "cancelled", "cancelled"),
            .. NonZero(snapshot.WarningCount, "warning", "warnings"),
        ];
        var title = StyledLine.Of("Problems", Palette.Bold).Add($" ({string.Join(", ", counts)})");
        var blocked = snapshot.Counts[NodeStatus.Blocked];
        if (blocked == 0)
        {
            return title;
        }

        var causes = snapshot.Blocked.Count;
        return title.Add(
            $" {DisplayText.Count(blocked, "task", "tasks")} blocked behind {DisplayText.Count(causes, "failure", "failures")}",
            Palette.Blocked);
    }

    private static IEnumerable<Entry> Failures(Snapshot snapshot, int width, Glyphs glyphs)
    {
        var blocks = snapshot.Blocked.ToDictionary(entry => entry.Cause, entry => entry.Tasks.Count);
        foreach (var task in NewestFirst(snapshot.Failed))
        {
            var when = StyledLine.Empty;
            if (blocks.TryGetValue(task.Path, out var blocked))
            {
                when = when.Add($"{glyphs.Blocked} blocks {DisplayText.Number(blocked)}", Palette.Blocked).Add(_between);
            }

            when = when.Add(SettledAt(task), Palette.Dim);
            var named = StyledLine.Spread(Named(task, glyphs.Failed, Palette.Failed), when, width, glyphs.Ellipsis);
            yield return new Entry(_failure, task.Message is { } message ? [named, StyledLine.Of(_indent + message)] : [named]);
        }
    }

    private static StyledLine Cancelled(NodeSnapshot task, int width, Glyphs glyphs)
    {
        var named = Named(task, glyphs.Cancelled, Palette.Cancelled);
        if (task.Message is { } message)
        {
            named = named.Add(_between + message, Palette.Dim);
        }

        return StyledLine.Spread(named, StyledLine.Of(SettledAt(task), Palette.Dim), width, glyphs.Ellipsis);
    }

    private static StyledLine Warning(WarningAdded warning, Glyphs glyphs)
        => StyledLine.Of(glyphs.Warning, Palette.Warning).Add($" {warning.Path}").Add(_between + warning.Warning, Palette.Dim);

    private static StyledLine Named(NodeSnapshot task, string glyph, Style style)
        => StyledLine.Of(glyph, style).Add($" {task.Path}");

    private static string SettledAt(NodeSnapshot task) => task.Settled is { } settled ? DisplayText.Time(settled) : "";

    /// <summary>The tasks, the one that settled last first. Tasks that settled at once keep their declaration order.</summary>
    private static IEnumerable<NodeSnapshot> NewestFirst(IEnumerable<NodeSnapshot> tasks)
        => tasks.OrderByDescending(task => task.Settled);

    private static IEnumerable<string> NonZero(int count, string one, string many)
        => count == 0 ? [] : [DisplayText.Count(count, one, many)];

    /// <summary>A kind of entry, by the words that count those left out.</summary>
    private sealed record Kind(string One, string Many);

    /// <summary>One problem: a failure with its message, a cancelled task or a warning, which is shown whole or not at all.</summary>
    private sealed record Entry(Kind Kind, IReadOnlyList<StyledLine> Lines);
}
