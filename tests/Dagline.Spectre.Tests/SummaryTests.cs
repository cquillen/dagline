using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Spectre.Console;
using Spectre.Console.Testing;

namespace Dagline.Spectre.Tests;

public class SummaryTests
{
    [Test]
    public async Task A_complete_run_with_no_journal_and_no_warnings_prints_only_its_outcome_duration_and_counts()
    {
        var a = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("a", a.Body);
        builder.Task("b", _ => Task.CompletedTask);

        await using var display = await DisplayRun.StartAsync(builder.Build(), 60, 20);
        await a.Started;
        display.Clock.Advance(TimeSpan.FromSeconds(75));
        a.Release();
        var summary = await display.EndAsync();

        summary.ShouldBe(
        [
            new string('─', 60),
            "Run complete  1m 15s",
            "✓ 2 succeeded",
        ]);
    }

    [Test]
    public async Task A_continuing_run_names_its_journal_and_says_what_a_later_run_has_left_and_what_was_set_aside()
    {
        using var temporary = new TemporaryDirectory();
        var first = new GraphBuilder();
        first.Task("a", context =>
        {
            context.AddWarning("entity 9 has no region");
            return Task.CompletedTask;
        });
        var firstB = first.Task("b", context => throw context.Fail("2 bad entities"));
        first.Task("report", _ => Task.CompletedTask).After(firstB);
        first.Task("old", context => throw context.Fail("no entities"));
        first.Task("gone", _ => Task.CompletedTask);
        var earlier = await Runner.RunAsync(first.Build(), new RunOptions { Journal = Journal.New(temporary.Path, "all shards") });
        var id = earlier.Snapshot.Journal!.Id;
        var second = new GraphBuilder();
        second.Task("a", _ => Task.CompletedTask);
        var b = second.Task("b", context =>
        {
            context.AddWarning("entity 4 has a bad date");
            throw context.Fail("1 bad entity");
        });
        second.Task("report", _ => Task.CompletedTask).After(b);
        var options = new RunOptions { Journal = Journal.Continue(temporary.Path, id) };

        await using var display = await DisplayRun.StartAsync(second.Build(), 100, 20, options);
        var summary = await display.EndAsync();

        summary.ShouldBe(
        [
            new string('─', 100),
            $"Run incomplete  0s · journal {id} · run 2",
            "✓ 2 succeeded (1 earlier)  ✗ 1 failed  ▲ 2 warnings",
            "",
            "Failures (1)",
            "✗ b  reported failure · 04:12:00 · ran 0s",
            "    1 bad entity",
            "",
            "Warnings (2 on 2 tasks)",
            "▲ b  1",
            "    entity 4 has a bad date",
            "▲ 1 warning on 1 task from earlier runs, not listed again",
            "",
            $"A run that continues journal {id} has 1 task left to run.",
            "1 earlier success set aside to run again:",
            "    report",
            "2 journal paths not in the graph (1 not succeeded)",
        ]);
    }

