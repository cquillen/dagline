using System.Collections.Immutable;
using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Dagline.Spectre;

/// <summary>One stretch of text in one style.</summary>
internal readonly record struct StyledSpan(string Text, Style Style);

/// <summary>
/// One line of a frame or of the summary: stretches of styled text, measured in terminal cells by Spectre.Console.
/// Immutable: every change gives a new line.
/// </summary>
internal sealed class StyledLine
{
    private StyledLine(ImmutableArray<StyledSpan> spans, int width)
    {
        Spans = spans;
        Width = width;
    }

    public static StyledLine Empty { get; } = new([], 0);

    public ImmutableArray<StyledSpan> Spans { get; }

    /// <summary>How many terminal cells the line takes.</summary>
    public int Width { get; }

    /// <summary>The text of the line without its styles.</summary>
    public string Text => string.Concat(Spans.Select(span => span.Text));

    public static StyledLine Of(string text, Style? style = null) => Empty.Add(text, style);

    public StyledLine Add(string text, Style? style = null)
        => text.Length == 0 ? this : new(Spans.Add(new StyledSpan(text, style ?? Palette.Plain)), Width + CellsOf(text));

    public StyledLine Add(StyledLine other) => new(Spans.AddRange(other.Spans), Width + other.Width);

    /// <summary>The line with spaces added to make it <paramref name="width"/> cells wide, or the line itself when it is that wide already.</summary>
    public StyledLine PadTo(int width) => Width >= width ? this : Add(new string(' ', width - Width));

    /// <summary>The line cut to <paramref name="width"/> cells, ending in <paramref name="ellipsis"/> where it was cut.</summary>
    public StyledLine Fit(int width, string ellipsis)
    {
        if (Width <= width)
        {
            return this;
        }

        var mark = CellsOf(ellipsis) <= width ? ellipsis : "";
        var room = width - CellsOf(mark);
        var fitted = Empty;
        foreach (var span in Spans)
        {
            var part = Cut(span.Text, room);
            fitted = fitted.Add(part, span.Style);
            room -= CellsOf(part);
            if (part.Length < span.Text.Length)
            {
                break;
            }
        }

        return fitted.Add(mark, Palette.Dim);
    }

    /// <summary>
    /// <paramref name="left"/> at the start of a line of <paramref name="width"/> cells and <paramref name="right"/> at its end,
    /// with spaces between. When both do not fit, the left gives way, and when too little of it would be left, the right is
    /// dropped and the left is cut to the width instead.
    /// </summary>
    public static StyledLine Spread(StyledLine left, StyledLine right, int width, string ellipsis)
    {
        const int leastLeft = 8;
        var room = width - right.Width - 1;
        if (right.Width == 0 || room < Math.Min(leastLeft, left.Width))
        {
            return left.Fit(width, ellipsis);
        }

        var fitted = left.Fit(room, ellipsis);
        return fitted.Add(new string(' ', width - fitted.Width - right.Width)).Add(right);
    }

    /// <summary>
    /// <paramref name="left"/> at the start of a line of <paramref name="width"/> cells and <paramref name="right"/> at its end,
    /// with spaces between. Only the left gives way, down to nothing, so the right is always kept, as a time at the end of a line
    /// must be. Only when the right alone is wider than the line is the right cut too.
    /// </summary>
    public static StyledLine PinRight(StyledLine left, StyledLine right, int width, string ellipsis)
    {
        var room = width - right.Width - 1;
        if (right.Width == 0 || room < 0)
        {
            return (right.Width == 0 ? left : right).Fit(width, ellipsis);
        }

        var fitted = left.Fit(room, ellipsis);
        return fitted.Add(new string(' ', width - fitted.Width - right.Width)).Add(right);
    }

    /// <summary>The start of <paramref name="text"/> that takes no more than <paramref name="cells"/> cells.</summary>
    private static string Cut(string text, int cells)
    {
        var cut = new StringBuilder();
        var used = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var runeCells = CellsOf(rune.ToString());
            if (used + runeCells > cells)
            {
                break;
            }

            cut.Append(rune.ToString());
            used += runeCells;
        }

        return cut.ToString();
    }

    private static int CellsOf(string text) => new Segment(text).CellCount();
}
