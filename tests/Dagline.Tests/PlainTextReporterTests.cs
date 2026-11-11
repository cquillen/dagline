using System.Globalization;
using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace Dagline.Tests;

public class PlainTextReporterTests
{
    private static readonly DateTimeOffset _runStart = new(2026, 11, 11, 4, 12, 0, TimeSpan.Zero);

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
            context.Message("checked 40 entities");
            context.Detail("entity 9 has no region");
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
                context.Warn(warning);
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
        last = Failing("text", context => context.Status("checking entities"));
        last = Failing("count", context => context.Count(17));
        last = Failing("count-of-total", context => context.Count(1234567, 2000000));
        last = Failing("text-and-count", context =>
        {
            context.Status("checking entities");
            context.Count(17);
        });
        last = Failing("text-and-count-of-total", context =>
        {
            context.Status("checking entities");
            context.Count(17, 40);
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
    public async Task A_group_that_settles_has_a_line_with_its_counts_and_a_warning_has_a_line_when_it_is_added_and_is_in_the_summary()
    {
        var clock = new FakeTimeProvider(_runStart);
        var writer = new StringWriter();
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1");
        var tenant = shard.Group("tenant-a");
        var load = tenant.Task("load", context =>
        {
            context.Warn("entity 9 has no region");
            context.Warn("entity 12 has no region");
            return Task.CompletedTask;
        });
        tenant.Task("store", _ => Task.CompletedTask).Requires(load);
        builder.Task("publish", context =>
        {
            context.Warn("nothing new to publish");
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
            context.Status("checking entities");
            context.Count(17, 40);
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

    private static RunOptions Options(FakeTimeProvider clock, params IReporter[] reporters)
        => new() { Journal = Journal.None, TimeProvider = clock, Reporters = reporters };

    /// <summary>What was written, one entry for each line, without the line break that ends the last one.</summary>
    private static string[] Lines(StringWriter writer)
    {
        var written = writer.ToString();
        written.ShouldEndWith(writer.NewLine);
        return written[..^writer.NewLine.Length].Split(writer.NewLine);
    }
}
