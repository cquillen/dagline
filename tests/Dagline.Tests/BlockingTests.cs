using Dagline.TestSupport;

namespace Dagline.Tests;

public class BlockingTests
{
    [Test]
    public async Task A_task_that_requires_a_failed_task_is_blocked_at_once_while_another_of_its_upstreams_is_still_running()
    {
        var prepare = new PausedBody();
        var loadSettled = new PausedBody();
        var builder = new GraphBuilder();
        var load = builder.Task("load", _ => throw new InvalidOperationException("no entities to load"));
        var prepareTask = builder.Task("prepare", prepare.Body);
        var publish = builder.Task("publish", _ => Task.CompletedTask).Requires(load).Requires(prepareTask);
        builder.Task("load-settled", loadSettled.Body).After(load);
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        var source = await reporter.Source;
        await loadSettled.Started;
        var prepareStillRunning = source.Current;
        loadSettled.Release();
        prepare.Release();
        var result = await run;

        prepareStillRunning[prepareTask.Path].Status.ShouldBe(NodeStatus.Running);
        prepareStillRunning[publish.Path].Status.ShouldBe(NodeStatus.Blocked);
        prepareStillRunning[publish.Path].BlockedBy.ShouldBe(load.Path);
        result.Outcome.ShouldBe(RunOutcome.Incomplete);
        reporter.Lines.Take(9).ShouldBe(
        [
            "run started",
            "load: pending -> ready",
            "prepare: pending -> ready",
            "load: ready -> running",
            "prepare: ready -> running",
            "load: running -> failed",
            "publish: pending -> blocked",
            "load-settled: pending -> ready",
            "load-settled: ready -> running",
        ]);
        reporter.Lines.Count(line => line.StartsWith("publish:", StringComparison.Ordinal)).ShouldBe(1);
        result.Snapshot[prepareTask.Path].Status.ShouldBe(NodeStatus.Succeeded);
    }

    [Test]
    public async Task The_cascade_reaches_every_task_that_requires_a_blocked_task_and_each_names_the_one_failed_task()
    {
        var builder = new GraphBuilder();
        var load = builder.Task("load", _ => throw new InvalidOperationException("no entities to load"));
        var process = builder.Task("process", _ => Task.CompletedTask).Requires(load);
        var publish = builder.Task("publish", _ => Task.CompletedTask).Requires(process);
        var notify = builder.Task("notify", _ => Task.CompletedTask).Requires(publish).Requires(process);
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        result.Outcome.ShouldBe(RunOutcome.Incomplete);
        reporter.Lines.ShouldBe(
        [
            "run started",
            "load: pending -> ready",
            "load: ready -> running",
            "load: running -> failed",
            "process: pending -> blocked",
            "publish: pending -> blocked",
            "notify: pending -> blocked",
            "run ended: incomplete",
        ]);
        foreach (var blocked in new[] { process, publish, notify })
        {
            result.Snapshot[blocked.Path].BlockedBy.ShouldBe(load.Path);
        }
    }

    [Test]
    public async Task A_task_that_requires_a_group_is_blocked_as_soon_as_one_task_beneath_the_group_fails()
    {
        var verify = new PausedBody();
        var loadSettled = new PausedBody();
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1");
        var load = shard.Group("tenant-a").Task("load", _ => throw new InvalidOperationException("no entities to load"));
        var verifyTask = shard.Task("verify", verify.Body);
        var publish = builder.Task("publish", _ => Task.CompletedTask).Requires(shard);
        builder.Task("load-settled", loadSettled.Body).After(load);
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        var source = await reporter.Source;
        await loadSettled.Started;
        var groupNotSettled = source.Current;
        loadSettled.Release();
        verify.Release();
        var result = await run;

        groupNotSettled[shard.Path].Status.ShouldBe(NodeStatus.Running);
        groupNotSettled[verifyTask.Path].Status.ShouldBe(NodeStatus.Running);
        groupNotSettled[publish.Path].Status.ShouldBe(NodeStatus.Blocked);
        groupNotSettled[publish.Path].BlockedBy.ShouldBe(load.Path);
        var lines = reporter.Lines.ToList();
        lines.IndexOf("publish: pending -> blocked").ShouldBe(lines.IndexOf("shard-1/tenant-a/load: running -> failed") + 1);
        lines.IndexOf("publish: pending -> blocked").ShouldBeLessThan(lines.IndexOf("group shard-1 settled: failed"));
        result.Snapshot[verifyTask.Path].Status.ShouldBe(NodeStatus.Succeeded);
    }

