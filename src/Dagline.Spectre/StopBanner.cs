using Spectre.Console;

namespace Dagline.Spectre;

/// <summary>
/// The one line under the header once a drain or a stop has been applied. After a drain: how many running tasks the run is
/// letting finish, and what Ctrl+C does now. After a stop: how many running tasks the run is waiting for, and how long is left
/// until the deadline.
/// </summary>
internal static class StopBanner
{
    private static readonly Style _badge = new(Color.Black, Color.Yellow, Decoration.Bold);

    /// <summary>
    /// The banner, or none before a drain or a stop has been applied. A stop that follows a drain replaces it. When the banner is
    /// wider than <paramref name="width"/>, what the run is waiting for gives way, so that on the draining banner what Ctrl+C
    /// does is always kept.
    /// </summary>
    /// <param name="snapshot">The run, whose drain and stop the banner shows, and whose stop says who asked for it.</param>
    /// <param name="width">The width of the console.</param>
    /// <param name="now">The time on the display's clock, which the time left runs to.</param>
    /// <param name="glyphs">The glyphs to draw with.</param>
    public static IReadOnlyList<StyledLine> Lines(Snapshot snapshot, int width, DateTimeOffset now, Glyphs glyphs)
    {
        var running = snapshot.Counts[NodeStatus.Running];
        if (snapshot.Stop is { } stop)
        {
            return [Stopping(stop, running, width, now, glyphs)];
        }

        return snapshot.Drain is null ? [] : [Draining(running, width, glyphs)];
    }

    private static StyledLine Draining(int running, int width, Glyphs glyphs)
    {
        var line = StyledLine.Of(" DRAINING ", _badge)
            .Add(" ")
            .Add($"letting {DisplayText.Count(running, "running task", "running tasks")} finish", Palette.Cancelled);

        // ConsoleStop turns the second Ctrl+C into a stop.
        var action = StyledLine.Of($" {glyphs.Separator} ", Palette.Dim).Add("Ctrl+C again to stop", Palette.Dim);
        return line.Fit(Math.Max(0, width - action.Width), glyphs.Ellipsis).Add(action);
    }

    private static StyledLine Stopping(StopState stop, int running, int width, DateTimeOffset now, Glyphs glyphs)
    {
        var line = StyledLine.Of(" STOPPING ", _badge).Add(" ");
        if (stop.By is StopRequester.Dagline)
        {
            line = line.Add("the journal could not be written", Palette.Failed).Add($" {glyphs.Separator} ", Palette.Dim);
        }

        line = line.Add($"waiting for {DisplayText.Count(running, "running task", "running tasks")} to end", Palette.Cancelled);
        if (stop.Deadline is { } deadline)
        {
            line = line.Add($" {glyphs.Separator} ", Palette.Dim).Add($"{DisplayText.Duration(deadline - now)} left", Palette.Bold);
        }

        return line.Fit(width, glyphs.Ellipsis);
    }
}
