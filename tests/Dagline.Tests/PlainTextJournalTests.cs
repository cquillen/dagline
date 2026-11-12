using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace Dagline.Tests;

public class PlainTextJournalTests
{
    private static readonly DateTimeOffset _firstRunStart = new(2026, 11, 11, 4, 12, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _secondRunStart = new(2026, 11, 11, 5, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task The_second_run_of_the_reference_journal_prints_the_journal_reference_output_line_for_line()
    {
        using var temporary = new TemporaryDirectory();
        var firstBuilder = new GraphBuilder();
        firstBuilder.Task("a", context =>
        {
            context.Warn("entity 9 has no region");
            return Task.CompletedTask;
        });
        var firstB = firstBuilder.Task("b", context => throw context.Fail("2 bad entities"));
        firstBuilder.Task("report", _ => Task.CompletedTask).After(firstB);
        firstBuilder.Task("old", context => throw context.Fail("no entities"));
        firstBuilder.Task("gone", _ => Task.CompletedTask);
        var first = await Runner.RunAsync(
            firstBuilder.Build(),
            new RunOptions { Journal = Journal.New(temporary.Path, "all shards"), TimeProvider = new FakeTimeProvider(_firstRunStart) });
        var id = first.Snapshot.Journal!.Id;

        var clock = new FakeTimeProvider(_secondRunStart);
        var writer = new StringWriter();
        var b = new PausedBody();
        var report = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("a", _ => Task.CompletedTask);
        var secondB = builder.Task("b", b.Body);
        builder.Task("report", report.Body).After(secondB);
        var run = Runner.RunAsync(
            builder.Build(),
            new RunOptions { Journal = Journal.Continue(temporary.Path, id), TimeProvider = clock, Reporters = [new PlainTextReporter(writer, clock)] });
        await b.Started;
        clock.Advance(TimeSpan.FromSeconds(4));
        b.Release();
        var reportContext = await report.Started;
        clock.Advance(TimeSpan.FromSeconds(1));
        reportContext.Message("2 of 2 checks passed");
        report.Release();
        await run;

        Lines(writer).ShouldBe(
        [
            "2026-11-11T05:00:00Z run started: 3 tasks, 1 earlier success, 1 set aside, 2 journal paths not in graph (1 not succeeded)",
            $"2026-11-11T05:00:00Z journal {id} run 2: all shards",
            "2026-11-11T05:00:00Z started b",
            "2026-11-11T05:00:04Z succeeded b in 4.0s",
            "2026-11-11T05:00:04Z started report",
            "2026-11-11T05:00:05Z succeeded report in 1.0s: 2 of 2 checks passed",
            "2026-11-11T05:00:05Z run ended: complete in 5.0s",
            $"journal: {id} run 2: all shards",
            "tasks: 3 succeeded (1 earlier)",
            "warnings: 1",
            "  from earlier runs, not listed again: 1 on 1 task",
            "set aside: 1",
            "  report",
            "journal paths not in graph: 2 (1 not succeeded)",
        ]);
    }

    [Test]
    public async Task A_new_journal_with_no_description_is_named_by_its_id_and_run_and_its_unfinished_tasks_are_left_for_a_later_run()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new FakeTimeProvider(_firstRunStart);
        var writer = new StringWriter();
        var builder = new GraphBuilder();
        var check = builder.Task("check", context => throw context.Fail("bad data"));
        builder.Task("publish", _ => Task.CompletedTask).Requires(check);
        builder.Task("load", _ => Task.CompletedTask).After(check);

        var result = await Runner.RunAsync(
            builder.Build(),
            new RunOptions { Journal = Journal.New(temporary.Path), TimeProvider = clock, Reporters = [new PlainTextReporter(writer, clock)] });

        var id = result.Snapshot.Journal!.Id;
        id.ShouldStartWith("20261111T041200Z-");
        Lines(writer).ShouldBe(
        [
            "2026-11-11T04:12:00Z run started: 3 tasks",
            $"2026-11-11T04:12:00Z journal {id} run 1",
            "2026-11-11T04:12:00Z started check",
            "2026-11-11T04:12:00Z failed check in 0.0s: bad data",
            "2026-11-11T04:12:00Z blocked 1 task by check",
            "2026-11-11T04:12:00Z started load",
            "2026-11-11T04:12:00Z succeeded load in 0.0s",
            "2026-11-11T04:12:00Z run ended: incomplete in 0.0s",
            $"journal: {id} run 1",
            "tasks: 1 succeeded, 1 failed, 1 blocked",
            "failed: 1",
            "  check (reported) at 2026-11-11T04:12:00Z after 0.0s",
            "    message: bad data",
            "    blocked 1 task: publish",
            "left for a later run: 2 tasks",
        ]);
    }

    [Test]
    public async Task A_complete_run_on_a_journal_leaves_nothing_for_a_later_run()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new FakeTimeProvider(_firstRunStart);
        var writer = new StringWriter();
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask);

