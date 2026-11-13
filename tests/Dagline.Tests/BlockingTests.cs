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
    public async Task Tasks_that_do_not_require_the_failed_task_keep_running_and_succeed_and_the_run_is_incomplete()
    {
        var verify = new PausedBody();
        var loadSettled = new PausedBody();
        var builder = new GraphBuilder();
        var load = builder.Task("load", _ => throw new InvalidOperationException("no entities to load"));
        var verifyTask = builder.Task("verify", verify.Body);
        var archive = builder.Task("archive", _ => Task.CompletedTask).Requires(verifyTask);
        var publish = builder.Task("publish", _ => Task.CompletedTask).Requires(load);
        var loadSettledTask = builder.Task("load-settled", loadSettled.Body).After(load);
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        var source = await reporter.Source;
        await loadSettled.Started;
        var afterTheFailure = source.Current;
        loadSettled.Release();
        verify.Release();
        var result = await run;

        afterTheFailure[load.Path].Status.ShouldBe(NodeStatus.Failed);
        afterTheFailure[verifyTask.Path].Status.ShouldBe(NodeStatus.Running);
        afterTheFailure[archive.Path].Status.ShouldBe(NodeStatus.Pending);
        result.Outcome.ShouldBe(RunOutcome.Incomplete);
        result.Snapshot.Outcome.ShouldBe(RunOutcome.Incomplete);
        reporter.Events[^1].ShouldBe(new RunEnded(result.Snapshot.Ended!.Value, RunOutcome.Incomplete));
        result.Snapshot[verifyTask.Path].Status.ShouldBe(NodeStatus.Succeeded);
        result.Snapshot[archive.Path].Status.ShouldBe(NodeStatus.Succeeded);
        result.Snapshot[loadSettledTask.Path].Status.ShouldBe(NodeStatus.Succeeded);
        result.Snapshot[publish.Path].Status.ShouldBe(NodeStatus.Blocked);
        result.Snapshot.Counts.ByStatus().ShouldBe(new Dictionary<NodeStatus, int>
        {
            [NodeStatus.Pending] = 0,
            [NodeStatus.Ready] = 0,
            [NodeStatus.Running] = 0,
            [NodeStatus.Succeeded] = 3,
            [NodeStatus.Failed] = 1,
            [NodeStatus.Blocked] = 1,
            [NodeStatus.Cancelled] = 0,
        });
    }

    [Test]
    public async Task An_after_task_runs_when_its_upstream_failed_and_when_its_upstream_was_blocked()
    {
        var builder = new GraphBuilder();
        var load = builder.Task("load", _ => throw new InvalidOperationException("no entities to load"));
        var publish = builder.Task("publish", _ => Task.CompletedTask).Requires(load);
        var report = builder.Task("report", _ => Task.CompletedTask).After(load);
        var audit = builder.Task("audit", _ => Task.CompletedTask).After(publish);
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        reporter.Lines.Take(9).ShouldBe(
        [
            "run started",
            "load: pending -> ready",
            "load: ready -> running",
            "load: running -> failed",
            "publish: pending -> blocked",
            "report: pending -> ready",
            "audit: pending -> ready",
            "report: ready -> running",
            "audit: ready -> running",
        ]);
        result.Snapshot[publish.Path].Status.ShouldBe(NodeStatus.Blocked);
        result.Snapshot[report.Path].Status.ShouldBe(NodeStatus.Succeeded);
        result.Snapshot[audit.Path].Status.ShouldBe(NodeStatus.Succeeded);
        result.Outcome.ShouldBe(RunOutcome.Incomplete);
    }

    [Test]
    public async Task An_after_task_runs_once_a_group_it_waits_for_has_settled_failed_or_blocked()
    {
        var builder = new GraphBuilder();
        var checks = builder.Group("checks");
        checks.Task("verify", _ => throw new InvalidOperationException("entity 3 has a bad date"));
        var release = builder.Group("release").Requires(checks);
        release.Task("publish", _ => Task.CompletedTask);
        var report = builder.Task("report", _ => Task.CompletedTask).After(checks);
        var audit = builder.Task("audit", _ => Task.CompletedTask).After(release);

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });

        result.Snapshot[checks.Path].Status.ShouldBe(NodeStatus.Failed);
        result.Snapshot[release.Path].Status.ShouldBe(NodeStatus.Blocked);
        result.Snapshot[report.Path].Status.ShouldBe(NodeStatus.Succeeded);
        result.Snapshot[audit.Path].Status.ShouldBe(NodeStatus.Succeeded);
    }

    [Test]
    public async Task A_group_is_failed_when_a_task_beneath_it_failed_and_blocked_when_none_failed_and_one_is_blocked()
    {
        var builder = new GraphBuilder();
        var checks = builder.Group("checks");
        checks.Task("count", _ => Task.CompletedTask);
        var verify = checks.Group("tenant-a").Task("verify", _ => throw new InvalidOperationException("entity 3 has a bad date"));
        checks.Task("sign", _ => Task.CompletedTask).Requires(verify);
        var release = builder.Group("release");
        release.Task("notes", _ => Task.CompletedTask);
        release.Group("tenant-a").Task("publish", _ => Task.CompletedTask).Requires(checks);
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        var failedGroup = result.Snapshot[checks.Path];
        failedGroup.Status.ShouldBe(NodeStatus.Failed);
        failedGroup.Counts[NodeStatus.Succeeded].ShouldBe(1);
        failedGroup.Counts[NodeStatus.Failed].ShouldBe(1);
        failedGroup.Counts[NodeStatus.Blocked].ShouldBe(1);
        failedGroup.Failure.ShouldBeNull();
        result.Snapshot[NodePath.Parse("checks/tenant-a")].Status.ShouldBe(NodeStatus.Failed);
        var blockedGroup = result.Snapshot[release.Path];
        blockedGroup.Status.ShouldBe(NodeStatus.Blocked);
        blockedGroup.Counts[NodeStatus.Succeeded].ShouldBe(1);
        blockedGroup.Counts[NodeStatus.Failed].ShouldBe(0);
        blockedGroup.Counts[NodeStatus.Blocked].ShouldBe(1);
        blockedGroup.BlockedBy.ShouldBeNull();
        result.Snapshot[NodePath.Parse("release/tenant-a")].Status.ShouldBe(NodeStatus.Blocked);
        reporter.Lines.ShouldContain("group checks/tenant-a settled: failed");
        reporter.Lines.ShouldContain("group checks settled: failed");
        reporter.Lines.ShouldContain("group release/tenant-a settled: blocked");
        reporter.Lines.ShouldContain("group release settled: blocked");
    }

    [Test]
    public async Task A_task_with_both_kinds_on_one_upstream_one_from_its_group_and_one_of_its_own_is_blocked_when_that_upstream_fails()
    {
        var builder = new GraphBuilder();
        var prepare = builder.Task("prepare", _ => throw new InvalidOperationException("the store is offline"));
        var waitsAfter = builder.Group("shard-1").After(prepare);
        var ownRequires = waitsAfter.Task("load", _ => Task.CompletedTask).Requires(prepare);
        var inheritsAfter = waitsAfter.Task("report", _ => Task.CompletedTask);
        var needs = builder.Group("shard-2").Requires(prepare);
        var ownAfter = needs.Task("report", _ => Task.CompletedTask).After(prepare);

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });

        result.Snapshot[ownRequires.Path].Status.ShouldBe(NodeStatus.Blocked);
        result.Snapshot[ownRequires.Path].BlockedBy.ShouldBe(prepare.Path);
        result.Snapshot[inheritsAfter.Path].Status.ShouldBe(NodeStatus.Succeeded);
        result.Snapshot[ownAfter.Path].Status.ShouldBe(NodeStatus.Blocked);
        result.Snapshot[ownAfter.Path].BlockedBy.ShouldBe(prepare.Path);
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
