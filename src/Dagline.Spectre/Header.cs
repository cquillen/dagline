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
        StyledLine.Spread(Title(snapshot, glyphs), Progress(snapshot, now), width, glyphs.Ellipsis),
        Bar(snapshot, width, glyphs),
        StyledLine.Spread(Tally.Short(snapshot, glyphs), Slots(snapshot, glyphs), width, glyphs.Ellipsis),
    ];

    private static StyledLine Title(Snapshot snapshot, Glyphs glyphs)
    {
        var title = StyledLine.Of("Dagline", Palette.Bold);
        if (snapshot.Journal is not { } journal)
        {
            return title.Add("  no journal", Palette.Dim);
        }

        if (journal.Description is { } description)
        {
            title = title.Add("  " + description);
        }

        return title.Add($"  journal {journal.Id} {glyphs.Separator} run {DisplayText.Number(journal.Run)}", Palette.Dim);
    }

    private static StyledLine Progress(Snapshot snapshot, DateTimeOffset now)
    {
        var tasks = snapshot.Counts.Values.Sum();
        var unsettled = snapshot.Counts[NodeStatus.Pending] + snapshot.Counts[NodeStatus.Ready] + snapshot.Counts[NodeStatus.Running];
        var percent = tasks == 0 ? 100 : (tasks - unsettled) * 100 / tasks;
        var elapsed = (snapshot.Ended ?? now) - snapshot.Started;
        return StyledLine.Of($"{percent}%", Palette.Bold)
            .Add("  elapsed ", Palette.Dim)
            .Add(DisplayText.Duration(elapsed), Palette.Bold);
    }

    /// <summary>One bar for the whole run: the settled and running tasks by status, then the rest of the tasks empty.</summary>
    private static StyledLine Bar(Snapshot snapshot, int width, Glyphs glyphs)
    {
        var counts = snapshot.Counts;
        var tasks = counts.Values.Sum();
        var waiting = snapshot.Nodes.Count(Running.IsWaiting);
        (int Tasks, Style Style)[] shares =
        [
            (snapshot.EarlierSuccesses, Palette.EarlierSuccess),
            (counts[NodeStatus.Succeeded] - snapshot.EarlierSuccesses, Palette.Succeeded),
            (counts[NodeStatus.Failed], Palette.Failed),
            (counts[NodeStatus.Blocked], Palette.Blocked),
            (counts[NodeStatus.Cancelled], Palette.Cancelled),
            (counts[NodeStatus.Running] - waiting, Palette.Working),
            (waiting, Palette.Waiting),
        ];

        var bar = StyledLine.Empty;
        var before = 0;
        foreach (var (share, style) in shares)
        {
            // Each share ends where its running total falls, so the cells always add up to the width.
            var cells = tasks == 0 ? 0 : (int)((long)(before + share) * width / tasks) - (int)((long)before * width / tasks);
            bar = bar.Add(DisplayText.Repeat(glyphs.Bar, cells), style);
            before += share;
        }

        return bar.Add(DisplayText.Repeat(glyphs.BarRest, width - bar.Width), Palette.Dim);
    }

    private static StyledLine Slots(Snapshot snapshot, Glyphs glyphs)
    {
        var slots = StyledLine.Of(
            $"cap {DisplayText.Number(snapshot.Cap.InUse)}/{DisplayText.Number(snapshot.Cap.Size)}",
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
