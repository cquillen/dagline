using Dagline.TestSupport;

namespace Dagline.Tests;

public class LimitUseTests
{
    [Test]
    public async Task The_snapshot_shows_the_cap_and_each_limit_the_graph_names_with_its_size_and_slots_in_use()
    {
        var firstLoad = new PausedBody();
        var secondLoad = new PausedBody();
        var export = new PausedBody();
        var publish = new PausedBody();
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1").Limit("database");
        shard.Task("load-1", firstLoad.Body);
        var held = shard.Task("load-2", secondLoad.Body);
        builder.Task("export", export.Body).Limit("files");
        builder.Task("publish", publish.Body);
        builder.Group("shard-2").Limit("network");
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(
            builder.Build(),
            new RunOptions
            {
                Journal = Journal.None,
                Cap = 3,
                Limits = new Dictionary<string, int> { ["network"] = 5, ["files"] = 2, ["database"] = 1, ["queue"] = 9 },
                Reporters = [reporter],
            });
        var source = await reporter.Source;
        await firstLoad.Started;
        await export.Started;
        await publish.Started;
        var bodiesPaused = source.Current;
        firstLoad.Release();
        secondLoad.Release();
        export.Release();
        publish.Release();
        var result = await run;

        bodiesPaused[held.Path].Status.ShouldBe(NodeStatus.Ready);
        bodiesPaused.Cap.ShouldBe(new LimitUse(Size: 3, InUse: 3));
        bodiesPaused.Limits.ShouldBe(
        [
            KeyValuePair.Create("database", new LimitUse(Size: 1, InUse: 1)),
            KeyValuePair.Create("files", new LimitUse(Size: 2, InUse: 1)),
            KeyValuePair.Create("network", new LimitUse(Size: 5, InUse: 0)),
        ]);
        result.Snapshot.Cap.ShouldBe(new LimitUse(Size: 3, InUse: 0));
        result.Snapshot.Limits.Values.ShouldAllBe(limit => limit.InUse == 0);
    }

    [Test]
    public async Task A_task_takes_its_slots_when_it_starts_and_gives_them_back_when_it_succeeds()
    {
        var builder = new GraphBuilder();
        builder.Task("load-1", _ => Task.CompletedTask).Limit("database");
        builder.Task("load-2", _ => Task.CompletedTask).Limit("database");
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(
            builder.Build(),
            new RunOptions
            {
                Journal = Journal.None,
                Limits = new Dictionary<string, int> { ["database"] = 1 },
                Reporters = [reporter],
            });

        // The snapshot handed with an event is the run as of that event, so it shows the slots as the event left them.
        result.Outcome.ShouldBe(RunOutcome.Complete);
        reporter.Lines.Zip(reporter.Snapshots, (line, snapshot) => (line, snapshot.Cap.InUse, snapshot.Limits["database"].InUse)).ShouldBe(
        [
            ("run started", 0, 0),
            ("load-1: pending -> ready", 0, 0),
            ("load-2: pending -> ready", 0, 0),
            ("load-1: ready -> running", 1, 1),
            ("load-1: running -> succeeded", 0, 0),
            ("load-2: ready -> running", 1, 1),
            ("load-2: running -> succeeded", 0, 0),
            ("run ended: complete", 0, 0),
        ]);
    }

    [Test]
    public async Task Each_node_shows_the_limits_named_on_it_and_not_those_it_inherits()
    {
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1").Limit("network").Limit("database").Limit("network");
        var load = shard.Task("load", _ => Task.CompletedTask).Limit("files");
        var verify = shard.Task("verify", _ => Task.CompletedTask);

        var result = await Runner.RunAsync(
            builder.Build(),
            new RunOptions
            {
                Journal = Journal.None,
                Limits = new Dictionary<string, int> { ["network"] = 1, ["database"] = 1, ["files"] = 1 },
            });

        result.Snapshot[shard.Path].Limits.ShouldBe(["network", "database"]);
        result.Snapshot[load.Path].Limits.ShouldBe(["files"]);
        result.Snapshot[verify.Path].Limits.ShouldBeEmpty();
        result.Snapshot.Limits.Keys.ShouldBe(["network", "database", "files"]);
    }

    [Test]
    public async Task A_graph_that_names_no_limit_has_only_the_cap_which_defaults_to_sixteen()
    {
        var builder = new GraphBuilder();
        builder.Task("prepare", _ => Task.CompletedTask);

        var result = await Runner.RunAsync(
            builder.Build(),
            new RunOptions { Journal = Journal.None, Limits = new Dictionary<string, int> { ["database"] = 2 } });

        result.Snapshot.Cap.ShouldBe(new LimitUse(Size: 16, InUse: 0));
        result.Snapshot.Limits.ShouldBeEmpty();
    }
}
