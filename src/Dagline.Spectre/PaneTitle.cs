namespace Dagline.Spectre;

/// <summary>The one home of the line that opens a pane: its title set in a rule across the frame, as in <c>── Problems (2 failed) ───</c>.</summary>
internal static class PaneTitle
{
    /// <summary>The title line of a pane, <paramref name="width"/> cells wide.</summary>
    /// <param name="title">The title, with any counts it carries.</param>
    /// <param name="width">The width of the console.</param>
    /// <param name="glyphs">The glyphs to draw with.</param>
    public static StyledLine Line(StyledLine title, int width, Glyphs glyphs)
    {
        var line = StyledLine.Of(glyphs.Rule + glyphs.Rule + " ", Palette.Dim).Add(title).Add(" ");
        return line.Add(string.Concat(Enumerable.Repeat(glyphs.Rule, Math.Max(0, width - line.Width))), Palette.Dim);
    }
}
