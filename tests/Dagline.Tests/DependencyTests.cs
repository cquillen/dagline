using Dagline.TestSupport;

namespace Dagline.Tests;

public class DependencyTests
{
    [Test]
    public async Task An_after_task_runs_once_its_upstream_has_settled()
    {
        var load = new PausedBody();
        var builder = new GraphBuilder();
        var loadTask = builder.Task("load", load.Body);
        var report = builder.Task("report", _ => Task.CompletedTask).After(loadTask);
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        var source = await reporter.Source;
        await load.Started;
        var midRun = source.Current;
        load.Release();
        var result = await run;

        midRun[report.Path].Status.ShouldBe(NodeStatus.Pending);
        result.Outcome.ShouldBe(RunOutcome.Complete);
        reporter.Lines.ShouldBe(
        [
            "run started",
            "load: pending -> ready",
            "load: ready -> running",
            "load: running -> succeeded",
            "report: pending -> ready",
            "report: ready -> running",
            "report: running -> succeeded",
            "run ended: complete",
        ]);
        result.Snapshot[report.Path].Dependencies.ShouldBe([new Dependency(loadTask.Path, DependencyKind.After)]);
    }

    [Test]
    public async Task A_dependency_declared_on_a_group_applies_to_every_task_beneath_that_group()
    {
        var prepare = new PausedBody();
        var builder = new GraphBuilder();
        var prepareTask = builder.Task("prepare", prepare.Body);
        var shard = builder.Group("shard-1").Requires(prepareTask);
        var load = shard.Task("load", _ => Task.CompletedTask);
        var deepLoad = shard.Group("tenant-a").Task("load", _ => Task.CompletedTask);
        var reports = builder.Group("reports").After(prepareTask);
        var report = reports.Task("report", _ => Task.CompletedTask);
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        var source = await reporter.Source;
        await prepare.Started;
        var prepareRunning = source.Current;
        prepare.Release();
        var result = await run;

        prepareRunning[load.Path].Status.ShouldBe(NodeStatus.Pending);
        prepareRunning[deepLoad.Path].Status.ShouldBe(NodeStatus.Pending);
        prepareRunning[report.Path].Status.ShouldBe(NodeStatus.Pending);
        result.Outcome.ShouldBe(RunOutcome.Complete);
        var lines = reporter.Lines.ToList();
        var prepareSucceeded = lines.IndexOf("prepare: running -> succeeded");
        lines.Skip(prepareSucceeded + 1).Take(3).ShouldBe(
        [
            "shard-1/load: pending -> ready",
            "shard-1/tenant-a/load: pending -> ready",
            "reports/report: pending -> ready",
        ]);
        result.Snapshot[shard.Path].Dependencies.ShouldBe([new Dependency(prepareTask.Path, DependencyKind.Requires)]);
        result.Snapshot[reports.Path].Dependencies.ShouldBe([new Dependency(prepareTask.Path, DependencyKind.After)]);
        result.Snapshot[load.Path].Dependencies.ShouldBeEmpty();
    }

    [Test]
    public async Task A_dependency_of_either_kind_against_a_group_waits_for_every_task_beneath_it_at_any_depth()
    {
        var prepare = new PausedBody();
        var load = new PausedBody();
        var prepareSettled = new PausedBody();
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1");
        var prepareTask = shard.Task("prepare", prepare.Body);
        shard.Group("tenant-a").Group("batch-1").Task("load", load.Body);
        var publish = builder.Task("publish", _ => Task.CompletedTask).Requires(shard);
        var report = builder.Task("report", _ => Task.CompletedTask).After(shard);
        builder.Task("prepare-settled", prepareSettled.Body).Requires(prepareTask);
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        var source = await reporter.Source;
        prepare.Release();
        await prepareSettled.Started;
        var oneTaskLeft = source.Current;
        prepareSettled.Release();
        load.Release();
        var result = await run;

        oneTaskLeft[prepareTask.Path].Status.ShouldBe(NodeStatus.Succeeded);
        oneTaskLeft[publish.Path].Status.ShouldBe(NodeStatus.Pending);
        oneTaskLeft[report.Path].Status.ShouldBe(NodeStatus.Pending);
        result.Outcome.ShouldBe(RunOutcome.Complete);
        var lines = reporter.Lines.ToList();
        var lastTaskBeneath = lines.IndexOf("shard-1/tenant-a/batch-1/load: running -> succeeded");
        lines.IndexOf("publish: pending -> ready").ShouldBe(lastTaskBeneath + 1);
        lines.IndexOf("report: pending -> ready").ShouldBe(lastTaskBeneath + 2);
        result.Snapshot[publish.Path].Dependencies.ShouldBe([new Dependency(shard.Path, DependencyKind.Requires)]);
        result.Snapshot[report.Path].Dependencies.ShouldBe([new Dependency(shard.Path, DependencyKind.After)]);
    }
}
