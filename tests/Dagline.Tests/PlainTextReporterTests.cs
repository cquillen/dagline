using System.Globalization;
using System.Text;
using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace Dagline.Tests;

public class PlainTextReporterTests
{
    private const int _repeats = 50;
    private static readonly DateTimeOffset _runStart = new(2026, 11, 11, 4, 12, 0, TimeSpan.Zero);

    [Test]
    public async Task A_run_of_the_reference_graph_prints_the_reference_output_line_for_line()
    {
        var clock = new FakeTimeProvider(_runStart);
        var writer = new StringWriter();
        var a = new PausedBody();
        var b = new PausedBody();
        var report = new PausedBody();
        var builder = new GraphBuilder();
        var checks = builder.Group("checks");
        checks.Task("a", a.Body);
        checks.Task("b", async context =>
        {
            await b.Body(context);
            throw context.Fail("2 bad entities", "entity 3: missing owner", "entity 17: bad date");
        });
        var publish = builder.Task("publish", _ => Task.CompletedTask).Requires(checks);
        builder.Task("notify", _ => Task.CompletedTask).Requires(publish);
        builder.Task("report", report.Body).After(checks);
        var reporter = new WatchedReporter(new PlainTextReporter(writer, clock));

        var run = Runner.RunAsync(builder.Build(), Options(clock, reporter));
        var checkA = await a.Started;
        var checkB = await b.Started;
        clock.Advance(TimeSpan.FromSeconds(2));
        checkA.AddWarning("entity 9 has no region");
        await reporter.Handled("warning checks/a: entity 9 has no region");
        clock.Advance(TimeSpan.FromSeconds(1.1));
        checkB.SetStatusText("checking entities");
        checkB.SetCount(17, 40);
        b.Release();
        await reporter.Handled("notify: pending -> blocked");
        clock.Advance(TimeSpan.FromSeconds(1.9));
        checkA.SetMessage("checked 40 entities");
        a.Release();
        await reporter.Handled("report: ready -> running");
        clock.Advance(TimeSpan.FromMinutes(1));
        clock.Advance(TimeSpan.FromSeconds(15));
        report.Release();
        var result = await run;

        result.Outcome.ShouldBe(RunOutcome.Incomplete);
        result.ReporterErrors.ShouldBeEmpty();
        Lines(writer).ShouldBe(
        [
            "2026-11-11T04:12:00Z run started: 5 tasks",
            "2026-11-11T04:12:00Z started checks/a",
            "2026-11-11T04:12:00Z started checks/b",
            "2026-11-11T04:12:02Z warning checks/a: entity 9 has no region",
            "2026-11-11T04:12:03Z failed checks/b in 3.1s: 2 bad entities",
            "    entity 3: missing owner",
            "    entity 17: bad date",
            "2026-11-11T04:12:03Z blocked 2 tasks by checks/b",
            "2026-11-11T04:12:05Z succeeded checks/a in 5.0s: checked 40 entities",
            "2026-11-11T04:12:05Z group checks failed: 1 succeeded, 1 failed",
            "2026-11-11T04:12:05Z started report",
            "2026-11-11T04:13:05Z tasks: 1 running, 1 succeeded, 1 failed, 2 blocked",
            "2026-11-11T04:13:20Z succeeded report in 1m15s",
            "2026-11-11T04:13:20Z run ended: incomplete in 1m20s",
            "tasks: 2 succeeded, 1 failed, 2 blocked",
            "failed: 1",
            "  checks/b (reported) at 2026-11-11T04:12:03Z after 3.1s",
            "    progress: checking entities, 17 of 40",
            "    message: 2 bad entities",
            "    detail:",
            "      entity 3: missing owner",
            "      entity 17: bad date",
            "    blocked 2 tasks: publish, notify",
            "warnings: 1",
            "  checks/a",
            "    entity 9 has no region",
        ]);
    }

    [Test]
    public async Task A_stopped_run_of_the_stop_reference_graph_prints_the_stop_reference_output_line_for_line()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var fake = new FakeTimeProvider(_runStart);
            var clock = new WatchedClock(fake);
            var writer = new StringWriter();
            var a = new PausedBody();
            var slow = new PausedBody();
            var jobStart = new PausedBody();
            var builder = new GraphBuilder();
            var aTask = builder.Task("a", a.Body);
            var job = builder.Task("job", async context =>
            {
                await jobStart.Body(context);
                await context.WaitUntilAsync(TimeSpan.FromMinutes(2), _ => Task.FromResult(false), "job 17 to finish");
            }).Requires(aTask);
            builder.Task("slow", slow.Body);
            builder.Task("tidy", _ => Task.CompletedTask).Requires(job);
            builder.Task("report", _ => Task.CompletedTask).After(job);
            var stop = new StopSignal();
            var reporter = new WatchedReporter(new PlainTextReporter(writer, fake));

            var run = Runner.RunAsync(
                builder.Build(),
                new RunOptions { Journal = Journal.None, TimeProvider = clock, Reporters = [reporter] },
                stop);
            await a.Started;
            await slow.Started;
            fake.Advance(TimeSpan.FromSeconds(1));
            a.Release();
            await jobStart.Started;
            fake.Advance(TimeSpan.FromSeconds(1));
            jobStart.Release();
            await reporter.Handled("waiting job: job 17 to finish");
            (await clock.NextTimerAsync()).ShouldBe(TimeSpan.FromMinutes(2));
            fake.Advance(TimeSpan.FromMinutes(1));
            fake.Advance(TimeSpan.FromSeconds(38));
            stop.Request();
            await reporter.Handled("job: running -> cancelled");
            (await clock.NextTimerAsync()).ShouldBe(TimeSpan.FromSeconds(30));
            fake.Advance(TimeSpan.FromSeconds(30));
            var result = await run;
            slow.Release();

