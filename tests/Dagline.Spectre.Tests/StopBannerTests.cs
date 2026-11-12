using Dagline.TestSupport;

namespace Dagline.Spectre.Tests;

public class StopBannerTests
{
    [Test]
    public async Task After_a_stop_a_banner_under_the_header_counts_down_the_grace_period()
    {
        var a = new PausedBody();
        var b = new PausedBody();
        var stop = new StopSignal();
        var builder = new GraphBuilder();
        builder.Task("a", a.Body);
        builder.Task("b", b.Body);
        builder.Task("c", _ => Task.CompletedTask);

        await using var display = await DisplayRun.StartAsync(builder.Build(), 100, 24, stop: stop);
        await a.Started;
        await b.Started;
        await display.Handled("c: running -> succeeded");
        var before = display.NextFrame();
        stop.Request();
        await display.Handled("stop requested");
        var stopped = display.NextFrame();
        display.Clock.Advance(TimeSpan.FromSeconds(4) - (DisplayRun.RedrawInterval * 2));
        var later = display.NextFrame();
        a.Release();
        b.Release();
        await display.Run;

        before.ShouldNotContain(line => line.Contains("STOPPING", StringComparison.Ordinal));
        stopped.ShouldBe(
        [
            Pinned("Dagline  no journal", "33%  elapsed 0s", 100),
            new string('━', 100),
            Pinned("✓ 1  ● 2", "cap 2/16", 100),
            " STOPPING  waiting for 2 running tasks to end · 29s left · Ctrl+C again to stop waiting",
            "── Groups " + new string('─', 90),
            "● a 0/1",
            "● b 0/1",
            "✓ c 1/1",
            "── Working (2) " + new string('─', 85),
            Pinned("● a", "0s", 100),
            Pinned("● b", "0s", 100),
        ]);
        later[3].ShouldBe(" STOPPING  waiting for 2 running tasks to end · 26s left · Ctrl+C again to stop waiting");
    }

    [Test]
    public async Task An_unlimited_grace_period_has_no_time_left_and_one_running_task_is_counted_as_one()
    {
        var a = new PausedBody();
        var stop = new StopSignal();
        var builder = new GraphBuilder();
        builder.Task("a", a.Body);
        var options = new RunOptions { Journal = Journal.None, GracePeriod = Timeout.InfiniteTimeSpan };

        await using var display = await DisplayRun.StartAsync(builder.Build(), 100, 24, options, stop);
        await a.Started;
        stop.Request();
        await display.Handled("stop requested");
        display.Clock.Advance(TimeSpan.FromMinutes(1));
        var frame = display.NextFrame();
        a.Release();
        await display.Run;

        frame[3].ShouldBe(" STOPPING  waiting for 1 running task to end · Ctrl+C again to stop waiting");
    }

    /// <summary><paramref name="left"/> at the start of a line <paramref name="width"/> cells wide and <paramref name="right"/> at its end.</summary>
    private static string Pinned(string left, string right, int width) => left + new string(' ', width - left.Length - right.Length) + right;
}
