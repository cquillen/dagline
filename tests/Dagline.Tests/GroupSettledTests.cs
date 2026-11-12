using Dagline.TestSupport;

namespace Dagline.Tests;

public class GroupSettledTests
{
    [Test]
    public async Task Groups_settle_deepest_first_after_the_tasks_made_ready_and_before_the_tasks_started()
    {
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1");
        shard.Group("tenant-a").Task("load", _ => Task.CompletedTask);
        builder.Task("publish", _ => Task.CompletedTask).Requires(shard);
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        result.Outcome.ShouldBe(RunOutcome.Complete);
        reporter.Lines.ShouldBe(
        [
            "run started",
            "shard-1/tenant-a/load: pending -> ready",
            "shard-1/tenant-a/load: ready -> running",
            "shard-1/tenant-a/load: running -> succeeded",
            "publish: pending -> ready",
            "group shard-1/tenant-a settled: succeeded",
            "group shard-1 settled: succeeded",
            "publish: ready -> running",
            "publish: running -> succeeded",
            "run ended: complete",
        ]);
    }

    [Test]
    public async Task A_group_settles_once_after_the_last_task_event_beneath_it()
    {
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1");
        var load = shard.Task("load", _ => Task.CompletedTask);
        shard.Group("tenant-a").Task("verify", _ => Task.CompletedTask).Requires(load);
        var reporter = new RecordingReporter();

        await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        reporter.Lines.ShouldBe(
        [
            "run started",
            "shard-1/load: pending -> ready",
            "shard-1/load: ready -> running",
            "shard-1/load: running -> succeeded",
            "shard-1/tenant-a/verify: pending -> ready",
            "shard-1/tenant-a/verify: ready -> running",
            "shard-1/tenant-a/verify: running -> succeeded",
            "group shard-1/tenant-a settled: succeeded",
            "group shard-1 settled: succeeded",
            "run ended: complete",
        ]);
    }

    [Test]
    public async Task A_group_settled_event_carries_the_status_and_the_counts_of_the_group_and_the_time_of_its_last_task()
    {
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1");
        shard.Task("load", _ => Task.CompletedTask);
        shard.Group("tenant-a").Task("verify", _ => Task.CompletedTask);
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        var events = reporter.Events.ToList();
        var settled = events.OfType<GroupSettled>().Single(groupSettled => groupSettled.Path == shard.Path);
        settled.Status.ShouldBe(NodeStatus.Succeeded);
        settled.Counts.ByStatus().ShouldBe(new Dictionary<NodeStatus, int>
        {
            [NodeStatus.Pending] = 0,
            [NodeStatus.Ready] = 0,
            [NodeStatus.Running] = 0,
            [NodeStatus.Succeeded] = 2,
            [NodeStatus.Failed] = 0,
            [NodeStatus.Blocked] = 0,
            [NodeStatus.Cancelled] = 0,
        });
        var lastTaskEvent = events.Take(events.IndexOf(settled)).OfType<TaskStatusChanged>().Last();
        lastTaskEvent.To.ShouldBe(NodeStatus.Succeeded);
        settled.At.ShouldBe(lastTaskEvent.At);
        var handedWithIt = reporter.Snapshots[events.IndexOf(settled)];
        handedWithIt[shard.Path].Status.ShouldBe(NodeStatus.Succeeded);
        handedWithIt[shard.Path].Counts.ByStatus().ShouldBe(settled.Counts.ByStatus());
        result.Snapshot[shard.Path].Counts.ByStatus().ShouldBe(settled.Counts.ByStatus());
    }

    [Test]
    public async Task A_group_that_is_settled_from_the_start_gets_no_event()
    {
        var builder = new GraphBuilder();
        builder.Group("shard-1").Group("tenant-a");
        builder.Task("publish", _ => Task.CompletedTask);
        var reporter = new RecordingReporter();

        await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        reporter.Lines.ShouldBe(
        [
            "run started",
            "publish: pending -> ready",
            "publish: ready -> running",
            "publish: running -> succeeded",
            "run ended: complete",
        ]);
    }
}
