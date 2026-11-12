namespace Dagline.Spectre;

/// <summary>The one home of the line that opens a pane: a short rule, the pane's title, then a rule to the right edge.</summary>
internal static class PaneTitle
{
    /// <summary>The title line of a pane, as in <c>── Groups ──────</c>, <paramref name="width"/> cells wide.</summary>
    /// <param name="title">The title, with any counts it carries.</param>
    /// <param name="width">The width of the console.</param>
    /// <param name="glyphs">The glyphs to draw with.</param>
    public static StyledLine Line(StyledLine title, int width, Glyphs glyphs)
    {
        var line = StyledLine.Of(glyphs.Rule + glyphs.Rule + " ", Palette.Dim).Add(title).Add(" ");
        return line.Add(DisplayText.Repeat(glyphs.Rule, width - line.Width), Palette.Dim);
    }
}
