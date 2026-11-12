namespace Dagline.Spectre;

/// <summary>
/// The pane of a frame that lists the running tasks that are working, in declaration order, each with its progress, its status
/// text and how long it has been running.
/// </summary>
internal static class WorkingPane
{
    private const int _barCells = 16;

    /// <summary>
    /// The pane's lines, at most <paramref name="rows"/> of them with its title, or none when no task is running or fewer than
    /// two rows are given. When tasks are running and every one is waiting, the pane is its title alone, saying so, and one row
    /// is enough for it.
    /// </summary>
    /// <param name="snapshot">The run.</param>
    /// <param name="width">The width of the console.</param>
    /// <param name="now">The time on the display's clock, which the times running run to.</param>
    /// <param name="glyphs">The glyphs to draw with.</param>
    /// <param name="rows">How many lines the pane may take.</param>
    public static IReadOnlyList<StyledLine> Lines(Snapshot snapshot, int width, DateTimeOffset now, Glyphs glyphs, int rows)
    {
        var running = snapshot.Counts[NodeStatus.Running];
        var working = snapshot.Working;
        var title = StyledLine.Of($"Working ({DisplayText.Number(working.Count)})", Palette.Bold);
        if (running > 0 && working.Count == 0 && rows >= 1)
        {
            var allWaiting = running == 1 ? "the running task is waiting" : $"all {DisplayText.Number(running)} running tasks are waiting";
            return [PaneTitle.Line(title.Add(" " + allWaiting, Palette.Dim), width, glyphs)];
        }

        if (working.Count == 0 || rows < PaneRows.Least)
        {
            return [];
        }

        var fits = working.Count <= rows - 1;
        IReadOnlyList<NodeSnapshot> shown = fits ? working : [.. working.Take(rows - 2)];
        var column = TaskLine.PathColumn(snapshot, width);
        var countCells = shown.Select(node => CountText(node).Length).DefaultIfEmpty(0).Max();
        List<StyledLine> lines =
        [
            PaneTitle.Line(title, width, glyphs),
            .. shown.Select(node => Line(node, column, countCells, width, now, glyphs)),
        ];
        if (!fits)
        {
            lines.Add(TaskLine.More($"and {DisplayText.Number(working.Count - shown.Count)} more working"));
        }

        return lines;
    }

    private static StyledLine Line(NodeSnapshot node, int column, int countCells, int width, DateTimeOffset now, Glyphs glyphs)
    {
        var line = TaskLine.Start(StatusMark.For(NodeStatus.Running, glyphs), node, column, glyphs);
        if (countCells > 0)
        {
            line = line.Add(Bar(node, glyphs)).Add(" ").Add(CountText(node).PadRight(countCells));
        }

        if (node.StatusText is { } status)
        {
            line = line.Add((countCells > 0 ? DisplayText.Between : "") + glyphs.Text(status));
        }

        return TaskLine.Finish(line, now - (node.Started ?? now), width, glyphs);
    }

    /// <summary>How far the count has got towards the total, or blank cells when the task has given no count with a total.</summary>
    private static StyledLine Bar(NodeSnapshot node, Glyphs glyphs)
    {
        if (node is not { Count: { } count, Total: { } total })
        {
            return StyledLine.Of(new string(' ', _barCells));
        }

        // A total of zero or a count past the total fills the bar: Dagline does not compare the two.
        var filled = total <= 0 || count >= total ? _barCells : (int)Math.Max(0, Math.Floor((double)count / total * _barCells));
        return StyledLine.Of(DisplayText.Repeat(glyphs.Bar, filled), Palette.Working).Add(DisplayText.Repeat(glyphs.BarRest, _barCells - filled), Palette.Dim);
    }

    private static string CountText(NodeSnapshot node) => DisplayText.Progress(node) ?? "";
}
