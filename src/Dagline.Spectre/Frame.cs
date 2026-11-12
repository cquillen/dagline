using Spectre.Console;

namespace Dagline.Spectre;

/// <summary>
/// Builds a frame: the lines the display shows for a run at one instant, already cut to the console's size.
/// It knows nothing of the terminal, which <see cref="Terminal"/> drives.
/// </summary>
internal static class Frame
{
    /// <summary>The fewest columns at which the panes are drawn: in a narrower console the frame is the header alone.</summary>
    public const int LeastWidth = 60;

    /// <summary>The fewest rows at which the panes are drawn: in a shorter console the frame is the header alone.</summary>
    public const int LeastHeight = 15;

    /// <summary>
    /// The frame for the run as <paramref name="snapshot"/> shows it: the header, the stop banner, the groups, then the working,
    /// waiting and problems panes in the lines left, shared as <see cref="PaneRows.Share"/> says. In a console smaller than
    /// <see cref="LeastWidth"/> by <see cref="LeastHeight"/> it is the header alone.
    /// </summary>
    /// <param name="snapshot">The run, usually as the snapshot source has it now.</param>
    /// <param name="size">The size of the console: no line is wider and there are no more lines than its height.</param>
    /// <param name="now">The time on the display's clock.</param>
    /// <param name="glyphs">The glyphs to draw with.</param>
    /// <param name="heard">What the display has kept from the events it has handled, which a snapshot does not say.</param>
    public static IReadOnlyList<StyledLine> Build(Snapshot snapshot, Size size, DateTimeOffset now, Glyphs glyphs, HeardEvents heard)
    {
        var width = Math.Max(0, size.Width);
        var height = Math.Max(0, size.Height);
        if (width < LeastWidth || height < LeastHeight)
        {
            return Cut(Header.Lines(snapshot, width, now, glyphs), width, height, glyphs);
        }

        IReadOnlyList<StyledLine> top =
        [
            .. Header.Lines(snapshot, width, now, glyphs),
            .. StopBanner.Lines(snapshot, heard.StopBy, width, now, glyphs),
        ];
        var groups = GroupsPane.Lines(snapshot, width, height - top.Count, glyphs);
        var left = height - top.Count - groups.Count;
        var rows = PaneRows.Share(left, PanesIn(new(left, left, left)).Count());
        var (working, waiting, problems) = PanesIn(rows);
        return Cut([.. top, .. groups, .. working, .. waiting, .. problems], width, height, glyphs);

        // Asked for every line that is left, each pane says how many it would take.
        Panes PanesIn(PaneRows rows) => new(
            WorkingPane.Lines(snapshot, width, now, glyphs, rows.Working),
            WaitingPane.Lines(snapshot, width, now, glyphs, rows.Waiting),
            ProblemsPane.Lines(snapshot, heard.LatestWarnings, width, glyphs, rows.Problems));
    }

    private static List<StyledLine> Cut(IEnumerable<StyledLine> lines, int width, int height, Glyphs glyphs)
        => [.. lines.Select(line => line.Fit(width, glyphs.Ellipsis)).Take(height)];

    /// <summary>The lines of the panes below the groups.</summary>
    private sealed record Panes(IReadOnlyList<StyledLine> Working, IReadOnlyList<StyledLine> Waiting, IReadOnlyList<StyledLine> Problems)
    {
        public PaneRows Count() => new(Working.Count, Waiting.Count, Problems.Count);
    }

}
