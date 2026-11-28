using Dagline.TestSupport;

namespace Dagline.Tests;

public class TaskCountsTests
{
    [Test]
    public async Task A_continuing_run_counts_its_earlier_successes_in_the_counts_of_the_run_and_of_a_group()
    {
        using var temporary = new TemporaryDirectory();
        var fail = true;
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1");
        var done = shard.Group("tenant-a");
        var doneLoad = done.Task("load", _ => Task.CompletedTask);
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

        var start = reporter.Snapshots[0];
        start.Counts.EarlierSuccesses.ShouldBe(2);
        start.Counts[NodeStatus.Succeeded].ShouldBe(2);
        start.Counts.Unsucceeded.ShouldBe(1);
        start[done.Path].Counts.EarlierSuccesses.ShouldBe(1);
        start[mixed.Path].Counts.EarlierSuccesses.ShouldBe(1);
        start[doneLoad.Path].Counts.EarlierSuccesses.ShouldBe(1);
        start[doneLoad.Path].Counts[NodeStatus.Succeeded].ShouldBe(1);
        var settled = reporter.Events.OfType<GroupSettled>().ToList();
        settled.Select(group => (group.Path, group.Counts.EarlierSuccesses, group.Counts[NodeStatus.Succeeded])).ShouldBe(
        [
            (mixed.Path, 1, 2),
            (shard.Path, 2, 3),
        ]);
        second.Snapshot.Counts.EarlierSuccesses.ShouldBe(2);
        second.Snapshot.Counts.Unsucceeded.ShouldBe(0);
        second.Snapshot[shard.Path].Counts.EarlierSuccesses.ShouldBe(2);
    }

    [Test]
    public async Task A_continuing_run_counts_the_warnings_of_its_earlier_successes_in_every_group_above_them_from_its_start()
    {
        using var temporary = new TemporaryDirectory();
        var fail = true;
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1");
        var tenant = shard.Group("tenant-a");
        tenant.Task("load", context =>
        {
            for (var entity = 1; entity <= 101; entity++)
            {
                context.AddWarning($"entity {entity} has no region");
            }

            return Task.CompletedTask;
        });
        var verify = tenant.Task("verify", context =>
        {
            context.AddWarning("entity 7 has no owner");
            return fail ? throw context.Fail("bad data") : Task.CompletedTask;
        });
        var graph = builder.Build();

        var first = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path) });
        fail = false;
        var reporter = new RecordingReporter();
        var second = await Runner.RunAsync(
            graph,
            new RunOptions { Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id), Reporters = [reporter] });

        var start = reporter.Snapshots[0];
        start[tenant.Path].WarningCount.ShouldBe(101);
        start[shard.Path].WarningCount.ShouldBe(101);
        start[verify.Path].WarningCount.ShouldBe(0);
        second.Snapshot[tenant.Path].WarningCount.ShouldBe(102);
        second.Snapshot[shard.Path].WarningCount.ShouldBe(102);
    }

}
