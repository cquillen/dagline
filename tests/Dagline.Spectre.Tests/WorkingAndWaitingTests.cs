using Dagline.TestSupport;

namespace Dagline.Spectre.Tests;

public class WorkingAndWaitingTests
{
    [Test]
    public async Task The_working_and_waiting_panes_show_what_the_run_is_doing_now()
    {
        var gate = new PausedBody();
        var totals = new PausedBody();
        var duplicates = new PausedBody();
        var crossCheck = new PausedBody();
        var extract = new PausedBody();
        var builder = new GraphBuilder();
        var first = builder.Task("gate", gate.Body);
        var check = builder.Group("check");
        check.Task("totals", context =>
        {
            context.Count(1928, 4087);
            context.Status("summing entities");
            return totals.Body(context);
        });
        check.Task("duplicates", duplicates.Body);
        check.Task("cross-check", context =>
        {
            context.Count(2602, 6068);
            context.Status("cross-checking");
            return crossCheck.Body(context);
        }).After(first);
        builder.Group("process").Task("extract", async context =>
        {
            context.Status("job 48067 running");
            using var wait = context.Waiting("external job");
            await extract.Body(context);
        });

        await using var display = await DisplayRun.StartAsync(builder.Build(), 100, 32);
        await totals.Started;
        await duplicates.Started;
        await display.Handled("waiting process/extract: external job");
        display.Clock.Advance(TimeSpan.FromSeconds(5));
        gate.Release();
        await crossCheck.Started;
        display.Clock.Advance(TimeSpan.FromSeconds(45) - DisplayRun.RedrawInterval);
        var frame = display.NextFrame();
        totals.Release();
        duplicates.Release();
        crossCheck.Release();
        extract.Release();
        await display.Run;

        frame.ShouldBe(
        [
            "Dagline  no journal" + new string(' ', 65) + "20%  elapsed 50s",
            new string('━', 100),
            "✓ 1  ● 3  ◌ 1                                                                               cap 4/16",
            "── Groups " + new string('─', 90),
            "✓ gate    1/1",
            "● check   ●●● 0/3",
            "◌ process ◌ 0/1",
            "── Working (3) " + new string('─', 85),
            "● check/totals       ━━━━━━━───────── 1,928 of 4,087  summing entities                           50s",
            "● check/duplicates                                                                               50s",
            "● check/cross-check  ━━━━━━────────── 2,602 of 6,068  cross-checking                             45s",
            "── Waiting on external systems (1) longest 50s " + new string('─', 53),
            "◌ process/extract    job 48067 running                                                           50s",
        ]);
    }

    [Test]
    public async Task A_task_with_a_count_and_no_total_shows_the_count_and_no_bar_and_one_with_no_count_shows_neither()
    {
        var a = new PausedBody();
        var b = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("a", context =>
        {
            context.Count(42);
            context.Status("reading\nfile 3");
            return a.Body(context);
        });
        builder.Task("b", b.Body);

        await using var display = await DisplayRun.StartAsync(builder.Build(), 80, 24);
        await a.Started;
        await b.Started;
        var frame = display.NextFrame();
        a.Release();
        b.Release();
        await display.Run;

        frame.SkipWhile(line => !line.StartsWith("── Working", StringComparison.Ordinal)).ShouldBe(
        [
            "── Working (2) " + new string('─', 65),
            "● a  " + new string(' ', 16) + " 42  reading file 3" + new string(' ', 38) + "0s",
            "● b" + new string(' ', 75) + "0s",
        ]);
    }

    [Test]
    public async Task The_times_running_advance_with_the_clock()
    {
        var a = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("a", a.Body);

        await using var display = await DisplayRun.StartAsync(builder.Build(), 80, 24);
        await a.Started;
        var before = display.NextFrame();
        display.Clock.Advance(TimeSpan.FromSeconds(83));
        var after = display.NextFrame();
        a.Release();
        await display.Run;

        before[^1].ShouldEndWith(" 0s");
        after[^1].ShouldEndWith(" 1m 23s");
    }

