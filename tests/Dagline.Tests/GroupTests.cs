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
}