    [Test]
    public async Task A_run_that_ended_with_an_error_says_so_and_why_in_place_of_a_run_outcome()
    {
        // No real file can be made to fail, so the end of a run whose journal could not be written is handed to the display
        // here, with the final snapshot of a real run.
        var clock = new FakeTimeProvider(DisplayRun.RunStart);
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask);
        var recording = new RecordingReporter();
        var final = (await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, TimeProvider = clock, Reporters = [recording] })).Snapshot;
        using var console = new TestConsole().Size(new Size(60, 20)).EmitAnsiSequences();
        var display = SpectreReporter.Create(new SpectreOptions { Display = DisplayMode.Force, Console = console, TimeProvider = clock });

        await display.OnEventAsync(recording.Events.OfType<RunStarted>().Single(), final, CancellationToken.None);
        await display.OnEventAsync(
            FailedJournalWrite.RunEnded(
                DisplayRun.RunStart.AddSeconds(5),
                new InvalidOperationException("The run's journal can no longer be written: disk full")),
            final,
            CancellationToken.None);

        Screen.Read(console.Output, new Size(60, 20), []).Ordinary.ShouldBe(
        [
            new string('─', 60),
            "Run ended with an error  0s",
            "    The run's journal can no longer be written: disk full",
            "✓ 1 succeeded",
        ]);
    }

    [Test]
    public async Task Each_task_with_warnings_lists_those_it_kept_and_counts_those_it_did_not()
    {
        string[] warnings = [.. Enumerable.Range(1, 103).Select(entity => $"entity {entity} has no region")];
        var builder = new GraphBuilder();
        builder.Task("load", context =>
        {
            foreach (var warning in warnings)
            {
                context.AddWarning(warning);
            }

            return Task.CompletedTask;
        });
        builder.Task("store", context =>
        {
            context.AddWarning("entity 3 has a bad date");
            return Task.CompletedTask;
        });

        await using var display = await DisplayRun.StartAsync(builder.Build(), 100, 20);
        var summary = await display.EndAsync();

        summary.ShouldBe(
        [
            new string('─', 100),
            "Run complete  0s",
            "✓ 2 succeeded  ▲ 104 warnings",
            "",
            "Warnings (104 on 2 tasks)",
            "▲ load  103",
            .. warnings.Take(100).Select(warning => $"    {warning}"),
            "    and 3 more warnings that were counted but not kept",
            "▲ store  1",
            "    entity 3 has a bad date",
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
            "1 task was not reached",
        ]);
        summary.ShouldNotContain(line => line.Contains("exit", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task Each_failure_is_listed_in_full_in_declaration_order_with_the_roots_of_what_it_blocked()
    {
        string[] detail = [.. Enumerable.Range(1, 103).Select(entity => $"entity {entity}: bad date")];
        var check = new PausedBody();
        var builder = new GraphBuilder();
        var checkTask = builder.Task("check", async context =>
        {
            await check.Body(context);
            context.SetStatusText("checking entities\nof shard-3");
            context.SetCount(17, 40);
            throw context.Fail("103 bad entities", detail);
        });
        builder.Task("one", _ => Task.CompletedTask).Requires(checkTask);
        var store = builder.Group("store").Requires(checkTask);
        store.Task("a", _ => Task.CompletedTask);
        store.Task("b", _ => Task.CompletedTask);
        var load = builder.Task("load", _ => throw new InvalidOperationException("no entities to load"));

        await using var display = await DisplayRun.StartAsync(builder.Build(), 100, 20);
        await check.Started;
        await display.Handled("load: running -> failed");
        display.Clock.Advance(TimeSpan.FromSeconds(3));
        check.Release();
        var summary = await display.EndAsync();

        var loadDetail = (await display.Run).Snapshot[load.Path].Detail;
        summary.ShouldBe(
        [
            new string('─', 100),
            "Run incomplete  3s",
            "✗ 2 failed  ⊘ 3 blocked",
            "",
            "Failures (2)",
            "✗ check  reported failure · 04:12:03 · ran 3s · got to checking entities of shard-3, 17 of 40",
            "    103 bad entities",
            .. detail.Take(100).Select(line => $"    {line}"),
            "    and 3 more lines that were counted but not kept",
            "    ⊘ blocked 3 tasks: one, store (2)",
            "✗ load  unexpected failure · 04:12:00 · ran 0s",
            "    no entities to load",
            .. loadDetail.Select(line => $"    {line}"),
        ]);
    }

    [Test]
    public async Task A_stopped_run_lists_each_cancelled_task_with_its_message_and_counts_the_tasks_it_never_reached()
    {
        for (var repeat = 0; repeat < 50; repeat++)
        {
            var load = new StoppedBody(context =>
            {
                context.SetMessage("loaded 17 entities");
                context.CancellationToken.ThrowIfCancellationRequested();
            });
            var store = new StoppedBody();
            var slow = new PausedBody();
            var builder = new GraphBuilder();
            var loadTask = builder.Task("load", load.Body);
            builder.Task("store", store.Body);
            builder.Task("slow", slow.Body);
            builder.Task("publish", _ => Task.CompletedTask).Requires(loadTask);
            builder.Task("report", _ => Task.CompletedTask).After(loadTask);
            var stop = new StopSignal();

            await using var display = await DisplayRun.StartAsync(builder.Build(), 100, 20, stop: stop);
            await load.Started;
            await store.Started;
            await slow.Started;
            display.Clock.Advance(TimeSpan.FromSeconds(2));
            stop.Request();
            await display.Handled("load: running -> cancelled");
            await display.Handled("store: running -> cancelled");
            stop.Request();
            var summary = await display.EndAsync();
            slow.Release();

            summary.ShouldBe(
            [
                new string('─', 100),
                "Run stopped  2s",
                "○ 1 ready  · 1 pending  ■ 3 cancelled",
                "2 tasks were not reached",
                "",
                "Cancelled (3)",
                "■ load  loaded 17 entities",
                "■ store",
                "■ slow  still running when the run stopped waiting",
            ]);
        }
    }
}
