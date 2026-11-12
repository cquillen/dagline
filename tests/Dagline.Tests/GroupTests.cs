using Dagline.TestSupport;

namespace Dagline.Tests;

public class GroupTests
{
    private static readonly string[] _shards = ["shard-1", "shard-2"];
    private static readonly string[] _tenants = ["tenant-a", "tenant-b"];

    [Test]
    public async Task A_title_is_carried_to_the_snapshot_and_is_separate_from_the_segment()
    {
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1").Title("Shard one (primary)");
        var load = shard.Task("load", _ => Task.CompletedTask).Title("Load entities");
        shard.Task("verify", _ => Task.CompletedTask);

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });

        result.Snapshot.Nodes.Select(node => (node.Path.ToString(), node.Title)).ShouldBe(
        [
            ("shard-1", "Shard one (primary)"),
            ("shard-1/load", "Load entities"),
            ("shard-1/verify", null),
        ]);
        shard.Path.ShouldBe(NodePath.Parse("shard-1"));
        load.Path.ShouldBe(NodePath.Parse("shard-1/load"));
    }

    [Test]
    public async Task A_group_that_has_not_settled_is_running_if_any_task_beneath_it_is_running_else_ready_if_any_is_ready_else_pending()
    {
        var prepare = new PausedBody();
        var builder = new GraphBuilder();
        var working = builder.Group("shard-1");
        var prepareTask = working.Group("tenant-a").Task("prepare", prepare.Body);
        working.Task("load", _ => Task.CompletedTask).Requires(prepareTask);
        var waiting = builder.Group("shard-2").Requires(prepareTask);
        waiting.Task("load", _ => Task.CompletedTask);
        waiting.Task("verify", _ => Task.CompletedTask);
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        var source = await reporter.Source;
        await prepare.Started;
        var prepareRunning = source.Current;
        prepare.Release();
        var result = await run;

        prepareRunning[working.Path].Status.ShouldBe(NodeStatus.Running);
        prepareRunning[NodePath.Parse("shard-1/tenant-a")].Status.ShouldBe(NodeStatus.Running);
        prepareRunning[waiting.Path].Status.ShouldBe(NodeStatus.Pending);

        // Nothing holds a ready task back, so a ready group is read from the snapshot handed with the event that made it ready.
        var lines = reporter.Lines.ToList();
        var firstReady = reporter.Snapshots[lines.IndexOf("shard-2/load: pending -> ready")];
        firstReady[NodePath.Parse("shard-2/verify")].Status.ShouldBe(NodeStatus.Pending);
        firstReady[waiting.Path].Status.ShouldBe(NodeStatus.Ready);
        var firstStarted = reporter.Snapshots[lines.IndexOf("shard-2/load: ready -> running")];
        firstStarted[NodePath.Parse("shard-2/verify")].Status.ShouldBe(NodeStatus.Ready);
        firstStarted[waiting.Path].Status.ShouldBe(NodeStatus.Running);

        result.Snapshot[working.Path].Status.ShouldBe(NodeStatus.Succeeded);
        result.Snapshot[waiting.Path].Status.ShouldBe(NodeStatus.Succeeded);
    }

    [Test]
    public async Task A_group_is_ready_while_every_task_beneath_it_is_held_ready_by_a_full_cap()
    {
        var prepare = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("prepare", prepare.Body);
        var held = builder.Group("shard-1");
        held.Task("load", _ => Task.CompletedTask);
        held.Group("tenant-a").Task("verify", _ => Task.CompletedTask);
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Cap = 1, Reporters = [reporter] });
        var source = await reporter.Source;
        await prepare.Started;
        var prepareRunning = source.Current;
        prepare.Release();
        var result = await run;

        prepareRunning[NodePath.Parse("shard-1/load")].Status.ShouldBe(NodeStatus.Ready);
        prepareRunning[held.Path].Status.ShouldBe(NodeStatus.Ready);
        prepareRunning[NodePath.Parse("shard-1/tenant-a")].Status.ShouldBe(NodeStatus.Ready);
        prepareRunning[held.Path].Counts[NodeStatus.Ready].ShouldBe(2);
        result.Snapshot[held.Path].Status.ShouldBe(NodeStatus.Succeeded);
    }

    [Test]
    public async Task An_empty_group_is_succeeded_from_the_start_to_the_end_of_the_run()
    {
        var prepare = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("prepare", prepare.Body);
        var empty = builder.Group("shard-1");
        var holdsOnlyAnEmptyGroup = builder.Group("shard-2");
        holdsOnlyAnEmptyGroup.Group("tenant-a");
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        var source = await reporter.Source;
        await prepare.Started;
        var prepareRunning = source.Current;
        prepare.Release();
        var result = await run;

        reporter.Snapshots[0][empty.Path].Status.ShouldBe(NodeStatus.Succeeded);
        prepareRunning[empty.Path].Status.ShouldBe(NodeStatus.Succeeded);
        prepareRunning[holdsOnlyAnEmptyGroup.Path].Status.ShouldBe(NodeStatus.Succeeded);
        result.Snapshot[empty.Path].Status.ShouldBe(NodeStatus.Succeeded);
        result.Snapshot[holdsOnlyAnEmptyGroup.Path].Status.ShouldBe(NodeStatus.Succeeded);
        result.Snapshot[empty.Path].Counts.Total.ShouldBe(0);
        result.Snapshot.Counts[NodeStatus.Succeeded].ShouldBe(1);
    }

    [Test]
    public async Task The_counts_of_a_group_cover_the_tasks_beneath_it_at_any_depth_and_those_of_a_task_itself()
    {
        var prepare = new PausedBody();
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1");
        var tenant = shard.Group("tenant-a");
        var prepareTask = tenant.Group("batch-1").Task("prepare", prepare.Body);
        tenant.Task("load", _ => Task.CompletedTask).Requires(prepareTask);
        shard.Task("verify", _ => Task.CompletedTask).Requires(prepareTask);
        builder.Task("publish", _ => Task.CompletedTask).Requires(shard);
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        var source = await reporter.Source;
        await prepare.Started;
        var prepareRunning = source.Current;
        prepare.Release();
        var result = await run;

        prepareRunning[shard.Path].Counts.ByStatus().ShouldBe(Counts(pending: 2, running: 1));
        prepareRunning[tenant.Path].Counts.ByStatus().ShouldBe(Counts(pending: 1, running: 1));
        prepareRunning[NodePath.Parse("shard-1/tenant-a/batch-1")].Counts.ByStatus().ShouldBe(Counts(running: 1));
        prepareRunning[prepareTask.Path].Counts.ByStatus().ShouldBe(Counts(running: 1));
        prepareRunning[NodePath.Parse("publish")].Counts.ByStatus().ShouldBe(Counts(pending: 1));
        result.Snapshot[shard.Path].Counts.ByStatus().ShouldBe(Counts(succeeded: 3));
        result.Snapshot[tenant.Path].Counts.ByStatus().ShouldBe(Counts(succeeded: 2));
        result.Snapshot[prepareTask.Path].Counts.ByStatus().ShouldBe(Counts(succeeded: 1));
        result.Snapshot.Counts.ByStatus().ShouldBe(Counts(succeeded: 4));
    }

    [Test]
    public async Task Nested_loops_build_groups_to_any_depth_and_every_task_beneath_them_runs()
    {
        var bodiesRun = 0;
        var builder = new GraphBuilder();
        builder.Task("prepare", _ => CountBody());
        foreach (var shard in _shards)
        {
            var shardGroup = builder.Group(shard);
            foreach (var tenant in _tenants)
            {
                var tenantGroup = shardGroup.Group(tenant);
                tenantGroup.Task("load", _ => CountBody());
            }
        }

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });

        result.Outcome.ShouldBe(RunOutcome.Complete);
        bodiesRun.ShouldBe(5);
        result.Snapshot.Nodes.Select(node => (node.Path.ToString(), node.IsGroup)).ShouldBe(
        [
            ("prepare", false),
            ("shard-1", true),
            ("shard-1/tenant-a", true),
            ("shard-1/tenant-a/load", false),
            ("shard-1/tenant-b", true),
            ("shard-1/tenant-b/load", false),
            ("shard-2", true),
            ("shard-2/tenant-a", true),
            ("shard-2/tenant-a/load", false),
            ("shard-2/tenant-b", true),
            ("shard-2/tenant-b/load", false),
        ]);

        Task CountBody()
        {
            Interlocked.Increment(ref bodiesRun);
            return Task.CompletedTask;
        }
    }

    private static Dictionary<NodeStatus, int> Counts(int pending = 0, int running = 0, int succeeded = 0) => new()
    {
        [NodeStatus.Pending] = pending,
        [NodeStatus.Ready] = 0,
        [NodeStatus.Running] = running,
        [NodeStatus.Succeeded] = succeeded,
        [NodeStatus.Failed] = 0,
        [NodeStatus.Blocked] = 0,
        [NodeStatus.Cancelled] = 0,
    };
}
