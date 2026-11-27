namespace Dagline.Spectre;

/// <summary>
/// The pane of a frame that lists the running tasks that are waiting on external systems, longest wait first, each with its
/// status text or else its waiting text, and how long it has waited. It has a pane of its own so that many waiting tasks do not
/// push the working ones off the screen.
/// </summary>
internal static class WaitingPane
{
    /// <summary>The most tasks the pane lists.</summary>
    public const int MostTasks = 5;

    /// <summary>
    /// The pane's lines, at most <paramref name="rows"/> of them with its title and listing at most <see cref="MostTasks"/> tasks,
    /// or none when no task is waiting or fewer than two rows are given.
    /// </summary>
    /// <param name="snapshot">The run.</param>
    /// <param name="width">The width of the console.</param>
    /// <param name="column">The cells the path column takes, as <see cref="TaskLine.PathColumn"/> gives them for the frame.</param>
    /// <param name="now">The time on the display's clock, which the times waited run to.</param>
    /// <param name="glyphs">The glyphs to draw with.</param>
    /// <param name="rows">How many lines the pane may take.</param>
    public static IReadOnlyList<StyledLine> Lines(Snapshot snapshot, int width, int column, DateTimeOffset now, Glyphs glyphs, int rows)
    {
        // OrderBy is stable, so tasks that began waiting at the same instant stay in declaration order.
        var waiting = snapshot.Waiting.OrderBy(node => node.WaitingSince).ToList();
        if (waiting.Count == 0 || rows < PaneRows.Least)
        {
            return [];
        }

        var shown = PaneRows.Shown(waiting, rows, MostTasks);
        var title = StyledLine.Of($"Waiting on external systems ({DisplayText.Number(waiting.Count)})", Palette.Bold)
            .Add($" longest {DisplayText.Duration(Waited(waiting[0], now))}", Palette.Dim);
        List<StyledLine> lines = [PaneTitle.Line(title, width, glyphs), .. shown.Select(node => Line(node, column, width, now, glyphs))];
        if (shown.Count < waiting.Count)
        {
            var newest = DisplayText.Duration(Waited(waiting[^1], now));
            lines.Add(TaskLine.More($"and {DisplayText.Number(waiting.Count - shown.Count)} more, the newest waiting {newest}"));
        }

        return lines;
    }

    private static StyledLine Line(NodeSnapshot node, int column, int width, DateTimeOffset now, Glyphs glyphs)
    {
        var line = TaskLine.Start(StatusMark.Waiting(glyphs), node, column, glyphs);
        if (node.StatusText is { } status)
        {
            line = line.Add(DisplayText.StatusText(status, glyphs));
        }
        else if (node.WaitingText is { } waitingText)
        {
            line = line.Add(glyphs.Text(waitingText));
        }

        return TaskLine.Finish(line, Waited(node, now), width, glyphs);
    }

    private static TimeSpan Waited(NodeSnapshot node, DateTimeOffset now) => now - (node.WaitingSince ?? now);
}
