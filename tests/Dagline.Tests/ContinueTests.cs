using System.Collections.Concurrent;
using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace Dagline.Tests;

public class ContinueTests
{
    private static readonly DateTimeOffset _created = new(2026, 11, 11, 4, 12, 0, TimeSpan.Zero);

    [Test]
    public async Task A_run_that_continues_a_journal_carries_run_2_and_the_same_id_and_description()
    {
        using var temporary = new TemporaryDirectory();
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask);
        var graph = builder.Build();

        var first = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path, "all shards") });
        var id = first.Snapshot.Journal!.Id;
        var second = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.Continue(temporary.Path, id) });

        second.Snapshot.Journal.ShouldBe(new JournalInfo(id, "all shards", 2));
    }

    [Test]
    public async Task A_new_journal_with_an_empty_description_has_none()
    {
        using var temporary = new TemporaryDirectory();
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask);
        var graph = builder.Build();

        var first = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path, "") });
        var second = await Runner.RunAsync(
            graph,
            new RunOptions { Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id) });

        first.Snapshot.Journal!.Description.ShouldBeNull();
        second.Snapshot.Journal!.Description.ShouldBeNull();
    }

    [Test]
    public async Task A_task_that_succeeded_is_an_earlier_success_with_its_whole_outcome_and_does_not_run()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new FakeTimeProvider(_created);
        var runs = 0;
        var builder = new GraphBuilder();
        var load = builder.Task("load", context =>
        {
            runs++;
            context.SetMessage("loaded 3 files");
            context.AddDetail("file-1", "file-2\nfile-3");
            context.AddWarning("file-2 was empty");
            return Task.CompletedTask;
        });
        var graph = builder.Build();

        var first = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path), TimeProvider = clock });
        clock.Advance(TimeSpan.FromHours(1));
        var reporter = new RecordingReporter();
        var second = await Runner.RunAsync(
            graph,
            new RunOptions
            {
                Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id),
                TimeProvider = clock,
                Reporters = [reporter],
            });

        runs.ShouldBe(1);
        var kept = second.Snapshot[load.Path];
        kept.Status.ShouldBe(NodeStatus.Succeeded);
        kept.IsEarlierSuccess.ShouldBeTrue();
        kept.Counts.EarlierSuccesses.ShouldBe(1);
        kept.Message.ShouldBe("loaded 3 files");
        kept.Detail.ShouldBe(["file-1", "file-2", "file-3"]);
        kept.Warnings.ShouldBe(["file-2 was empty"]);
        kept.Settled.ShouldBe(_created);
        kept.Started.ShouldBeNull();
        reporter.Snapshots[0][load.Path].ShouldBe(kept);
    }

    [Test]
    public async Task Tasks_that_failed_were_blocked_were_cancelled_or_were_never_reached_start_pending_and_run()
    {
        using var temporary = new TemporaryDirectory();
        var stopNow = false;
        var stop = new StopSignal();
        var ran = new ConcurrentQueue<string>();
        var builder = new GraphBuilder();
        var check = builder.Task("check", context =>
        {
            ran.Enqueue("check");
            return stopNow ? Task.CompletedTask : throw context.Fail("bad data");
        });
        builder.Task("load", _ =>
        {
            ran.Enqueue("load");
            return Task.CompletedTask;
        }).Requires(check);
        var wait = builder.Task("wait", async context =>
        {
            ran.Enqueue("wait");
            if (!stopNow)
            {
                stop.Request();
                await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            }
        });
        builder.Task("later", _ =>
        {
            ran.Enqueue("later");
            return Task.CompletedTask;
        }).After(wait).After(check);
        var graph = builder.Build();

        var first = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path) }, stop);
        first.Snapshot.Nodes.Select(node => node.Status)
            .ShouldBe([NodeStatus.Failed, NodeStatus.Blocked, NodeStatus.Cancelled, NodeStatus.Ready]);
        stopNow = true;
        ran.Clear();
        var reporter = new RecordingReporter();
        var second = await Runner.RunAsync(
            graph,
            new RunOptions { Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id), Reporters = [reporter] });

        reporter.Snapshots[0].Nodes.Select(node => node.Status).ShouldAllBe(status => status == NodeStatus.Pending);
        second.Outcome.ShouldBe(RunOutcome.Complete);
        ran.Order().ShouldBe(["check", "later", "load", "wait"]);
        second.Snapshot.Counts.EarlierSuccesses.ShouldBe(0);
    }

    [Test]
    public async Task Earlier_successes_get_no_event_and_a_dependency_of_either_kind_on_one_is_met_from_the_start()
    {
        using var temporary = new TemporaryDirectory();
        var fail = true;
        var builder = new GraphBuilder();
        var load = builder.Task("load", _ => Task.CompletedTask);
        var check = builder.Task("check", context => fail ? throw context.Fail("bad data") : Task.CompletedTask);
        builder.Task("publish", _ => Task.CompletedTask).Requires(load).Requires(check);
        builder.Task("report", _ => Task.CompletedTask).After(load).Requires(check);
        var graph = builder.Build();

        var first = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path) });
        fail = false;
        var reporter = new RecordingReporter();
        var second = await Runner.RunAsync(
            graph,
            new RunOptions { Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id), Reporters = [reporter] });

        second.Outcome.ShouldBe(RunOutcome.Complete);
        reporter.Lines.Take(8).ShouldBe(
        [
            "run started",
            "check: pending -> ready",
            "check: ready -> running",
            "check: running -> succeeded",
            "publish: pending -> ready",
            "report: pending -> ready",
            "publish: ready -> running",
            "report: ready -> running",
        ]);
        reporter.Lines.Skip(8).Order().ShouldBe(["publish: running -> succeeded", "report: running -> succeeded", "run ended: complete"]);
        reporter.Snapshots[0][load.Path].IsEarlierSuccess.ShouldBeTrue();
        reporter.Snapshots[0].Counts.EarlierSuccesses.ShouldBe(1);
        reporter.Snapshots[0].Counts[NodeStatus.Succeeded].ShouldBe(1);
        reporter.Snapshots[0].Counts[NodeStatus.Pending].ShouldBe(3);
    }

    [Test]
    public async Task A_group_of_earlier_successes_gets_no_event_and_a_mixed_group_settles_once_with_them_counted_apart()
    {
        using var temporary = new TemporaryDirectory();
        var fail = true;
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1");
        var done = shard.Group("tenant-a");
        done.Task("load", _ => Task.CompletedTask);
        done.Task("verify", _ => Task.CompletedTask);
        var mixed = shard.Group("tenant-b");
        mixed.Task("load", _ => Task.CompletedTask);
        mixed.Task("verify", context => fail ? throw context.Fail("bad data") : Task.CompletedTask);
        var graph = builder.Build();

        var first = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path) });
        fail = false;
        var reporter = new RecordingReporter();
        var second = await Runner.RunAsync(
            graph,
            new RunOptions { Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id), Reporters = [reporter] });

        var settled = reporter.Events.OfType<GroupSettled>().ToList();
        settled.Select(group => (group.Path, group.Status, group.Counts.EarlierSuccesses)).ShouldBe(
        [
            (mixed.Path, NodeStatus.Succeeded, 1),
            (shard.Path, NodeStatus.Succeeded, 3),
        ]);
        settled[0].Counts[NodeStatus.Succeeded].ShouldBe(2);
        var start = reporter.Snapshots[0];
        start[done.Path].Status.ShouldBe(NodeStatus.Succeeded);
        start[done.Path].IsEarlierSuccess.ShouldBeTrue();
        start[done.Path].Counts.EarlierSuccesses.ShouldBe(2);
        start[mixed.Path].IsEarlierSuccess.ShouldBeFalse();
        start[mixed.Path].Counts.EarlierSuccesses.ShouldBe(1);
        start[shard.Path].IsEarlierSuccess.ShouldBeFalse();
        start[shard.Path].Counts.EarlierSuccesses.ShouldBe(3);
        second.Snapshot.Counts.EarlierSuccesses.ShouldBe(3);
        second.Snapshot[shard.Path].Counts.EarlierSuccesses.ShouldBe(3);
    }

    [Test]
    public async Task A_continuing_run_with_nothing_left_to_do_publishes_two_events_and_is_complete()
    {
        using var temporary = new TemporaryDirectory();
        var builder = new GraphBuilder();
        builder.Group("shard-1").Task("load", _ => Task.CompletedTask);
        var graph = builder.Build();

        var first = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path) });
        var reporter = new RecordingReporter();
        var second = await Runner.RunAsync(
            graph,
            new RunOptions { Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id), Reporters = [reporter] });

        second.Outcome.ShouldBe(RunOutcome.Complete);
        reporter.Lines.ShouldBe(["run started", "run ended: complete"]);
        second.Snapshot.Counts[NodeStatus.Succeeded].ShouldBe(1);
    }

    [Test]
    public async Task Continuing_a_finished_journal_with_a_graph_that_gained_a_path_runs_only_that_task()
    {
        using var temporary = new TemporaryDirectory();
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask);
        var graph = builder.Build();
        var first = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path) });
        var id = first.Snapshot.Journal!.Id;
        var second = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.Continue(temporary.Path, id) });

        var grown = new GraphBuilder();
        grown.Task("load", _ => Task.CompletedTask);
        grown.Task("publish", _ => Task.CompletedTask);
        var reporter = new RecordingReporter();
        var third = await Runner.RunAsync(
            grown.Build(),
            new RunOptions { Journal = Journal.Continue(temporary.Path, id), Reporters = [reporter] });

        second.Outcome.ShouldBe(RunOutcome.Complete);
        third.Outcome.ShouldBe(RunOutcome.Complete);
        third.Snapshot.Journal!.Run.ShouldBe(3);
        reporter.Lines.ShouldBe(
        [
            "run started",
            "publish: pending -> ready",
            "publish: ready -> running",
            "publish: running -> succeeded",
            "run ended: complete",
        ]);
    }

    [Test]
    public async Task A_run_that_continues_with_different_max_running_tasks_and_limit_sizes_works()
    {
        using var temporary = new TemporaryDirectory();
        var fail = true;
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask).Limit("database");
        builder.Task("verify", context => fail ? throw context.Fail("bad data") : Task.CompletedTask).Limit("database");
        var graph = builder.Build();

        var first = await Runner.RunAsync(
            graph,
            new RunOptions { Journal = Journal.New(temporary.Path), MaxRunningTasks = 4, Limits = new Dictionary<string, int> { ["database"] = 2 } });
        fail = false;
        var second = await Runner.RunAsync(
            graph,
            new RunOptions
            {
                Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id),
                MaxRunningTasks = 1,
                Limits = new Dictionary<string, int> { ["database"] = 1 },
            });

        second.Outcome.ShouldBe(RunOutcome.Complete);
        second.Snapshot.RunningTasks.Size.ShouldBe(1);
        second.Snapshot.Counts.EarlierSuccesses.ShouldBe(1);
    }

    [Test]
    public async Task An_earlier_success_that_requires_a_task_that_settles_in_this_run_is_never_made_ready_and_never_blocked()
    {
        using var temporary = new TemporaryDirectory();
        var alone = new GraphBuilder();
        alone.Task("publish", _ => Task.CompletedTask);
        alone.Task("report", _ => Task.CompletedTask);
        var first = await Runner.RunAsync(alone.Build(), new RunOptions { Journal = Journal.New(temporary.Path) });

        var builder = new GraphBuilder();
        var good = builder.Task("good", _ => Task.CompletedTask);
        var bad = builder.Task("bad", context => throw context.Fail("bad data"));
        var publish = builder.Task("publish", _ => Task.CompletedTask).Requires(good);
        var report = builder.Task("report", _ => Task.CompletedTask).Requires(bad);
        var reporter = new RecordingReporter();
        var second = await Runner.RunAsync(
            builder.Build(),
            new RunOptions { Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id), Reporters = [reporter] });

        second.Outcome.ShouldBe(RunOutcome.Incomplete);
        second.Snapshot[publish.Path].IsEarlierSuccess.ShouldBeTrue();
        second.Snapshot[report.Path].IsEarlierSuccess.ShouldBeTrue();
        second.Snapshot.Counts[NodeStatus.Succeeded].ShouldBe(3);
        reporter.Lines.ShouldNotContain(line => line.StartsWith("publish", StringComparison.Ordinal));
        reporter.Lines.ShouldNotContain(line => line.StartsWith("report", StringComparison.Ordinal));
    }

    [Test]
    public async Task A_run_with_no_journal_has_none_in_its_snapshot_and_no_earlier_successes()
    {
        var builder = new GraphBuilder();
        var load = builder.Task("load", _ => Task.CompletedTask);

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });

        result.Snapshot.Journal.ShouldBeNull();
        result.Snapshot.Counts.EarlierSuccesses.ShouldBe(0);
        result.Snapshot[load.Path].IsEarlierSuccess.ShouldBeFalse();
    }
}