        var result = await Runner.RunAsync(
            builder.Build(),
            new RunOptions
            {
                Journal = Journal.New(temporary.Path, "first shard"),
                TimeProvider = clock,
                Reporters = [new PlainTextReporter(writer, clock)],
            });

        var id = result.Snapshot.Journal!.Id;
        Lines(writer).ShouldBe(
        [
            "2026-11-11T04:12:00Z run started: 1 task",
            $"2026-11-11T04:12:00Z journal {id} run 1: first shard",
            "2026-11-11T04:12:00Z started load",
            "2026-11-11T04:12:00Z succeeded load in 0.0s",
            "2026-11-11T04:12:00Z run ended: complete in 0.0s",
            $"journal: {id} run 1: first shard",
            "tasks: 1 succeeded",
        ]);
    }

    [Test]
    public async Task The_line_of_counts_and_a_group_line_show_earlier_successes_in_brackets_after_succeeded()
    {
        using var temporary = new TemporaryDirectory();
        var fail = true;
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1");
        shard.Task("load", _ => Task.CompletedTask);
        shard.Task("store", context => fail ? throw context.Fail("disk full") : Task.CompletedTask);
        var first = await Runner.RunAsync(
            builder.Build(),
            new RunOptions { Journal = Journal.New(temporary.Path), TimeProvider = new FakeTimeProvider(_firstRunStart) });
        fail = false;

        var clock = new FakeTimeProvider(_secondRunStart);
        var writer = new StringWriter();
        var store = new PausedBody();
        var again = new GraphBuilder();
        var againShard = again.Group("shard-1");
        againShard.Task("load", _ => Task.CompletedTask);
        againShard.Task("store", store.Body);
        var reporter = new WatchedReporter(new PlainTextReporter(writer, clock));
        var run = Runner.RunAsync(
            again.Build(),
            new RunOptions { Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id), TimeProvider = clock, Reporters = [reporter] });
        await reporter.Handled("shard-1/store: ready -> running");
        clock.Advance(TimeSpan.FromMinutes(1));
        store.Release();
        await run;

        var id = first.Snapshot.Journal!.Id;
        Lines(writer).ShouldBe(
        [
            "2026-11-11T05:00:00Z run started: 2 tasks, 1 earlier success",
            $"2026-11-11T05:00:00Z journal {id} run 2",
            "2026-11-11T05:00:00Z started shard-1/store",
            "2026-11-11T05:01:00Z tasks: 1 running, 1 succeeded (1 earlier)",
            "2026-11-11T05:01:00Z succeeded shard-1/store in 1m0s",
            "2026-11-11T05:01:00Z group shard-1 succeeded: 2 succeeded (1 earlier)",
            "2026-11-11T05:01:00Z run ended: complete in 1m0s",
            $"journal: {id} run 2",
            "tasks: 2 succeeded (1 earlier)",
        ]);
    }

    [Test]
    public async Task A_warning_added_in_a_continuing_run_is_listed_above_the_line_for_the_warnings_of_earlier_successes()
    {
        using var temporary = new TemporaryDirectory();
        var fail = true;
        var builder = new GraphBuilder();
        builder.Task("load", context =>
        {
            context.Warn("entity 9 has no region");
            context.Warn("entity 12 has no region");
            return Task.CompletedTask;
        });
        builder.Task("store", context =>
        {
            context.Warn("entity 3 has a bad date");
            return Task.CompletedTask;
        });
        builder.Task("publish", context =>
        {
            if (fail)
            {
                throw context.Fail("no connection");
            }

            context.Warn("nothing new to publish");
            return Task.CompletedTask;
        });
        var graph = builder.Build();
        var first = await Runner.RunAsync(
            graph,
            new RunOptions { Journal = Journal.New(temporary.Path), TimeProvider = new FakeTimeProvider(_firstRunStart) });
        fail = false;

        var clock = new FakeTimeProvider(_secondRunStart);
        var writer = new StringWriter();
        await Runner.RunAsync(
            graph,
            new RunOptions
            {
                Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id),
                TimeProvider = clock,
                Reporters = [new PlainTextReporter(writer, clock)],
            });

        Lines(writer)[^4..].ShouldBe(
        [
            "warnings: 4",
            "  publish",
            "    nothing new to publish",
            "  from earlier runs, not listed again: 3 on 2 tasks",
        ]);
    }

    [Test]
    public async Task Journal_paths_not_in_graph_show_both_counts_when_every_one_of_them_succeeded()
    {
        using var temporary = new TemporaryDirectory();
        var larger = new GraphBuilder();
        larger.Task("load", _ => Task.CompletedTask);
        larger.Task("gone", _ => Task.CompletedTask);
        var first = await Runner.RunAsync(
            larger.Build(),
            new RunOptions { Journal = Journal.New(temporary.Path), TimeProvider = new FakeTimeProvider(_firstRunStart) });

        var clock = new FakeTimeProvider(_secondRunStart);
        var writer = new StringWriter();
        var smaller = new GraphBuilder();
        smaller.Task("load", _ => Task.CompletedTask);
        await Runner.RunAsync(
            smaller.Build(),
            new RunOptions
            {
                Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id),
                TimeProvider = clock,
                Reporters = [new PlainTextReporter(writer, clock)],
            });

        var id = first.Snapshot.Journal!.Id;
        Lines(writer).ShouldBe(
        [
            "2026-11-11T05:00:00Z run started: 1 task, 1 earlier success, 1 journal path not in graph (0 not succeeded)",
            $"2026-11-11T05:00:00Z journal {id} run 2",
            "2026-11-11T05:00:00Z run ended: complete in 0.0s",
            $"journal: {id} run 2",
            "tasks: 1 succeeded (1 earlier)",
            "journal paths not in graph: 1 (0 not succeeded)",
        ]);
    }

    [Test]
    public async Task A_stop_Dagline_asked_for_and_a_run_that_ended_with_an_error_each_read_as_a_failed_journal_write()
    {
        // No real file can be made to fail, so the two events a failed write gives are handed to the reporter here,
        // each with a real snapshot of a run: one taken while a task was running, and the final one.
        var clock = new FakeTimeProvider(_firstRunStart);
        var load = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("load", load.Body);
        var recording = new RecordingReporter();
        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, TimeProvider = clock, Reporters = [recording] });
        await load.Started;
        clock.Advance(TimeSpan.FromSeconds(5));
        var running = (await recording.Source).Current;
        load.Release();
        var final = (await run).Snapshot;
        var writer = new StringWriter();
        var reporter = new PlainTextReporter(writer, clock);

        await reporter.OnEventAsync(
            new StopRequested(_firstRunStart.AddSeconds(5), StopRequester.Dagline, TimeSpan.FromSeconds(30)), running, CancellationToken.None);
        await reporter.OnEventAsync(
            new StopRequested(_firstRunStart.AddSeconds(5), StopRequester.Dagline, Timeout.InfiniteTimeSpan), running, CancellationToken.None);
        await reporter.OnEventAsync(
            new RunEnded(
                _firstRunStart.AddSeconds(5),
                null,
                new InvalidOperationException("The journal can no longer be written.", new IOException("disk full"))),
            final,
            CancellationToken.None);

        Lines(writer).ShouldBe(
        [
            "2026-11-11T04:12:05Z stop requested by Dagline, journal write failed: waiting up to 30.0s for 1 running task",
            "2026-11-11T04:12:05Z stop requested by Dagline, journal write failed: waiting for 1 running task",
            "2026-11-11T04:12:05Z run ended: journal write failed after 5.0s: disk full",
            "tasks: 1 succeeded",
        ]);
    }

    /// <summary>What was written, one entry for each line, without the line break that ends the last one.</summary>
    private static string[] Lines(StringWriter writer)
    {
        var written = writer.ToString();
        written.ShouldEndWith(writer.NewLine);
        return written[..^writer.NewLine.Length].Split(writer.NewLine);
    }
}
