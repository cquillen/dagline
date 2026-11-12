using Dagline.TestSupport;

namespace Dagline.Spectre.Tests;

public class HeaderTests
{
    [Test]
    public async Task The_header_shows_no_journal_the_counts_apart_and_the_cap_with_the_held_tasks()
    {
        var a = new PausedBody();
        var b = new PausedBody();
        var builder = new GraphBuilder();
        var first = builder.Task("a", a.Body);
        builder.Task("b", async context =>
        {
            using var wait = context.Waiting("job 7");
            await b.Body(context);
        });
        builder.Task("c", _ => Task.CompletedTask).Requires(first);
        builder.Task("d", _ => Task.CompletedTask);
        var options = new RunOptions { Journal = Journal.None, Cap = 2 };

        await using var display = await DisplayRun.StartAsync(builder.Build(), 80, 24, options);
        await display.Handled("waiting b: job 7");
        var frame = display.NextFrame();
        a.Release();
        b.Release();
        await display.Run;

        frame.Take(3).ShouldBe(
        [
            "Dagline  no journal                                               0%  elapsed 0s",
            new string('━', 40) + new string('─', 40),
            "● 1  ◌ 1  ○ 1  · 1                                       cap 2/2 · 1 ready, held",
        ]);
    }

    [Test]
    public async Task The_header_counts_failed_blocked_and_warnings_and_settled_tasks_in_the_percentage()
    {
        var c = new PausedBody();
        var builder = new GraphBuilder();
        var a = builder.Task("a", context =>
        {
            context.Warn("first");
            context.Warn("second");
            throw context.Fail("broken");
        });
        builder.Task("b", _ => Task.CompletedTask).Requires(a);
        builder.Task("c", c.Body);

        await using var display = await DisplayRun.StartAsync(builder.Build(), 80, 24);
        await display.Handled("b: pending -> blocked");
        var frame = display.NextFrame();
        c.Release();
        await display.Run;

        frame.Take(3).ShouldBe(
        [
            "Dagline  no journal                                              66%  elapsed 0s",
            new string('━', 80),
            "● 1  ✗ 1  ⊘ 1  ▲ 2                                                      cap 1/16",
        ]);
    }

    [Test]
    public async Task The_header_shows_how_many_limits_are_full()
    {
        var a = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("a", a.Body).Limit("db");
        builder.Task("b", _ => Task.CompletedTask).Limit("files");
        var options = new RunOptions
        {
            Journal = Journal.None,
            Limits = new Dictionary<string, int> { ["db"] = 1, ["files"] = 2 },
        };

        await using var display = await DisplayRun.StartAsync(builder.Build(), 80, 24, options);
        await display.Handled("b: running -> succeeded");
        var frame = display.NextFrame();
        a.Release();
        await display.Run;

        frame[2].ShouldBe("✓ 1  ● 1                                           cap 1/16 · 1 of 2 limits full");
    }

    [Test]
    public async Task The_header_shows_the_journal_its_description_and_the_run_number()
    {
        using var temporary = new TemporaryDirectory();
        var a = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("a", a.Body);
        var options = new RunOptions { Journal = Journal.New(temporary.Path, "all shards") };

        await using var display = await DisplayRun.StartAsync(builder.Build(), 80, 24, options);
        await a.Started;
        var frame = display.NextFrame();
        a.Release();
        var result = await display.Run;

        var left = $"Dagline  all shards  journal {result.Snapshot.Journal!.Id} · run 1";
        frame[0].ShouldBe(left + new string(' ', 80 - left.Length - 14) + "0%  elapsed 0s");
    }

    [Test]
    public async Task A_continuing_run_counts_its_earlier_successes_apart()
    {
        using var temporary = new TemporaryDirectory();
        var first = new GraphBuilder();
        first.Task("a", _ => Task.CompletedTask);
        first.Task("b", _ => throw new InvalidOperationException("not yet"));
        var earlier = await Runner.RunAsync(first.Build(), new RunOptions { Journal = Journal.New(temporary.Path) });
        var id = earlier.Snapshot.Journal!.Id;
        var b = new PausedBody();
        var second = new GraphBuilder();
        second.Task("a", _ => Task.CompletedTask);
        second.Task("b", b.Body);
        var options = new RunOptions { Journal = Journal.Continue(temporary.Path, id) };

        await using var display = await DisplayRun.StartAsync(second.Build(), 80, 24, options);
        await b.Started;
        var frame = display.NextFrame();
        b.Release();
        await display.Run;

        var left = $"Dagline  journal {id} · run 2";
        frame.Take(3).ShouldBe(
        [
            left + new string(' ', 80 - left.Length - 15) + "50%  elapsed 0s",
            new string('━', 80),
            "✓ 1 (1 earlier)  ● 1                                                    cap 1/16",
        ]);
    }

    [Test]
    public async Task The_elapsed_time_is_read_from_the_display_clock()
    {
        var a = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("a", a.Body);

        await using var display = await DisplayRun.StartAsync(builder.Build(), 80, 24);
        await a.Started;
        display.Clock.Advance(TimeSpan.FromSeconds(44.75));
        var seconds = display.NextFrame()[0];
        display.Clock.Advance(TimeSpan.FromMinutes(20));
        var minutes = display.NextFrame()[0];
        a.Release();
        await display.Run;

        seconds.ShouldEndWith("  0%  elapsed 45s");
        minutes.ShouldEndWith("  0%  elapsed 20m 45s");
    }
}
