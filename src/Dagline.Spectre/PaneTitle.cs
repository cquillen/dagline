namespace Dagline.Spectre;

/// <summary>The line that opens a pane of a frame: its title set in a rule across the width, as in <c>── Working (3) ───</c>.</summary>
internal static class PaneTitle
{
    /// <summary>A rule <paramref name="width"/> cells wide with <paramref name="title"/> set in it near its start.</summary>
    public static StyledLine Line(StyledLine title, int width, Glyphs glyphs)
    {
        var line = StyledLine.Of(glyphs.Rule + glyphs.Rule + " ", Palette.Dim).Add(title).Add(" ");
        return line.Add(DisplayText.Repeat(glyphs.Rule, width - line.Width), Palette.Dim);
    }
}
