namespace Dagline.Spectre;

/// <summary>
/// The parts that the working and waiting panes share in a line about one task: the glyph and the path in a column of their
/// own, the time at the right edge, and the line that says how many tasks a pane left out.
/// </summary>
internal static class TaskLine
{
    private static readonly int _gap = DisplayText.Between.Length;

    /// <summary>
    /// How many cells the path column takes after the glyph: the longest task path of the graph and a gap, but no more than
    /// 45% of the width. It is the same in every frame of a run at one width, so the columns do not move as tasks come and go.
    /// </summary>
    public static int PathColumn(Snapshot snapshot, int width)
    {
        var longest = snapshot.Nodes.Where(node => !node.IsGroup).Select(node => StyledLine.Of(node.Path.ToString()).Width).DefaultIfEmpty(0).Max();
        return Math.Min(longest + _gap, width * 45 / 100);
    }

    /// <summary>The mark, then the path cut to the column, keeping its start, and spaces to the end of the column.</summary>
    public static StyledLine Start(Mark mark, NodeSnapshot node, int column, Glyphs glyphs)
    {
        var path = StyledLine.Of(node.Path.ToString()).Fit(Math.Max(0, column - _gap), glyphs.Ellipsis);
        var start = mark.Line(" ");
        return start.Add(path).PadTo(start.Width + column);
    }

    /// <summary>
    /// <paramref name="left"/> with <paramref name="time"/> at the right edge of a line <paramref name="width"/> cells wide.
    /// The left gives way, so the time is always kept.
    /// </summary>
    public static StyledLine Finish(StyledLine left, TimeSpan time, int width, Glyphs glyphs)
        => StyledLine.PinRight(left, StyledLine.Of(DisplayText.Duration(time)), width, glyphs.Ellipsis);

    /// <summary>The last line of a pane that was cut short, saying what it left out. The problems pane ends with one too.</summary>
    public static StyledLine More(string text) => StyledLine.Of(DisplayText.Between + text, Palette.Dim);
}
