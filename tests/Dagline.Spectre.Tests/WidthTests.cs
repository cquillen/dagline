using Dagline.TestSupport;

namespace Dagline.Spectre.Tests;

public class WidthTests
{
    [Test]
    public async Task At_60_columns_long_text_is_cut_with_an_ellipsis_paths_keep_their_start_and_times_are_kept()
    {
        var totals = new PausedBody();
        var extract = new PausedBody();
        var builder = new GraphBuilder();
        var check = builder.Group("validate").Title("a group title far too long to fit in its column at all").Group("shard-04").Group("tenant-035");
        check.Task("totals-across-every-entity", context =>
        {
            context.Count(1928, 4087);
            context.Status("summing entities\nacross every ledger");
            return totals.Body(context);
        });
        check.Task("broken-beyond-repair-entirely", context => throw context.Fail("a message that is much longer than the sixty columns of this console"));
        builder.Group("process").Group("shard-02").Task("extract-the-whole-history", async context =>
        {
            using var wait = context.Waiting("external job 48067 running on the remote side, queued behind others");
            await extract.Body(context);
        });
        var options = new RunOptions { Journal = Journal.None };

        await using var display = await DisplayRun.StartAsync(builder.Build(), 60, 20, options);
        await totals.Started;
        await display.Handled("waiting process/shard-02/extract-the-whole-history: external job 48067 running on the remote side, queued behind others");
        await display.Handled("validate/shard-04/tenant-035/broken-beyond-repair-entirely: running -> failed");
        display.Clock.Advance(TimeSpan.FromMinutes(27) - DisplayRun.RedrawInterval);
        var frame = display.NextFrame();
        var screen = display.Screen;
        totals.Release();
        extract.Release();
        await display.Run;

        screen.Overflowed.ShouldBeFalse();
        frame.ShouldAllBe(line => line.Length <= 60);
        frame.Skip(6).ShouldBe(
        [
            "── Working (1) " + new string('─', 45),
            "● validate/shard-04/tenant…  ━━━━━━━───────── 1,928 … 27m 0s",
            "── Waiting on external systems (1) longest 27m 0s " + new string('─', 10),
            "◌ process/shard-02/extract…  external job 48067 runn… 27m 0s",
            "── Problems (1 failed) " + new string('─', 37),
            "✗ validate/shard-04/tenant-035/broken-beyond-repai… 04:12:00",
            "    a message that is much longer than the sixty columns of…",
        ]);
    }
}
