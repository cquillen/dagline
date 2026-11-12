using Spectre.Console;

namespace Dagline.Spectre;

/// <summary>
/// The one line under the header once a stop has been applied: how many running tasks the run is waiting for, how long is left
/// until the deadline, and what Ctrl+C does now.
/// </summary>
internal static class StopBanner
{
    private static readonly Style _badge = new(Color.Black, Color.Yellow, Decoration.Bold);

    /// <summary>
    /// The banner, or none before a stop has been applied. When it is wider than <paramref name="width"/>, what the run is
    /// waiting for gives way, so what Ctrl+C does is always kept.
    /// </summary>
    /// <param name="snapshot">The run.</param>
    /// <param name="stopBy">
    /// Who asked for the stop, from the <see cref="StopRequested"/> the display has handled. A snapshot with a stop handed before
    /// that event was handled is taken as the app's.
    /// </param>
    /// <param name="width">The width of the console.</param>
    /// <param name="now">The time on the display's clock, which the time left runs to.</param>
    /// <param name="glyphs">The glyphs to draw with.</param>
    public static IReadOnlyList<StyledLine> Lines(Snapshot snapshot, StopRequester? stopBy, int width, DateTimeOffset now, Glyphs glyphs)
    {
        if (snapshot.Stop is not { } stop)
        {
            return [];
        }

        var byDagline = stopBy is StopRequester.Dagline;
        var running = snapshot.Counts[NodeStatus.Running];
        var line = StyledLine.Of(" STOPPING ", _badge).Add(" ");
        if (byDagline)
        {
            line = line.Add("the journal could not be written", Palette.Failed).Add($" {glyphs.Separator} ", Palette.Dim);
        }

        line = line.Add($"waiting for {DisplayText.Count(running, "running task", "running tasks")} to end", Palette.Cancelled);
        if (stop.Deadline is { } deadline)
        {
            line = line.Add($" {glyphs.Separator} ", Palette.Dim).Add($"{DisplayText.Duration(deadline - now)} left", Palette.Bold);
        }

        // After a stop Dagline asked for, the app's first request already ends the wait.
        var again = byDagline ? "Ctrl+C to stop waiting" : "Ctrl+C again to stop waiting";
        var action = StyledLine.Of($" {glyphs.Separator} ", Palette.Dim).Add(again, Palette.Dim);
        return [line.Fit(Math.Max(0, width - action.Width), glyphs.Ellipsis).Add(action)];
    }
}
