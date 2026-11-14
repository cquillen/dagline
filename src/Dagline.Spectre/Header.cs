using Spectre.Console;

namespace Dagline.Spectre;

/// <summary>The three lines at the top of every frame, about the whole run.</summary>
internal static class Header
{
    /// <summary>The header for a run as <paramref name="snapshot"/> shows it, <paramref name="width"/> cells wide.</summary>
    /// <param name="snapshot">The run.</param>
    /// <param name="width">The width of the console.</param>
    /// <param name="now">The time on the display's clock, which the elapsed time runs to while the run has not ended.</param>
    /// <param name="glyphs">The glyphs to draw with.</param>
    public static IReadOnlyList<StyledLine> Lines(Snapshot snapshot, int width, DateTimeOffset now, Glyphs glyphs) =>
    [
        StyledLine.PinRight(Title(snapshot, glyphs), Progress(snapshot, now), width, glyphs.Ellipsis),
        Bar(snapshot, width, glyphs),
        StyledLine.Spread(Tally.Short(snapshot, glyphs), Slots(snapshot, glyphs), width, glyphs.Ellipsis),
    ];

    private static StyledLine Title(Snapshot snapshot, Glyphs glyphs)
    {
        var title = StyledLine.Of("Dagline", Palette.Bold);
        if (snapshot.Journal is not { } journal)
        {
            return title.Add(DisplayText.Between + "no journal", Palette.Dim);
        }

        if (journal.Description is { } description)
        {
            title = title.Add(DisplayText.Between + glyphs.Text(description));
        }

        return title.Add($"{DisplayText.Between}journal {journal.Id} {glyphs.Separator} run {DisplayText.Number(journal.Run)}", Palette.Dim);
    }

    private static StyledLine Progress(Snapshot snapshot, DateTimeOffset now)
    {
        var tasks = snapshot.Counts.Total;
        var percent = tasks == 0 ? 100 : snapshot.Counts.Settled * 100 / tasks;
        var elapsed = (snapshot.Ended ?? now) - snapshot.Started;
        return StyledLine.Of($"{percent}%", Palette.Bold)
            .Add(DisplayText.Between + "elapsed ", Palette.Dim)
            .Add(DisplayText.Duration(elapsed), Palette.Bold);
    }

    /// <summary>One bar for the whole run: the settled and running tasks by status, then the rest of the tasks empty.</summary>
    private static StyledLine Bar(Snapshot snapshot, int width, Glyphs glyphs)
        => StatusBar.Of(snapshot.Counts, width, glyphs);

    private static StyledLine Slots(Snapshot snapshot, Glyphs glyphs)
    {
        var slots = StyledLine.Of(
            $"cap {DisplayText.Number(snapshot.RunningTasks.InUse)}/{DisplayText.Number(snapshot.RunningTasks.Size)}",
            Palette.Dim);
        var full = snapshot.Limits.Values.Count(limit => limit.InUse >= limit.Size);
        if (full > 0)
        {
            slots = slots.Add($" {glyphs.Separator} {DisplayText.Number(full)} of {DisplayText.Number(snapshot.Limits.Count)} limits full", Palette.Dim);
        }

        var held = snapshot.Counts[NodeStatus.Ready];
        if (held > 0)
        {
            slots = slots.Add($" {glyphs.Separator} {DisplayText.Number(held)} ready, held");
        }

        return slots;
    }
}
