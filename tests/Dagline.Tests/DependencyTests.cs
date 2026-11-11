using Dagline.TestSupport;

namespace Dagline.Tests;

public class DependencyTests
{
    private static readonly string[] _shards = ["shard-1", "shard-2", "shard-3"];

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
    public async Task A_gate_is_a_dependency_on_a_collection_of_group_handles()
    {
        var builder = new GraphBuilder();
        List<GroupNode> shards = [];
        foreach (var segment in _shards)
        {
            var shard = builder.Group(segment);
            shard.Task("load", _ => Task.CompletedTask);
            shards.Add(shard);
        }

        var publish = builder.Task("publish", _ => Task.CompletedTask).Requires(shards);
        var report = builder.Task("report", _ => Task.CompletedTask).After(shards);
        var archive = builder.Group("archive").Requires(shards.Take(2)).After(shards.Skip(2));
        archive.Task("store", _ => Task.CompletedTask);
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        result.Outcome.ShouldBe(RunOutcome.Complete);
        var lines = reporter.Lines.ToList();
        var lastLoad = _shards.Max(shard => lines.IndexOf($"{shard}/load: running -> succeeded"));
        lines.Skip(lastLoad + 1).Take(3).ShouldBe(
        [
            "publish: pending -> ready",
            "report: pending -> ready",
            "archive/store: pending -> ready",
        ]);
        result.Snapshot[publish.Path].Dependencies.ShouldBe(
        [
            new Dependency(NodePath.Parse("shard-1"), DependencyKind.Requires),
            new Dependency(NodePath.Parse("shard-2"), DependencyKind.Requires),
            new Dependency(NodePath.Parse("shard-3"), DependencyKind.Requires),
        ]);
        result.Snapshot[report.Path].Dependencies.ShouldBe(
        [
            new Dependency(NodePath.Parse("shard-1"), DependencyKind.After),
            new Dependency(NodePath.Parse("shard-2"), DependencyKind.After),
            new Dependency(NodePath.Parse("shard-3"), DependencyKind.After),
        ]);
        result.Snapshot[archive.Path].Dependencies.ShouldBe(
        [
            new Dependency(NodePath.Parse("shard-1"), DependencyKind.Requires),
            new Dependency(NodePath.Parse("shard-2"), DependencyKind.Requires),
            new Dependency(NodePath.Parse("shard-3"), DependencyKind.After),
        ]);
    }

    [Test]
    public async Task A_dependency_on_an_empty_collection_of_handles_is_no_dependency()
    {
        GroupNode[] noShards = [];
        var builder = new GraphBuilder();
        var publish = builder.Task("publish", _ => Task.CompletedTask).Requires(noShards).After(noShards);
        var archive = builder.Group("archive").Requires(noShards).After(noShards);
        archive.Task("store", _ => Task.CompletedTask);
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        result.Outcome.ShouldBe(RunOutcome.Complete);
        reporter.Lines.Take(3).ShouldBe(
        [
            "run started",
            "publish: pending -> ready",
            "archive/store: pending -> ready",
        ]);
        result.Snapshot[publish.Path].Dependencies.ShouldBeEmpty();
        result.Snapshot[archive.Path].Dependencies.ShouldBeEmpty();
    }

    [Test]
    public async Task A_dependency_on_an_empty_group_is_met_at_once()
    {
        var builder = new GraphBuilder();
        var empty = builder.Group("shard-1");
        var holdsOnlyAnEmptyGroup = builder.Group("shard-2");
        holdsOnlyAnEmptyGroup.Group("tenant-a");
        var publish = builder.Task("publish", _ => Task.CompletedTask).Requires(empty).Requires(holdsOnlyAnEmptyGroup);
        builder.Task("report", _ => Task.CompletedTask).After(empty);
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        result.Outcome.ShouldBe(RunOutcome.Complete);
        reporter.Lines.Take(5).ShouldBe(
        [
            "run started",
            "publish: pending -> ready",
            "report: pending -> ready",
            "publish: ready -> running",
            "report: ready -> running",
        ]);
        result.Snapshot[publish.Path].Dependencies.ShouldBe(
        [
            new Dependency(empty.Path, DependencyKind.Requires),
            new Dependency(holdsOnlyAnEmptyGroup.Path, DependencyKind.Requires),
        ]);
    }

    [Test]
    public async Task Both_kinds_on_one_upstream_are_one_requires_and_exact_duplicates_collapse()
    {
        var builder = new GraphBuilder();
        var prepare = builder.Task("prepare", _ => Task.CompletedTask);
        var load = builder.Task("load", _ => Task.CompletedTask);
        var publish = builder.Task("publish", _ => Task.CompletedTask)
            .After(prepare)
            .After(load)
            .Requires(prepare)
            .After(load)
            .Requires([prepare, prepare]);
        var shard = builder.Group("shard-1").After(prepare).After([prepare, load]);
        var member = shard.Task("load", _ => Task.CompletedTask).Requires(prepare).Requires(load);
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        result.Outcome.ShouldBe(RunOutcome.Complete);
        result.Snapshot[publish.Path].Dependencies.ShouldBe(
        [
            new Dependency(prepare.Path, DependencyKind.Requires),
            new Dependency(load.Path, DependencyKind.After),
        ]);
        result.Snapshot[shard.Path].Dependencies.ShouldBe(
        [
            new Dependency(prepare.Path, DependencyKind.After),
            new Dependency(load.Path, DependencyKind.After),
        ]);
        result.Snapshot[member.Path].Dependencies.ShouldBe(
        [
            new Dependency(prepare.Path, DependencyKind.Requires),
            new Dependency(load.Path, DependencyKind.Requires),
        ]);
        reporter.Lines.Count(line => line.StartsWith("publish:", StringComparison.Ordinal)).ShouldBe(3);
        reporter.Lines.Count(line => line.StartsWith("shard-1/load:", StringComparison.Ordinal)).ShouldBe(3);
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
