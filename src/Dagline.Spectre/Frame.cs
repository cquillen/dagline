using Spectre.Console;

namespace Dagline.Spectre;

/// <summary>
/// Builds a frame: the lines the display shows for a run at one instant, already cut to the console's size.
/// It knows nothing of the terminal, which <see cref="Terminal"/> drives.
/// </summary>
internal static class Frame
{
    /// <summary>The frame for the run as <paramref name="snapshot"/> shows it.</summary>
    /// <param name="snapshot">The run, usually as the snapshot source has it now.</param>
    /// <param name="size">The size of the console: no line is wider and there are no more lines than its height.</param>
    /// <param name="now">The time on the display's clock.</param>
    /// <param name="glyphs">The glyphs to draw with.</param>
    /// <param name="heard">What the display has kept from the events it has handled, which a snapshot does not say.</param>
    public static IReadOnlyList<StyledLine> Build(Snapshot snapshot, Size size, DateTimeOffset now, Glyphs glyphs, HeardEvents heard)
    {
        var width = Math.Max(0, size.Width);
        var height = Math.Max(0, size.Height);
        IReadOnlyList<StyledLine> top =
        [
            .. Header.Lines(snapshot, width, now, glyphs),
            .. StopBanner.Lines(snapshot, heard.StopBy, now, glyphs),
        ];
        return
        [
            .. top
                .Concat(GroupsPane.Lines(snapshot, width, height - top.Count, glyphs))
                .Concat(ProblemsPane.Lines(snapshot, heard.LatestWarnings, width, glyphs))
                .Select(line => line.Fit(width, glyphs.Ellipsis))
                .Take(height),
        ];
    }
}
