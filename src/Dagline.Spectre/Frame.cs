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
    public static IReadOnlyList<StyledLine> Build(Snapshot snapshot, Size size, DateTimeOffset now, Glyphs glyphs)
    {
        var width = Math.Max(0, size.Width);
        var height = Math.Max(0, size.Height);
        var header = Header.Lines(snapshot, width, now, glyphs);
        var groups = GroupsPane.Lines(snapshot, width, height - header.Count, glyphs);
        var left = height - header.Count - groups.Count;
        var waiting = WaitingPane.Lines(snapshot, width, now, glyphs, left);
        var working = WorkingPane.Lines(snapshot, width, now, glyphs, left - waiting.Count);
        return
        [
            .. header.Concat(groups).Concat(working).Concat(waiting)
                .Select(line => line.Fit(width, glyphs.Ellipsis))
                .Take(height),
        ];
    }
}
