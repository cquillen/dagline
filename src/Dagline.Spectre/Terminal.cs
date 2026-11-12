using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Dagline.Spectre;

/// <summary>
/// Drives the terminal for the display: the alternate screen, the cursor, and writing lines at their place.
/// It knows nothing of what the lines say, which <see cref="Frame"/> and <see cref="Summary"/> decide.
/// </summary>
/// <remarks>
/// Every write is one call to the console with control segments and text segments together. Spectre.Console drops the
/// control segments on a console that does not accept ANSI sequences, and styles the text for the console's colour depth.
/// </remarks>
internal sealed class Terminal(IAnsiConsole console)
{
    private const string _csi = "\u001b[";
    private const string _enterAlternateScreen = _csi + "?1049h";
    private const string _leaveAlternateScreen = _csi + "?1049l";
    private const string _hideCursor = _csi + "?25l";
    private const string _showCursor = _csi + "?25h";
    private const string _beginUpdate = _csi + "?2026h";
    private const string _endUpdate = _csi + "?2026l";
    private const string _eraseLine = _csi + "2K";
    private const string _eraseBelow = _csi + "J";

    private int _linesDrawn;

    /// <summary>The size of the console now, read again on every call.</summary>
    public Size Size => new(console.Profile.Width, console.Profile.Height);

    public void EnterAlternateScreen()
    {
        _linesDrawn = 0;
        Write([Segment.Control(_enterAlternateScreen), Segment.Control(_hideCursor)]);
    }

    /// <summary>Draws a frame from the top-left corner, over whatever was there, in one synchronised update.</summary>
    public void Draw(IReadOnlyList<StyledLine> frame)
    {
        var segments = new List<Segment> { Segment.Control(_beginUpdate) };
        for (var row = 0; row < frame.Count; row++)
        {
            // The line is erased before it is written: erasing after a line that fills the width would take its last cell on
            // terminals that hold the cursor on that cell.
            segments.Add(Segment.Control(MoveTo(row)));
            segments.Add(Segment.Control(_eraseLine));
            segments.AddRange(SegmentsOf(frame[row]));
        }

        if (frame.Count < Size.Height)
        {
            segments.Add(Segment.Control(MoveTo(frame.Count)));
            segments.Add(Segment.Control(_eraseBelow));
        }

        segments.Add(Segment.Control(_endUpdate));
        Write(segments);
        _linesDrawn = frame.Count;
    }

    /// <summary>
    /// Puts the cursor below the last frame, so that what follows comes after it on a terminal that ignored the alternate
    /// screen, then leaves the alternate screen and shows the cursor.
    /// </summary>
    public void LeaveAlternateScreen()
        => Write(
        [
            Segment.Control(MoveTo(Math.Max(0, _linesDrawn - 1))),
            Segment.LineBreak,
            Segment.Control(_leaveAlternateScreen),
            Segment.Control(_showCursor),
        ]);

    /// <summary>Prints lines where the cursor is, each followed by a line break, as on the ordinary screen.</summary>
    public void Print(IReadOnlyList<StyledLine> lines)
        => Write([.. lines.SelectMany(line => SegmentsOf(line).Append(Segment.LineBreak))]);

    private void Write(IReadOnlyList<Segment> segments) => console.Write(new Segments(segments));

    private static string MoveTo(int row) => string.Create(CultureInfo.InvariantCulture, $"{_csi}{row + 1};1H");

    private static IEnumerable<Segment> SegmentsOf(StyledLine line) => line.Spans.Select(span => new Segment(span.Text, span.Style));

    /// <summary>Segments written exactly as given: no wrapping, no padding.</summary>
    private sealed class Segments(IReadOnlyList<Segment> segments) : IRenderable
    {
        public Measurement Measure(RenderOptions options, int maxWidth) => new(maxWidth, maxWidth);

        public IEnumerable<Segment> Render(RenderOptions options, int maxWidth) => segments;
    }
}