            result.Outcome.ShouldBe(RunOutcome.Stopped);
            result.ReporterErrors.ShouldBeEmpty();
            Lines(writer).ShouldBe(
            [
                "2026-11-11T04:12:00Z run started: 5 tasks",
                "2026-11-11T04:12:00Z started a",
                "2026-11-11T04:12:00Z started slow",
                "2026-11-11T04:12:01Z succeeded a in 1.0s",
                "2026-11-11T04:12:01Z started job",
                "2026-11-11T04:12:02Z waiting job: job 17 to finish",
                "2026-11-11T04:13:02Z tasks: 2 pending, 2 running, 1 succeeded",
                "2026-11-11T04:13:40Z stop requested: waiting up to 30.0s for 2 running tasks",
                "2026-11-11T04:13:40Z waited job for 1m38s",
                "2026-11-11T04:13:40Z cancelled job in 1m39s",
                "2026-11-11T04:14:10Z stopped waiting: grace period over, 1 task still running",
                "2026-11-11T04:14:10Z cancelled slow in 2m10s: still running when the run stopped waiting",
                "2026-11-11T04:14:10Z run ended: stopped in 2m10s",
                "tasks: 1 pending, 1 ready, 1 succeeded, 2 cancelled",
                "cancelled: 2",
                "  job at 2026-11-11T04:13:40Z after 1m39s",
                "  slow at 2026-11-11T04:14:10Z after 2m10s",
                "    message: still running when the run stopped waiting",
            ]);
        }
    }

    [Test]
    public async Task A_wait_with_no_text_prints_the_path_alone()
    {
        var clock = new FakeTimeProvider(_runStart);
        var writer = new StringWriter();
        var builder = new GraphBuilder();
        builder.Task("job", context =>
        {
            using (context.BeginWaiting())
            {
                clock.Advance(TimeSpan.FromSeconds(3));
            }

            return Task.CompletedTask;
        });

        await Runner.RunAsync(builder.Build(), Options(clock, new PlainTextReporter(writer, clock)));

        Lines(writer).ShouldBe(
        [
            "2026-11-11T04:12:00Z run started: 1 task",
            "2026-11-11T04:12:00Z started job",
            "2026-11-11T04:12:00Z waiting job",
            "2026-11-11T04:12:03Z waited job for 3.0s",
            "2026-11-11T04:12:03Z succeeded job in 3.0s",
            "2026-11-11T04:12:03Z run ended: complete in 3.0s",
            "tasks: 1 succeeded",
        ]);
    }

    [Test]
    public async Task An_unlimited_grace_period_prints_the_stop_line_without_a_duration_and_a_second_stop_gives_its_own_reason()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var clock = new FakeTimeProvider(_runStart);
            var writer = new StringWriter();
            var load = new PausedBody();
            var store = new PausedBody();
            var builder = new GraphBuilder();
            builder.Task("load", load.Body);
            builder.Task("store", store.Body);
            var stop = new StopSignal();
            var reporter = new WatchedReporter(new PlainTextReporter(writer, clock));

            var run = Runner.RunAsync(
                builder.Build(),
                new RunOptions
                {
                    Journal = Journal.None,
                    TimeProvider = clock,
                    Reporters = [reporter],
                    GracePeriod = Timeout.InfiniteTimeSpan,
                },
                stop);
            await load.Started;
            await store.Started;
            clock.Advance(TimeSpan.FromSeconds(5));
            stop.Request();
            await reporter.Handled("stop requested");
            clock.Advance(TimeSpan.FromSeconds(2));
            stop.Request();
            await run;
            load.Release();
            store.Release();

            Lines(writer).ShouldBe(
            [
                "2026-11-11T04:12:00Z run started: 2 tasks",
                "2026-11-11T04:12:00Z started load",
                "2026-11-11T04:12:00Z started store",
                "2026-11-11T04:12:05Z stop requested: waiting for 2 running tasks",
                "2026-11-11T04:12:07Z stopped waiting: second stop, 2 tasks still running",
                "2026-11-11T04:12:07Z cancelled load in 7.0s: still running when the run stopped waiting",
                "2026-11-11T04:12:07Z cancelled store in 7.0s: still running when the run stopped waiting",
                "2026-11-11T04:12:07Z run ended: stopped in 7.0s",
                "tasks: 2 cancelled",
                "cancelled: 2",
                "  load at 2026-11-11T04:12:07Z after 7.0s",
                "    message: still running when the run stopped waiting",
                "  store at 2026-11-11T04:12:07Z after 7.0s",
                "    message: still running when the run stopped waiting",
            ]);
        }
    }

    [Test]
    public async Task A_count_of_one_reads_one_running_task_and_one_task_still_running()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var clock = new FakeTimeProvider(_runStart);
            var writer = new StringWriter();
            var load = new PausedBody();
            var builder = new GraphBuilder();
            builder.Task("load", load.Body);
            var stop = new StopSignal();
            var reporter = new WatchedReporter(new PlainTextReporter(writer, clock));

            var run = Runner.RunAsync(
                builder.Build(),
                new RunOptions { Journal = Journal.None, TimeProvider = clock, Reporters = [reporter], GracePeriod = TimeSpan.Zero },
                stop);
            await load.Started;
            stop.Request();
            await run;
            load.Release();

            Lines(writer).Take(5).ShouldBe(
            [
                "2026-11-11T04:12:00Z run started: 1 task",
                "2026-11-11T04:12:00Z started load",
                "2026-11-11T04:12:00Z stop requested: waiting up to 0.0s for 1 running task",
                "2026-11-11T04:12:00Z stopped waiting: grace period over, 1 task still running",
                "2026-11-11T04:12:00Z cancelled load in 0.0s: still running when the run stopped waiting",
            ]);
        }
    }

    [Test]
    public async Task A_cancelled_task_prints_its_own_message_and_the_summary_lists_it_with_its_progress_between_failures_and_warnings()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var clock = new FakeTimeProvider(_runStart);
            var writer = new StringWriter();
            var verify = new PausedBody();
            var load = new StoppedBody(context =>
            {
                context.SetMessage("loaded 17 entities");
                context.CancellationToken.ThrowIfCancellationRequested();
            });
            var builder = new GraphBuilder();
            builder.Task("verify", async context =>
            {
                await verify.Body(context);
                throw context.Fail("entity 3 has a bad date");
            });
            builder.Task("load", load.Body);
            var stop = new StopSignal();
            var reporter = new WatchedReporter(new PlainTextReporter(writer, clock));

            var run = Runner.RunAsync(builder.Build(), Options(clock, reporter), stop);
            await verify.Started;
            var loading = await load.Started;
            loading.AddWarning("entity 9 has no region");
            loading.SetStatusText("loading entities");
            loading.SetCount(17, 40);
            await reporter.Handled("warning load: entity 9 has no region");
            clock.Advance(TimeSpan.FromSeconds(2));
            verify.Release();
            await reporter.Handled("verify: running -> failed");
            clock.Advance(TimeSpan.FromSeconds(1));
            stop.Request();
            await run;

            Lines(writer).ShouldBe(
            [
                "2026-11-11T04:12:00Z run started: 2 tasks",
                "2026-11-11T04:12:00Z started verify",
                "2026-11-11T04:12:00Z started load",
                "2026-11-11T04:12:00Z warning load: entity 9 has no region",
                "2026-11-11T04:12:02Z failed verify in 2.0s: entity 3 has a bad date",
                "2026-11-11T04:12:03Z stop requested: waiting up to 30.0s for 1 running task",
                "2026-11-11T04:12:03Z cancelled load in 3.0s: loaded 17 entities",
                "2026-11-11T04:12:03Z run ended: stopped in 3.0s",
                "tasks: 1 failed, 1 cancelled",
                "failed: 1",
                "  verify (reported) at 2026-11-11T04:12:02Z after 2.0s",
                "    message: entity 3 has a bad date",
                "cancelled: 1",
                "  load at 2026-11-11T04:12:03Z after 3.0s",
                "    progress: loading entities, 17 of 40",
                "    message: loaded 17 entities",
                "warnings: 1",
                "  load",
                "    entity 9 has no region",
            ]);
        }
    }

    [Test]
    public async Task A_complete_run_with_no_warnings_prints_each_task_starting_and_settling_and_only_the_counts_as_its_summary()
    {
        var clock = new FakeTimeProvider(_runStart);
        var writer = new StringWriter();
        var builder = new GraphBuilder();
        builder.Task("prepare", _ => Task.CompletedTask);

        var result = await Runner.RunAsync(builder.Build(), Options(clock, new PlainTextReporter(writer, clock)));

        result.ReporterErrors.ShouldBeEmpty();
        Lines(writer).ShouldBe(
        [
            "2026-11-11T04:12:00Z run started: 1 task",
            "2026-11-11T04:12:00Z started prepare",
            "2026-11-11T04:12:00Z succeeded prepare in 0.0s",
            "2026-11-11T04:12:00Z run ended: complete in 0.0s",
            "tasks: 1 succeeded",
        ]);
    }

    [Test]
    [Arguments("00:00:00", "0.0s")]
    [Arguments("00:00:03.19", "3.1s")]
    [Arguments("00:00:59.99", "59.9s")]
    [Arguments("00:01:00", "1m0s")]
    [Arguments("00:01:15.9", "1m15s")]
    [Arguments("00:59:59.9", "59m59s")]
    [Arguments("01:00:00", "1h0m")]
    [Arguments("02:14:59", "2h14m")]
    [Arguments("1.02:03:00", "26h3m")]
    public async Task A_duration_is_seconds_under_a_minute_minutes_under_an_hour_and_hours_from_there_rounded_down(
        string ran,
        string printed)
    {
        var clock = new FakeTimeProvider(_runStart);
        var writer = new StringWriter();
        var prepare = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("prepare", prepare.Body);

        var run = Runner.RunAsync(builder.Build(), Options(clock, new PlainTextReporter(writer, clock)));
        await prepare.Started;
        clock.Advance(TimeSpan.Parse(ran, CultureInfo.InvariantCulture));
        prepare.Release();
        await run;

        var lines = Lines(writer).Where(line => !line.Contains(" tasks: ", StringComparison.Ordinal)).ToArray();
        lines[2].ShouldEndWith($" succeeded prepare in {printed}");
        lines[3].ShouldEndWith($" run ended: complete in {printed}");
    }

    [Test]
    public async Task A_task_that_set_a_message_has_it_after_its_duration()
    {
        var clock = new FakeTimeProvider(_runStart);
        var writer = new StringWriter();
        var builder = new GraphBuilder();
        builder.Task("prepare", context =>
        {
            context.SetMessage("checked 40 entities");
            context.AddDetail("entity 9 has no region");
            return Task.CompletedTask;
        });

        await Runner.RunAsync(builder.Build(), Options(clock, new PlainTextReporter(writer, clock)));

        Lines(writer).ShouldBe(
        [
            "2026-11-11T04:12:00Z run started: 1 task",
            "2026-11-11T04:12:00Z started prepare",
            "2026-11-11T04:12:00Z succeeded prepare in 0.0s: checked 40 entities",
            "2026-11-11T04:12:00Z run ended: complete in 0.0s",
            "tasks: 1 succeeded",
        ]);
    }

    [Test]
    public async Task An_unexpected_failure_says_so_and_prints_the_detail_lines_of_the_task_beneath_it_and_in_the_summary()
    {
        var clock = new FakeTimeProvider(_runStart);
        var writer = new StringWriter();
        var builder = new GraphBuilder();
        var load = builder.Task("load", _ => throw new InvalidOperationException("no entities to load"));

        var result = await Runner.RunAsync(builder.Build(), Options(clock, new PlainTextReporter(writer, clock)));

        var detail = result.Snapshot[load.Path].Detail;
        detail[0].ShouldBe("System.InvalidOperationException");
        detail.Count.ShouldBeGreaterThan(1);
        Lines(writer).ShouldBe(
        [
            "2026-11-11T04:12:00Z run started: 1 task",
            "2026-11-11T04:12:00Z started load",
            "2026-11-11T04:12:00Z failed load in 0.0s (unexpected): no entities to load",
            .. detail.Select(line => $"    {line}"),
            "2026-11-11T04:12:00Z run ended: incomplete in 0.0s",
            "tasks: 1 failed",
            "failed: 1",
            "  load (unexpected) at 2026-11-11T04:12:00Z after 0.0s",
            "    message: no entities to load",
            "    detail:",
            .. detail.Select(line => $"      {line}"),
        ]);
    }

    [Test]
    public async Task A_failure_with_no_message_ends_after_the_duration_and_has_no_message_in_the_summary()
    {
        var clock = new FakeTimeProvider(_runStart);
        var writer = new StringWriter();
        var builder = new GraphBuilder();
        builder.Task("load", _ => throw new InvalidOperationException(""));

        await Runner.RunAsync(builder.Build(), Options(clock, new PlainTextReporter(writer, clock)));

        var lines = Lines(writer);
        lines[2].ShouldBe("2026-11-11T04:12:00Z failed load in 0.0s (unexpected)");
        lines.ShouldContain("  load (unexpected) at 2026-11-11T04:12:00Z after 0.0s");
        lines.ShouldNotContain(line => line.StartsWith("    message:", StringComparison.Ordinal));
    }

    [Test]
    public async Task Each_failure_that_blocks_tasks_has_one_line_for_them_and_names_their_roots_in_the_summary()
    {
        var clock = new FakeTimeProvider(_runStart);
        var writer = new StringWriter();
        var builder = new GraphBuilder();
        var first = builder.Task("first", context => throw context.Fail("no entities"));
        builder.Task("one", _ => Task.CompletedTask).Requires(first);
        var second = builder.Task("second", context => throw context.Fail("no shards")).After(first);
        var store = builder.Group("store").Requires(second);
        store.Task("a", _ => Task.CompletedTask);
        store.Task("b", _ => Task.CompletedTask);

        await Runner.RunAsync(builder.Build(), Options(clock, new PlainTextReporter(writer, clock)));

        Lines(writer).ShouldBe(
        [
            "2026-11-11T04:12:00Z run started: 5 tasks",
            "2026-11-11T04:12:00Z started first",
            "2026-11-11T04:12:00Z failed first in 0.0s: no entities",
            "2026-11-11T04:12:00Z blocked 1 task by first",
            "2026-11-11T04:12:00Z started second",
            "2026-11-11T04:12:00Z failed second in 0.0s: no shards",
            "2026-11-11T04:12:00Z blocked 2 tasks by second",
            "2026-11-11T04:12:00Z group store blocked: 2 blocked",
            "2026-11-11T04:12:00Z run ended: incomplete in 0.0s",
            "tasks: 2 failed, 3 blocked",
            "failed: 2",
            "  first (reported) at 2026-11-11T04:12:00Z after 0.0s",
            "    message: no entities",
            "    blocked 1 task: one",
            "  second (reported) at 2026-11-11T04:12:00Z after 0.0s",
            "    message: no shards",
            "    blocked 2 tasks: store",
        ]);
    }

    [Test]
    public async Task Warnings_and_detail_lines_that_were_not_kept_are_counted_at_the_indent_of_the_lines_they_follow()
    {
        var clock = new FakeTimeProvider(_runStart);
        var writer = new StringWriter();
        string[] warnings = [.. Enumerable.Range(1, 103).Select(entity => $"entity {entity} has no region")];
        string[] detail = [.. Enumerable.Range(1, 103).Select(entity => $"entity {entity}: bad date")];
        var builder = new GraphBuilder();
        builder.Task("check", context =>
        {
            foreach (var warning in warnings)
            {
                context.AddWarning(warning);
            }

            throw context.Fail("103 bad entities", detail);
        });

        await Runner.RunAsync(builder.Build(), Options(clock, new PlainTextReporter(writer, clock)));

        Lines(writer).ShouldBe(
        [
            "2026-11-11T04:12:00Z run started: 1 task",
            "2026-11-11T04:12:00Z started check",
            .. warnings.Take(100).Select(warning => $"2026-11-11T04:12:00Z warning check: {warning}"),
            "2026-11-11T04:12:00Z failed check in 0.0s: 103 bad entities",
            .. detail.Take(100).Select(line => $"    {line}"),
            "    and 3 more",
            "2026-11-11T04:12:00Z run ended: incomplete in 0.0s",
            "tasks: 1 failed",
            "failed: 1",
            "  check (reported) at 2026-11-11T04:12:00Z after 0.0s",
            "    message: 103 bad entities",
            "    detail:",
            .. detail.Take(100).Select(line => $"      {line}"),
            "      and 3 more",
            "warnings: 103",
            "  check",
            .. warnings.Take(100).Select(warning => $"    {warning}"),
            "    and 3 more",
        ]);
    }

    [Test]
    public async Task The_summary_shows_the_progress_a_failed_task_had_reported_in_each_of_its_forms()
    {
        var clock = new FakeTimeProvider(_runStart);
        var writer = new StringWriter();
        var builder = new GraphBuilder();
        Node last = builder.Task("none", context => throw context.Fail("stopped"));
        last = Failing("text", context => context.SetStatusText("checking entities"));
        last = Failing("count", context => context.SetCount(17));
        last = Failing("count-of-total", context => context.SetCount(1234567, 2000000));
        last = Failing("text-and-count", context =>
        {
            context.SetStatusText("checking entities");
            context.SetCount(17);
        });
        last = Failing("text-and-count-of-total", context =>
        {
            context.SetStatusText("checking entities");
            context.SetCount(17, 40);
        });

        await Runner.RunAsync(builder.Build(), Options(clock, new PlainTextReporter(writer, clock)));

        Lines(writer).SkipWhile(line => line != "failed: 6").ShouldBe(
        [
            "failed: 6",
            "  none (reported) at 2026-11-11T04:12:00Z after 0.0s",
            "    message: stopped",
            "  text (reported) at 2026-11-11T04:12:00Z after 0.0s",
            "    progress: checking entities",
            "    message: stopped",
            "  count (reported) at 2026-11-11T04:12:00Z after 0.0s",
            "    progress: 17",
            "    message: stopped",
            "  count-of-total (reported) at 2026-11-11T04:12:00Z after 0.0s",
            "    progress: 1234567 of 2000000",
            "    message: stopped",
            "  text-and-count (reported) at 2026-11-11T04:12:00Z after 0.0s",
            "    progress: checking entities, 17",
            "    message: stopped",
            "  text-and-count-of-total (reported) at 2026-11-11T04:12:00Z after 0.0s",
            "    progress: checking entities, 17 of 40",
            "    message: stopped",
        ]);

        TaskNode Failing(string segment, Action<TaskContext> report)
            => builder.Task(segment, context =>
            {
                report(context);
                throw context.Fail("stopped");
            }).After(last);
    }

    [Test]
    public async Task Status_text_that_holds_line_breaks_is_printed_as_one_progress_line_with_a_space_for_each_break()
    {
        const string statusText = "checking entities\r\nof shard-3\nin two passes";
        var clock = new FakeTimeProvider(_runStart);
        var writer = new StringWriter();
        var builder = new GraphBuilder();
        var check = builder.Task("check", context =>
        {
            context.SetStatusText(statusText);
            context.SetCount(17, 40);
            throw context.Fail("stopped");
        });

        var result = await Runner.RunAsync(builder.Build(), Options(clock, new PlainTextReporter(writer, clock)));

        // The snapshot keeps status text as the task gave it, so it is the reporter that keeps its line one line.
        result.Snapshot[check.Path].StatusText.ShouldBe(statusText);
        Lines(writer).SkipWhile(line => line != "failed: 1").ShouldBe(
        [
            "failed: 1",
            "  check (reported) at 2026-11-11T04:12:00Z after 0.0s",
            "    progress: checking entities of shard-3 in two passes, 17 of 40",
            "    message: stopped",
        ]);
    }

    [Test]
    public async Task A_group_that_settles_has_a_line_with_its_counts_and_a_warning_has_a_line_when_it_is_added_and_is_in_the_summary()
    {
        var clock = new FakeTimeProvider(_runStart);
        var writer = new StringWriter();
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1");
        var tenant = shard.Group("tenant-a");
        var load = tenant.Task("load", context =>
        {
            context.AddWarning("entity 9 has no region");
            context.AddWarning("entity 12 has no region");
            return Task.CompletedTask;
        });
        tenant.Task("store", _ => Task.CompletedTask).Requires(load);
        builder.Task("publish", context =>
        {
            context.AddWarning("nothing new to publish");
            return Task.CompletedTask;
        }).Requires(shard);

        await Runner.RunAsync(builder.Build(), Options(clock, new PlainTextReporter(writer, clock)));

        Lines(writer).ShouldBe(
        [
            "2026-11-11T04:12:00Z run started: 3 tasks",
            "2026-11-11T04:12:00Z started shard-1/tenant-a/load",
            "2026-11-11T04:12:00Z warning shard-1/tenant-a/load: entity 9 has no region",
            "2026-11-11T04:12:00Z warning shard-1/tenant-a/load: entity 12 has no region",
            "2026-11-11T04:12:00Z succeeded shard-1/tenant-a/load in 0.0s",
            "2026-11-11T04:12:00Z started shard-1/tenant-a/store",
            "2026-11-11T04:12:00Z succeeded shard-1/tenant-a/store in 0.0s",
            "2026-11-11T04:12:00Z group shard-1/tenant-a succeeded: 2 succeeded",
            "2026-11-11T04:12:00Z group shard-1 succeeded: 2 succeeded",
            "2026-11-11T04:12:00Z started publish",
            "2026-11-11T04:12:00Z warning publish: nothing new to publish",
            "2026-11-11T04:12:00Z succeeded publish in 0.0s",
            "2026-11-11T04:12:00Z run ended: complete in 0.0s",
            "tasks: 3 succeeded",
            "warnings: 3",
            "  shard-1/tenant-a/load",
            "    entity 9 has no region",
            "    entity 12 has no region",
            "  publish",
            "    nothing new to publish",
        ]);
    }

    [Test]
    public async Task A_graph_with_no_tasks_reads_no_tasks()
    {
        var clock = new FakeTimeProvider(_runStart);
        var writer = new StringWriter();
        var builder = new GraphBuilder();
        builder.Group("shard-1");

        await Runner.RunAsync(builder.Build(), Options(clock, new PlainTextReporter(writer, clock)));

        Lines(writer).ShouldBe(
        [
            "2026-11-11T04:12:00Z run started: 0 tasks",
            "2026-11-11T04:12:00Z run ended: complete in 0.0s",
            "tasks: no tasks",
        ]);
    }

    [Test]
    public async Task Progress_and_a_title_are_never_printed()
    {
        var clock = new FakeTimeProvider(_runStart);
        var writer = new StringWriter();
        var builder = new GraphBuilder();
        builder.Task("prepare", context =>
        {
            context.SetStatusText("checking entities");
            context.SetCount(17, 40);
            return Task.CompletedTask;
        }).Title("Prepare the entities");

        await Runner.RunAsync(builder.Build(), Options(clock, new PlainTextReporter(writer, clock)));

        Lines(writer).ShouldBe(
        [
            "2026-11-11T04:12:00Z run started: 1 task",
            "2026-11-11T04:12:00Z started prepare",
            "2026-11-11T04:12:00Z succeeded prepare in 0.0s",
            "2026-11-11T04:12:00Z run ended: complete in 0.0s",
            "tasks: 1 succeeded",
        ]);
    }

    [Test]
    public async Task A_line_of_counts_is_printed_after_each_quiet_minute_and_none_once_the_run_has_ended()
    {
        var clock = new FakeTimeProvider(_runStart);
        var writer = new StringWriter();
        var prepare = new PausedBody();
        var builder = new GraphBuilder();
        var prepareTask = builder.Task("prepare", prepare.Body);
        builder.Task("publish", _ => Task.CompletedTask).Requires(prepareTask);
        var reporter = new WatchedReporter(new PlainTextReporter(writer, clock));

        var run = Runner.RunAsync(builder.Build(), Options(clock, reporter));
        await reporter.Handled("prepare: ready -> running");
        clock.Advance(TimeSpan.FromSeconds(59));
        var afterLessThanAMinute = Lines(writer);
        clock.Advance(TimeSpan.FromSeconds(1));
        var afterAMinute = Lines(writer);
        clock.Advance(TimeSpan.FromSeconds(60));
        prepare.Release();
        await run;
        clock.Advance(TimeSpan.FromMinutes(5));

        afterLessThanAMinute.ShouldBe(
        [
            "2026-11-11T04:12:00Z run started: 2 tasks",
            "2026-11-11T04:12:00Z started prepare",
        ]);
        afterAMinute.ShouldBe(
        [
            .. afterLessThanAMinute,
            "2026-11-11T04:13:00Z tasks: 1 pending, 1 running",
        ]);
        Lines(writer).ShouldBe(
        [
            .. afterAMinute,
            "2026-11-11T04:14:00Z tasks: 1 pending, 1 running",
            "2026-11-11T04:14:00Z succeeded prepare in 2m0s",
            "2026-11-11T04:14:00Z started publish",
            "2026-11-11T04:14:00Z succeeded publish in 0.0s",
            "2026-11-11T04:14:00Z run ended: complete in 2m0s",
            "tasks: 2 succeeded",
        ]);
    }

    [Test]
    public async Task The_line_of_counts_waits_a_minute_from_the_last_line_printed_whatever_that_line_was()
    {
        var clock = new FakeTimeProvider(_runStart);
        var writer = new StringWriter();
        var prepare = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("prepare", prepare.Body);
        var reporter = new WatchedReporter(new PlainTextReporter(writer, clock));

        var run = Runner.RunAsync(builder.Build(), Options(clock, reporter));
        var context = await prepare.Started;
        await reporter.Handled("prepare: ready -> running");
        clock.Advance(TimeSpan.FromSeconds(40));
        context.AddWarning("entity 9 has no region");
        await reporter.Handled("warning prepare: entity 9 has no region");
        clock.Advance(TimeSpan.FromSeconds(40));
        var aMinuteAfterTheStart = Lines(writer);
        clock.Advance(TimeSpan.FromSeconds(20));
        var aMinuteAfterTheWarning = Lines(writer);
        prepare.Release();
        await run;

        aMinuteAfterTheStart.ShouldBe(
        [
            "2026-11-11T04:12:00Z run started: 1 task",
            "2026-11-11T04:12:00Z started prepare",
            "2026-11-11T04:12:40Z warning prepare: entity 9 has no region",
        ]);
        aMinuteAfterTheWarning.ShouldBe(
        [
            .. aMinuteAfterTheStart,
            "2026-11-11T04:13:40Z tasks: 1 running",
        ]);
    }

    [Test]
    public async Task The_quiet_minute_is_measured_on_the_clock_of_the_reporter_and_not_on_that_of_the_run()
    {
        var clock = new FakeTimeProvider(_runStart);
        var reporterClock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero));
        var writer = new StringWriter();
        var prepare = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("prepare", prepare.Body);
        var reporter = new WatchedReporter(new PlainTextReporter(writer, reporterClock));

        var run = Runner.RunAsync(builder.Build(), Options(clock, reporter));
        await reporter.Handled("prepare: ready -> running");
        clock.Advance(TimeSpan.FromMinutes(5));
        var afterFiveMinutesOfTheRun = Lines(writer);
        reporterClock.Advance(TimeSpan.FromMinutes(1));
        prepare.Release();
        await run;

        afterFiveMinutesOfTheRun.ShouldBe(
        [
            "2026-11-11T04:12:00Z run started: 1 task",
            "2026-11-11T04:12:00Z started prepare",
        ]);
        Lines(writer).ShouldBe(
        [
            .. afterFiveMinutesOfTheRun,
            "2026-11-11T09:01:00Z tasks: 1 running",
            "2026-11-11T04:17:00Z succeeded prepare in 5m0s",
            "2026-11-11T04:17:00Z run ended: complete in 5m0s",
            "tasks: 1 succeeded",
        ]);
    }

    [Test]
    public async Task A_reporter_given_to_a_second_run_once_the_first_has_ended_prints_that_run_as_well()
    {
        var clock = new FakeTimeProvider(_runStart);
        var writer = new StringWriter();
        var builder = new GraphBuilder();
        builder.Task("prepare", _ => Task.CompletedTask);
        var graph = builder.Build();
        var options = Options(clock, new PlainTextReporter(writer, clock));

        await Runner.RunAsync(graph, options);
        clock.Advance(TimeSpan.FromMinutes(3));
        await Runner.RunAsync(graph, options);
        clock.Advance(TimeSpan.FromMinutes(3));

        Lines(writer).ShouldBe(
        [
            "2026-11-11T04:12:00Z run started: 1 task",
            "2026-11-11T04:12:00Z started prepare",
            "2026-11-11T04:12:00Z succeeded prepare in 0.0s",
            "2026-11-11T04:12:00Z run ended: complete in 0.0s",
            "tasks: 1 succeeded",
            "2026-11-11T04:15:00Z run started: 1 task",
            "2026-11-11T04:15:00Z started prepare",
            "2026-11-11T04:15:00Z succeeded prepare in 0.0s",
            "2026-11-11T04:15:00Z run ended: complete in 0.0s",
            "tasks: 1 succeeded",
        ]);
    }

    [Test]
    public async Task The_reporter_rejects_a_null_writer_a_null_event_and_a_null_snapshot()
    {
        var recording = new RecordingReporter();
        await Runner.RunAsync(new GraphBuilder().Build(), new RunOptions { Journal = Journal.None, Reporters = [recording] });
        var ended = recording.Events[^1];
        var reporter = new PlainTextReporter(new StringWriter());

        Should.Throw<ArgumentNullException>(() => new PlainTextReporter(null!)).ParamName.ShouldBe("writer");
        var noEvent = await Should.ThrowAsync<ArgumentNullException>(
            async () => await reporter.OnEventAsync(null!, null!, CancellationToken.None));
        noEvent.ParamName.ShouldBe("runEvent");
        var noSnapshot = await Should.ThrowAsync<ArgumentNullException>(
            async () => await reporter.OnEventAsync(ended, null!, CancellationToken.None));
        noSnapshot.ParamName.ShouldBe("snapshot");
    }

    [Test]
    public async Task The_line_for_blocked_tasks_is_printed_before_the_line_of_counts_when_no_event_follows_it_within_the_minute()
    {
        var clock = new FakeTimeProvider(_runStart);
        var writer = new StringWriter();
        var load = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("load", load.Body);
        var verify = builder.Task("verify", context => throw context.Fail("entity 3 has a bad date"));
        builder.Task("publish", _ => Task.CompletedTask).Requires(verify);
        var reporter = new WatchedReporter(new PlainTextReporter(writer, clock));

        var run = Runner.RunAsync(builder.Build(), Options(clock, reporter));
        await reporter.Handled("publish: pending -> blocked");
        clock.Advance(TimeSpan.FromMinutes(1));
        var afterAMinute = Lines(writer);
        load.Release();
        await run;

        afterAMinute.ShouldBe(
        [
            "2026-11-11T04:12:00Z run started: 3 tasks",
            "2026-11-11T04:12:00Z started load",
            "2026-11-11T04:12:00Z started verify",
            "2026-11-11T04:12:00Z failed verify in 0.0s: entity 3 has a bad date",
            "2026-11-11T04:12:00Z blocked 1 task by verify",
            "2026-11-11T04:13:00Z tasks: 1 running, 1 failed, 1 blocked",
        ]);
    }

    [Test]
    public async Task A_reporter_whose_writer_fails_on_an_event_is_dropped_and_prints_no_line_of_counts_afterwards()
    {
        var clock = new FakeTimeProvider(_runStart);
        var writer = new BreakingWriter(text => text.Contains("started load", StringComparison.Ordinal));
        var load = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("load", load.Body);
        var reporter = new WatchedReporter(new PlainTextReporter(writer, clock));

        var run = Runner.RunAsync(builder.Build(), Options(clock, reporter));
        await reporter.Handled("load: ready -> running");
        clock.Advance(TimeSpan.FromMinutes(5));
        load.Release();
        var result = await run;

        writer.Attempts.ShouldBe(
        [
            $"2026-11-11T04:12:00Z run started: 1 task{writer.NewLine}",
            $"2026-11-11T04:12:00Z started load{writer.NewLine}",
        ]);
        var error = result.ReporterErrors.ShouldHaveSingleItem();
        error.Reporter.ShouldBeSameAs(reporter);
        error.Exception.ShouldBeSameAs(writer.Failure);
        result.Outcome.ShouldBe(RunOutcome.Complete);
    }

    [Test]
    public async Task A_reporter_whose_writer_fails_on_the_line_of_counts_prints_no_other_and_is_dropped_at_the_next_event()
    {
        var clock = new FakeTimeProvider(_runStart);
        var writer = new BreakingWriter(text => text.Contains(" tasks: ", StringComparison.Ordinal));
        var load = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("load", load.Body);
        var reporter = new WatchedReporter(new PlainTextReporter(writer, clock));

        var run = Runner.RunAsync(builder.Build(), Options(clock, reporter));
        await reporter.Handled("load: ready -> running");
        clock.Advance(TimeSpan.FromMinutes(1));
        clock.Advance(TimeSpan.FromMinutes(5));
        load.Release();
        var result = await run;

        writer.Attempts.ShouldBe(
        [
            $"2026-11-11T04:12:00Z run started: 1 task{writer.NewLine}",
            $"2026-11-11T04:12:00Z started load{writer.NewLine}",
            $"2026-11-11T04:13:00Z tasks: 1 running{writer.NewLine}",
        ]);
        var error = result.ReporterErrors.ShouldHaveSingleItem();
        error.Reporter.ShouldBeSameAs(reporter);
        error.Event.ShouldBeOfType<TaskStatusChanged>().To.ShouldBe(NodeStatus.Succeeded);
        error.Exception.ShouldBeSameAs(writer.Failure);
        result.Outcome.ShouldBe(RunOutcome.Complete);
    }

    [Test]
    public async Task All_the_lines_about_one_event_are_written_with_one_call_and_no_two_writes_are_ever_in_progress_at_once()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var clock = new FakeTimeProvider(_runStart);
            var writer = new OverlapCountingWriter();
            var builder = new GraphBuilder();
            Node last = builder.Task("shard-0", context => throw context.Fail("no entities", "entity 3: missing owner"));
            for (var shard = 1; shard <= 20; shard++)
            {
                last = builder.Task($"shard-{shard}", WaitAMinute).After(last);
            }

            // Every step of the clock ends a task and, a minute after the last line, is also due a line of counts:
            // the reporter is called for the events on one thread while its own timer fires on this one.
            var run = Runner.RunAsync(builder.Build(), Options(clock, new PlainTextReporter(writer, clock)));
            await clock.AdvanceUntilAsync(run, TimeSpan.FromMinutes(1));
            await run;

            writer.MostWritesAtOnce.ShouldBe(1);
            writer.Writes
                .Where(text => text.Contains(" failed shard-0 in ", StringComparison.Ordinal))
                .ShouldHaveSingleItem()
                .ShouldEndWith($": no entities{writer.NewLine}    entity 3: missing owner{writer.NewLine}");
            writer.Writes.Count(text => text.Contains(" started shard-", StringComparison.Ordinal)).ShouldBe(21);
            writer.Writes[^1].ShouldContain($"{writer.NewLine}tasks: 20 succeeded, 1 failed{writer.NewLine}failed: 1{writer.NewLine}");
        }

        static Task WaitAMinute(TaskContext context) => Task.Delay(TimeSpan.FromMinutes(1), context.TimeProvider);
    }

    private static RunOptions Options(FakeTimeProvider clock, params IReporter[] reporters)
        => new() { Journal = Journal.None, TimeProvider = clock, Reporters = reporters };

    /// <summary>What was written, one entry for each line, without the line break that ends the last one.</summary>
    private static string[] Lines(StringWriter writer)
    {
        var written = writer.ToString();
        written.ShouldEndWith(writer.NewLine);
        return written[..^writer.NewLine.Length].Split(writer.NewLine);
    }

    /// <summary>
    /// Keeps every text it is asked to write, and fails on each one that <paramref name="failsOn"/> picks, with <see cref="Failure"/>.
    /// </summary>
    private sealed class BreakingWriter(Func<string, bool> failsOn) : TextWriter
    {
        private readonly Lock _gate = new();
        private readonly List<string> _attempts = [];

        public IOException Failure { get; } = new("the disk is full");

        public IReadOnlyList<string> Attempts
        {
            get
            {
                lock (_gate)
                {
                    return [.. _attempts];
                }
            }
        }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => Write(value.ToString());

        public override void Write(string? value)
        {
            var text = value ?? "";
            lock (_gate)
            {
                _attempts.Add(text);
            }

            if (failsOn(text))
            {
                throw Failure;
            }
        }
    }

    /// <summary>
    /// Keeps every text it is asked to write, and counts the most calls to write that were ever in progress at once.
    /// Each call stays open across a yield to other threads, so a call made before the last one returned would overlap it.
    /// </summary>
    private sealed class OverlapCountingWriter : TextWriter
    {
        private readonly Lock _gate = new();
        private readonly List<string> _writes = [];
        private int _writing;
        private int _mostWritesAtOnce;

        public int MostWritesAtOnce => Volatile.Read(ref _mostWritesAtOnce);

        public IReadOnlyList<string> Writes
        {
            get
            {
                lock (_gate)
                {
                    return [.. _writes];
                }
            }
        }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => Write(value.ToString());

        public override void Write(string? value)
        {
            var writing = Interlocked.Increment(ref _writing);
            int most;
            while (writing > (most = Volatile.Read(ref _mostWritesAtOnce)))
            {
                Interlocked.CompareExchange(ref _mostWritesAtOnce, writing, most);
            }

            Thread.Yield();
            lock (_gate)
            {
                _writes.Add(value ?? "");
            }

            Thread.Yield();
            Interlocked.Decrement(ref _writing);
        }
    }
}
