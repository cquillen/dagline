namespace Dagline.Spectre;

/// <summary>Builds the summary the display leaves on the ordinary screen once the run has ended.</summary>
internal static class Summary
{
    /// <summary>The summary of a run that has ended.</summary>
    /// <param name="ended">The last event of the run, which carries its run outcome or its error.</param>
    /// <param name="snapshot">The final snapshot, handed with <paramref name="ended"/>.</param>
    /// <param name="width">The width of the console, which the rule above the summary fills.</param>
    /// <param name="glyphs">The glyphs to draw with.</param>
    public static IReadOnlyList<StyledLine> Lines(RunEnded ended, Snapshot snapshot, int width, Glyphs glyphs) =>
    [
        StyledLine.Of(string.Concat(Enumerable.Repeat(glyphs.Rule, Math.Max(1, width))), Palette.Dim),
        Outcome(ended, snapshot, glyphs),
        Tally.Long(snapshot, glyphs),
    ];

    private static StyledLine Outcome(RunEnded ended, Snapshot snapshot, Glyphs glyphs)
    {
        var line = ended.Outcome switch
        {
            RunOutcome.Complete => StyledLine.Of("Run complete", Palette.Succeeded),
            RunOutcome.Incomplete => StyledLine.Of("Run incomplete", Palette.Failed),
            RunOutcome.Stopped => StyledLine.Of("Run stopped", Palette.Cancelled),
            _ => StyledLine.Of("Run ended with an error", Palette.Failed),
        };

        line = line.Add("  " + DisplayText.Duration((snapshot.Ended ?? ended.At) - snapshot.Started), Palette.Bold);
        return snapshot.Journal is { } journal
            ? line.Add($" {glyphs.Separator} journal {journal.Id} {glyphs.Separator} run {DisplayText.Number(journal.Run)}", Palette.Dim)
            : line;
    }
}
