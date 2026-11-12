using Dagline.TestSupport;

namespace Dagline.Spectre.Tests;

public class SummaryTests
{
    [Test]
    public async Task A_complete_run_with_no_journal_ends_with_its_outcome_duration_and_counts()
    {
        var a = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("a", a.Body);
        builder.Task("b", context =>
        {
            context.Warn("odd");
            return Task.CompletedTask;
        });

        await using var display = await DisplayRun.StartAsync(builder.Build(), 60, 20);
        await a.Started;
        display.Clock.Advance(TimeSpan.FromSeconds(75));
        a.Release();
        var summary = await display.EndAsync();

        summary.ShouldBe(
        [
            new string('─', 60),
            "Run complete  1m 15s",
            "✓ 2 succeeded  ▲ 1 warning",
        ]);
    }

    [Test]
    public async Task An_incomplete_run_on_a_continued_journal_names_the_journal_and_counts_earlier_successes_apart()
    {
        using var temporary = new TemporaryDirectory();
        var first = new GraphBuilder();
        first.Task("a", _ => Task.CompletedTask);
        first.Task("b", _ => throw new InvalidOperationException("not yet"));
        var earlier = await Runner.RunAsync(first.Build(), new RunOptions { Journal = Journal.New(temporary.Path, "all shards") });
        var id = earlier.Snapshot.Journal!.Id;
        var second = new GraphBuilder();
        second.Task("a", _ => Task.CompletedTask);
        var b = second.Task("b", context =>
        {
            context.Warn("first");
            context.Warn("second");
            throw new InvalidOperationException("still not");
        });
        second.Task("c", _ => Task.CompletedTask).Requires(b);
        var options = new RunOptions { Journal = Journal.Continue(temporary.Path, id) };

        await using var display = await DisplayRun.StartAsync(second.Build(), 60, 20, options);
        var summary = await display.EndAsync();

        summary.ShouldBe(
        [
            new string('─', 60),
            $"Run incomplete  0s · journal {id} · run 2",
            "✓ 1 succeeded (1 earlier)  ✗ 1 failed  ⊘ 1 blocked  ▲ 2 warnings",
        ]);
    }

    [Test]
    public async Task A_stopped_run_counts_the_tasks_it_left_and_gives_no_exit_code()
    {
        var a = new PausedBody();
        var builder = new GraphBuilder();
        var first = builder.Task("a", a.Body);
        builder.Task("b", _ => Task.CompletedTask).Requires(first);
        var stop = new StopSignal();

        await using var display = await DisplayRun.StartAsync(builder.Build(), 60, 20, stop: stop);
        await a.Started;
        stop.Request();
        await display.Handled("stop requested");
        a.Release();
        var summary = await display.EndAsync();

        summary.ShouldBe(
        [
            new string('─', 60),
            "Run stopped  0s",
            "✓ 1 succeeded  ○ 1 ready",
        ]);
        summary.ShouldNotContain(line => line.Contains("exit", StringComparison.OrdinalIgnoreCase));
    }
}
