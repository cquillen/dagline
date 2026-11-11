using Dagline.TestSupport;

namespace Dagline.Tests;

public class SnapshotTests
{
    [Test]
    public async Task The_final_snapshot_lists_every_node_in_declaration_order_with_its_status_and_declared_dependencies()
    {
        var builder = new GraphBuilder();
        var prepare = builder.Task("prepare", _ => Task.CompletedTask);
        var process = builder.Task("process", _ => Task.CompletedTask).Requires(prepare);
        builder.Task("publish", _ => Task.CompletedTask).Requires(prepare).Requires(process);

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });

        var nodes = result.Snapshot.Nodes;
        nodes.Select(node => node.Path.ToString()).ShouldBe(["prepare", "process", "publish"]);
        nodes.Select(node => node.Status).ShouldBe([NodeStatus.Succeeded, NodeStatus.Succeeded, NodeStatus.Succeeded]);
        nodes[0].Dependencies.ShouldBeEmpty();
        nodes[1].Dependencies.ShouldBe([new Dependency(prepare.Path, DependencyKind.Requires)]);
        nodes[2].Dependencies.ShouldBe(
        [
            new Dependency(prepare.Path, DependencyKind.Requires),
            new Dependency(process.Path, DependencyKind.Requires),
        ]);
    }

    [Test]
    public async Task A_reporter_can_read_the_run_through_the_snapshot_source_while_tasks_are_running()
    {
        var prepare = new PausedBody();
        var builder = new GraphBuilder();
        var prepareTask = builder.Task("prepare", prepare.Body);
        builder.Task("publish", _ => Task.CompletedTask).Requires(prepareTask);
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        var source = await reporter.Source;
        await prepare.Started;
        var midRun = source.Current;
        prepare.Release();
        await run;

        midRun.Nodes.Select(node => node.Status).ShouldBe([NodeStatus.Running, NodeStatus.Pending]);
        midRun.Ended.ShouldBeNull();
        midRun.Outcome.ShouldBeNull();
        midRun.Counts.ShouldBe(new Dictionary<NodeStatus, int>
        {
            [NodeStatus.Pending] = 1,
            [NodeStatus.Ready] = 0,
            [NodeStatus.Running] = 1,
            [NodeStatus.Succeeded] = 0,
            [NodeStatus.Failed] = 0,
            [NodeStatus.Blocked] = 0,
            [NodeStatus.Cancelled] = 0,
        });
    }

    [Test]
    public async Task Counts_gives_every_status_once_the_run_has_ended()
    {
        var builder = new GraphBuilder();
        var prepare = builder.Task("prepare", _ => Task.CompletedTask);
        builder.Task("publish", _ => Task.CompletedTask).Requires(prepare);

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });

        result.Snapshot.Counts.ShouldBe(new Dictionary<NodeStatus, int>
        {
            [NodeStatus.Pending] = 0,
            [NodeStatus.Ready] = 0,
            [NodeStatus.Running] = 0,
            [NodeStatus.Succeeded] = 2,
            [NodeStatus.Failed] = 0,
            [NodeStatus.Blocked] = 0,
            [NodeStatus.Cancelled] = 0,
        });
    }

    [Test]
    public async Task The_result_snapshot_the_one_handed_with_the_last_event_and_the_source_agree_after_the_run()
    {
        var builder = new GraphBuilder();
        builder.Task("prepare", _ => Task.CompletedTask);
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        var source = await reporter.Source;
        reporter.Events[^1].ShouldBeOfType<RunEnded>();
        reporter.Snapshots[^1].ShouldBeSameAs(result.Snapshot);
        source.Current.ShouldBeSameAs(result.Snapshot);
        result.Snapshot.Outcome.ShouldBe(RunOutcome.Complete);
        result.Snapshot.Outcome.ShouldBe(result.Outcome);
    }

    [Test]
    public async Task A_node_is_found_in_a_snapshot_by_its_path()
    {
        var builder = new GraphBuilder();
        builder.Task("prepare", _ => Task.CompletedTask);
        builder.Task("publish", _ => Task.CompletedTask);

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });

        var publish = NodePath.Parse("publish");
        result.Snapshot[publish].ShouldBeSameAs(result.Snapshot.Nodes[1]);
        result.Snapshot.TryGetNode(publish, out var found).ShouldBeTrue();
        found.ShouldBeSameAs(result.Snapshot.Nodes[1]);
    }

    [Test]
    public async Task A_path_that_is_not_in_the_graph_is_not_in_the_snapshot()
    {
        var builder = new GraphBuilder();
        builder.Task("prepare", _ => Task.CompletedTask);

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });

        var missing = NodePath.Parse("publish");
        Should.Throw<KeyNotFoundException>(() => result.Snapshot[missing]).Message.ShouldContain("'publish'");
        result.Snapshot.TryGetNode(missing, out _).ShouldBeFalse();
    }
}
