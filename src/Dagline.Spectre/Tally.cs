namespace Dagline.Spectre;

/// <summary>
/// The one home of the count of tasks per status that the header and the summary show: the running tasks split into working
/// and waiting, the earlier successes apart, then the warnings, each left out when it is zero.
/// </summary>
internal static class Tally
{
    /// <summary>The counts as the header writes them, as in <c>✓ 1,560 (1,543 earlier)  ◌ 11  · 91</c>.</summary>
    public static StyledLine Short(Snapshot snapshot, Glyphs glyphs) => Join(Parts(snapshot, glyphs), word: false);

    /// <summary>
    /// The counts as the summary writes them, as in <c>✓ 1,560 succeeded (1,543 earlier)  · 91 pending</c>,
    /// or <c>no tasks</c> when the graph has none.
    /// </summary>
    public static StyledLine Long(Snapshot snapshot, Glyphs glyphs)
    {
        var line = Join(Parts(snapshot, glyphs), word: true);
        return line.Width == 0 ? StyledLine.Of("no tasks", Palette.Dim) : line;
    }

    private static StyledLine Join(IEnumerable<Part> parts, bool word)
    {
        var line = StyledLine.Empty;
        foreach (var part in parts.Where(part => part.Count > 0))
        {
            if (line.Width > 0)
            {
                line = line.Add(DisplayText.Between);
            }

            line = line.Add($"{part.Mark.Glyph} {DisplayText.Number(part.Count)}", part.Mark.Style);
            if (word)
            {
                line = line.Add(" " + (part.Count == 1 ? part.One : part.Many), part.Mark.Style);
            }

            if (part.Earlier > 0)
            {
                line = line.Add($" ({DisplayText.Number(part.Earlier)} earlier)", Palette.Dim);
            }
        }

        return line;
    }

    private static IEnumerable<Part> Parts(Snapshot snapshot, Glyphs glyphs)
    {
        var waiting = snapshot.Waiting.Count;
        var counts = snapshot.Counts;
        var warnings = snapshot.WarningCount;

        yield return new(counts[NodeStatus.Succeeded], StatusMark.For(NodeStatus.Succeeded, glyphs), "succeeded", "succeeded", snapshot.EarlierSuccesses);
        yield return new(snapshot.Working.Count, StatusMark.For(NodeStatus.Running, glyphs), "working", "working");
        yield return new(waiting, StatusMark.Waiting(glyphs), "waiting", "waiting");
        yield return new(counts[NodeStatus.Ready], StatusMark.For(NodeStatus.Ready, glyphs), "ready", "ready");
        yield return new(counts[NodeStatus.Pending], StatusMark.For(NodeStatus.Pending, glyphs), "pending", "pending");
        yield return new(counts[NodeStatus.Failed], StatusMark.For(NodeStatus.Failed, glyphs), "failed", "failed");
        yield return new(counts[NodeStatus.Blocked], StatusMark.For(NodeStatus.Blocked, glyphs), "blocked", "blocked");
        yield return new(counts[NodeStatus.Cancelled], StatusMark.For(NodeStatus.Cancelled, glyphs), "cancelled", "cancelled");
        yield return new(warnings, StatusMark.Warning(glyphs), "warning", "warnings");
    }

    private readonly record struct Part(int Count, Mark Mark, string One, string Many, int Earlier = 0);
}
