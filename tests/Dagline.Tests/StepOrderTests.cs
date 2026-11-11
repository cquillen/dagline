using Dagline.TestSupport;

namespace Dagline.Tests;

public class StepOrderTests
{
    [Test]
    public async Task Within_one_step_come_the_settle_the_tasks_it_blocks_those_it_makes_ready_the_groups_that_settle_and_the_tasks_that_start()
    {
        var builder = new GraphBuilder();
        var audit = builder.Task("audit", _ => Task.CompletedTask);
        var checks = builder.Group("checks");
        checks.Group("tenant-a").Task("verify", _ => throw new InvalidOperationException("entity 3 has a bad date"));
        var release = builder.Group("release");
        var notify = release.Group("tenant-a").Task("notify", _ => Task.CompletedTask);
        var publish = release.Task("publish", _ => Task.CompletedTask).Requires(checks);
        notify.Requires(publish);
        builder.Task("report", _ => Task.CompletedTask).After(checks);
        audit.After(release);
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        result.Outcome.ShouldBe(RunOutcome.Incomplete);
        reporter.Lines.Take(14).ShouldBe(
        [
            "run started",
            "checks/tenant-a/verify: pending -> ready",
            "checks/tenant-a/verify: ready -> running",
            "checks/tenant-a/verify: running -> failed",
            "release/tenant-a/notify: pending -> blocked",
            "release/publish: pending -> blocked",
            "audit: pending -> ready",
            "report: pending -> ready",
            "group checks/tenant-a settled: failed",
            "group release/tenant-a settled: blocked",
            "group checks settled: failed",
            "group release settled: blocked",
            "audit: ready -> running",
            "report: ready -> running",
        ]);
        var step = reporter.Events.Skip(3).Take(11).ToList();
        step.ShouldAllBe(runEvent => runEvent.At == step[0].At);
        var asOfTheSettle = reporter.Snapshots[3];
        asOfTheSettle[publish.Path].Status.ShouldBe(NodeStatus.Pending);
        asOfTheSettle[checks.Path].Status.ShouldBe(NodeStatus.Failed);
        var asOfTheLastBlock = reporter.Snapshots[5];
        asOfTheLastBlock[publish.Path].Status.ShouldBe(NodeStatus.Blocked);
        asOfTheLastBlock[audit.Path].Status.ShouldBe(NodeStatus.Pending);
    }

    [Test]
    public async Task Groups_at_one_depth_that_settle_in_one_step_are_announced_in_declaration_order()
    {
        var builder = new GraphBuilder();
        var first = builder.Group("shard-1");
        var load = first.Task("load", _ => Task.CompletedTask);
        var second = builder.Group("shard-2");
        var prepare = second.Task("prepare", _ => throw new InvalidOperationException("the store is offline"));
        load.Requires(prepare);
        var reporter = new RecordingReporter();

        await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        // The step settles shard-2 first, with its failed task, and shard-1 only once that failure has blocked its task.
        reporter.Lines.ShouldBe(
        [
            "run started",
            "shard-2/prepare: pending -> ready",
            "shard-2/prepare: ready -> running",
            "shard-2/prepare: running -> failed",
            "shard-1/load: pending -> blocked",
            "group shard-1 settled: blocked",
            "group shard-2 settled: failed",
            "run ended: incomplete",
        ]);
    }
}