    [Test]
    public async Task A_task_that_requires_a_group_is_blocked_when_a_task_beneath_the_group_is_blocked()
    {
        var builder = new GraphBuilder();
        var prepare = builder.Task("prepare", _ => throw new InvalidOperationException("the store is offline"));
        var shard = builder.Group("shard-1").Requires(prepare);
        var load = shard.Group("tenant-a").Task("load", _ => Task.CompletedTask);
        var publish = builder.Task("publish", _ => Task.CompletedTask).Requires(shard);
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        reporter.Lines.ShouldBe(
        [
            "run started",
            "prepare: pending -> ready",
            "prepare: ready -> running",
            "prepare: running -> failed",
            "shard-1/tenant-a/load: pending -> blocked",
            "publish: pending -> blocked",
            "group shard-1/tenant-a settled: blocked",
            "group shard-1 settled: blocked",
            "run ended: incomplete",
        ]);
        result.Snapshot[load.Path].BlockedBy.ShouldBe(prepare.Path);
        result.Snapshot[publish.Path].BlockedBy.ShouldBe(prepare.Path);
    }

    [Test]
    public async Task A_blocked_task_keeps_its_first_cause_when_another_task_it_requires_fails_later()
    {
        var verify = new PausedBody();
        var loadSettled = new PausedBody();
        var builder = new GraphBuilder();
        var load = builder.Task("load", _ => throw new InvalidOperationException("no entities to load"));
        var verifyTask = builder.Task("verify", async context =>
        {
            await verify.Body(context);
            throw new InvalidOperationException("entity 3 has a bad date");
        });
        var publish = builder.Task("publish", _ => Task.CompletedTask).Requires(load).Requires(verifyTask);
        var notify = builder.Task("notify", _ => Task.CompletedTask).Requires(publish);
        var archive = builder.Task("archive", _ => Task.CompletedTask).Requires(verifyTask);
        builder.Task("load-settled", loadSettled.Body).After(load);
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        await loadSettled.Started;
        verify.Release();
        loadSettled.Release();
        var result = await run;

        result.Snapshot[publish.Path].BlockedBy.ShouldBe(load.Path);
        result.Snapshot[notify.Path].BlockedBy.ShouldBe(load.Path);
        result.Snapshot[archive.Path].BlockedBy.ShouldBe(verifyTask.Path);
        var lines = reporter.Lines.ToList();
        lines.Count(line => line.StartsWith("publish:", StringComparison.Ordinal)).ShouldBe(1);
        lines.Count(line => line.StartsWith("notify:", StringComparison.Ordinal)).ShouldBe(1);
        lines.IndexOf("archive: pending -> blocked").ShouldBe(lines.IndexOf("verify: running -> failed") + 1);
    }

    [Test]
    public async Task A_blocked_task_names_the_failed_task_and_has_a_settle_time_and_no_start_time()
    {
        var builder = new GraphBuilder();
        var load = builder.Task("load", _ => throw new InvalidOperationException("no entities to load"));
        var publish = builder.Task("publish", _ => Task.CompletedTask).Requires(load);
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        var failed = result.Snapshot[load.Path];
        var blocked = result.Snapshot[publish.Path];
        blocked.Status.ShouldBe(NodeStatus.Blocked);
        blocked.BlockedBy.ShouldBe(load.Path);
        blocked.Started.ShouldBeNull();
        blocked.Settled.ShouldNotBeNull();
        blocked.Settled.ShouldBe(failed.Settled);
        blocked.Failure.ShouldBeNull();
        failed.BlockedBy.ShouldBeNull();
        var blockedEvent = reporter.Events.OfType<TaskStatusChanged>().Single(changed => changed.To is NodeStatus.Blocked);
        blockedEvent.Node.ShouldBeSameAs(blocked);
        blockedEvent.From.ShouldBe(NodeStatus.Pending);
        blockedEvent.At.ShouldBe(failed.Settled!.Value);
    }
}
