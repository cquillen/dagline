using Spectre.Console;

namespace Dagline.Spectre;

/// <summary>
/// The one home of the count of tasks per status that the header and the summary show: the running tasks split into working
/// and waiting, the earlier successes apart, then the warnings, each left out when it is zero.
/// </summary>
internal static class Tally
{
    private const string _between = "  ";

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
                line = line.Add(_between);
            }

            line = line.Add($"{part.Glyph} {DisplayText.Number(part.Count)}", part.Style);
            if (word)
            {
                line = line.Add(" " + (part.Count == 1 ? part.One : part.Many), part.Style);
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
        var waiting = snapshot.Nodes.Count(Running.IsWaiting);
        var counts = snapshot.Counts;
        var warnings = snapshot.WarningCount;

        yield return new(counts[NodeStatus.Succeeded], glyphs.Succeeded, Palette.Succeeded, "succeeded", "succeeded", snapshot.EarlierSuccesses);
        yield return new(counts[NodeStatus.Running] - waiting, glyphs.Running, Palette.Working, "working", "working");
        yield return new(waiting, glyphs.Waiting, Palette.Waiting, "waiting", "waiting");
        yield return new(counts[NodeStatus.Ready], glyphs.Ready, Palette.For(NodeStatus.Ready), "ready", "ready");
        yield return new(counts[NodeStatus.Pending], glyphs.Pending, Palette.For(NodeStatus.Pending), "pending", "pending");
        yield return new(counts[NodeStatus.Failed], glyphs.Failed, Palette.Failed, "failed", "failed");
        yield return new(counts[NodeStatus.Blocked], glyphs.Blocked, Palette.Blocked, "blocked", "blocked");
        yield return new(counts[NodeStatus.Cancelled], glyphs.Cancelled, Palette.Cancelled, "cancelled", "cancelled");
        yield return new(warnings, glyphs.Warning, Palette.Warning, "warning", "warnings");
    }

    private readonly record struct Part(int Count, string Glyph, Style Style, string One, string Many, int Earlier = 0);
}