    [Test]
    public async Task When_more_tasks_are_working_than_fit_the_pane_says_how_many_it_left_out()
    {
        var bodies = Enumerable.Range(0, 12).Select(_ => new PausedBody()).ToArray();
        var builder = new GraphBuilder();
        var group = builder.Group("g");
        for (var i = 0; i < bodies.Length; i++)
        {
            group.Task($"t{i + 1}", bodies[i].Body);
        }

        await using var display = await DisplayRun.StartAsync(builder.Build(), 80, 15);
        foreach (var body in bodies)
        {
            await body.Started;
        }

        var frame = display.NextFrame();
        foreach (var body in bodies)
        {
            body.Release();
        }

        await display.Run;

        frame.SkipWhile(line => !line.StartsWith("── Working", StringComparison.Ordinal)).ShouldBe(
        [
            "── Working (12) " + new string('─', 64),
            .. Enumerable.Range(1, 8).Select(i => $"● g/t{i}" + new string(' ', 72) + "0s"),
            "  and 4 more working",
        ]);
    }

    [Test]
    public async Task The_waiting_pane_lists_the_longest_waits_first_and_says_how_many_it_left_out()
    {
        WaitingBody[] waits =
        [
            new("job 1", status: "job 48067 running"),
            new("job 2\nqueued"),
            new("job 3"),
            new("job 4"),
            new("job 5"),
            new("job 6"),
            new("job 7"),
        ];
        var builder = new GraphBuilder();
        var load = builder.Group("load");
        for (var i = 0; i < waits.Length; i++)
        {
            load.Task($"w{i + 1}", waits[i].Body);
        }

        await using var display = await DisplayRun.StartAsync(builder.Build(), 80, 32);
        foreach (var (wait, line) in new[]
        {
            (waits[2], "waiting load/w3: job 3"),
            (waits[0], "waiting load/w1: job 1"),
            (waits[1], "waiting load/w2: job 2\nqueued"),
            (waits[3], "waiting load/w4: job 4"),
            (waits[4], "waiting load/w5: job 5"),
            (waits[5], "waiting load/w6: job 6"),
            (waits[6], "waiting load/w7: job 7"),
        })
        {
            display.Clock.Advance(TimeSpan.FromSeconds(10));
            wait.Begin();
            await display.Handled(line);
        }

        var frame = display.NextFrame();
        foreach (var wait in waits)
        {
            wait.Release();
        }

        await display.Run;

        frame.SkipWhile(line => !line.StartsWith("── Working", StringComparison.Ordinal)).ShouldBe(
        [
            "── Working (0) all 7 running tasks are waiting " + new string('─', 33),
            "── Waiting on external systems (7) longest 1m 0s " + new string('─', 31),
            Row("◌ load/w3  job 3", "1m 0s"),
            Row("◌ load/w1  job 48067 running", "50s"),
            Row("◌ load/w2  job 2 queued", "40s"),
            Row("◌ load/w4  job 4", "30s"),
            Row("◌ load/w5  job 5", "20s"),
            "  and 2 more, the newest waiting 0s",
        ]);

        static string Row(string left, string time) => left + new string(' ', 80 - left.Length - time.Length) + time;
    }

    [Test]
    public async Task The_waiting_pane_is_not_shown_when_no_task_is_waiting_and_the_working_pane_not_when_none_is_running()
    {
        var a = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("a", a.Body);
        builder.Task("b", _ => Task.CompletedTask).Requires(builder.Task("c", _ => Task.CompletedTask));

        await using var display = await DisplayRun.StartAsync(builder.Build(), 80, 24);
        await display.Handled("b: running -> succeeded");
        var running = display.NextFrame();
        a.Release();
        await display.Run;

        running.ShouldContain(line => line.StartsWith("── Working (1) ", StringComparison.Ordinal));
        running.ShouldNotContain(line => line.StartsWith("── Waiting", StringComparison.Ordinal));
        running[^1].ShouldStartWith("● a ");
    }

    /// <summary>A task body that begins waiting when the test says so, then pauses until released.</summary>
    private sealed class WaitingBody(string text, string? status = null)
    {
        private readonly TaskCompletionSource _begin = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly PausedBody _paused = new();

        public void Begin() => _begin.TrySetResult();

        public void Release()
        {
            Begin();
            _paused.Release();
        }

        public async Task Body(TaskContext context)
        {
            await _begin.Task;
            if (status is not null)
            {
                context.Status(status);
            }

            using var wait = context.Waiting(text);
            await _paused.Body(context);
        }
    }
}
