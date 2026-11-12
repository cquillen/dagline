using Spectre.Console;

namespace Dagline.Spectre;

/// <summary>
/// The one home of a bar split by status: the settled and running tasks by status, earlier successes first and dimmer,
/// then the rest of the tasks empty. The header draws one for the whole run and the groups pane one for a row that has more
/// children than cells.
/// </summary>
internal static class StatusBar
{
    /// <summary>A bar <paramref name="width"/> cells wide over the tasks that <paramref name="counts"/> counts.</summary>
    /// <param name="counts">How many tasks have each status, and the earlier successes and waiting among them.</param>
    /// <param name="width">How many cells the bar takes.</param>
    /// <param name="glyphs">The glyphs to draw with.</param>
    public static StyledLine Of(TaskCounts counts, int width, Glyphs glyphs)
    {
        var tasks = counts.Total;
        (int Tasks, Style Style)[] shares =
        [
            (counts.EarlierSuccesses, Palette.EarlierSuccess),
            (counts[NodeStatus.Succeeded] - counts.EarlierSuccesses, Palette.Succeeded),
            (counts[NodeStatus.Failed], Palette.Failed),
            (counts[NodeStatus.Blocked], Palette.Blocked),
            (counts[NodeStatus.Cancelled], Palette.Cancelled),
            (counts.Working, Palette.Working),
            (counts.Waiting, Palette.Waiting),
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
}
