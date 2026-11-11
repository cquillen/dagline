using Dagline.TestSupport;

namespace Dagline.Tests;

public class GraphTests
{
    [Test]
    public async Task Declaration_order_is_depth_first_with_the_children_of_each_group_in_the_order_added_to_it()
    {
        var builder = new GraphBuilder();
        var a = builder.Group("a");
        a.Task("x", _ => Task.CompletedTask);
        builder.Task("b", _ => Task.CompletedTask);
        a.Task("y", _ => Task.CompletedTask);
        var graph = builder.Build();
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        graph.Select(node => node.Path.ToString()).ShouldBe(["a", "a/x", "a/y", "b"]);
        result.Snapshot.Nodes.Select(node => node.Path.ToString()).ShouldBe(["a", "a/x", "a/y", "b"]);
        reporter.Lines.Take(7).ShouldBe(
        [
            "run started",
            "a/x: pending -> ready",
            "a/y: pending -> ready",
            "b: pending -> ready",
            "a/x: ready -> running",
            "a/y: ready -> running",
            "b: ready -> running",
        ]);
    }

    [Test]
    public void A_graph_lists_the_handles_of_its_nodes_in_declaration_order()
    {
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1");
        var publish = builder.Task("publish", _ => Task.CompletedTask);
        var load = shard.Task("load", _ => Task.CompletedTask);

        var graph = builder.Build();

        graph.Count.ShouldBe(3);
        graph[0].ShouldBeSameAs(shard);
        graph[1].ShouldBeSameAs(load);
        graph[2].ShouldBeSameAs(publish);
        graph.ToList().ShouldBe([shard, load, publish]);
    }

    [Test]
    public void A_graph_with_no_nodes_is_an_empty_list()
    {
        var graph = new GraphBuilder().Build();

        graph.Count.ShouldBe(0);
        graph.ShouldBeEmpty();
    }

    [Test]
    public void A_node_is_found_in_a_graph_by_its_path()
    {
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1");
        var load = shard.Task("load", _ => Task.CompletedTask);

        var graph = builder.Build();

        graph.TryGetNode(NodePath.Parse("shard-1"), out var group).ShouldBeTrue();
        group.ShouldBeSameAs(shard);
        graph.TryGetNode(NodePath.Parse("shard-1/load"), out var task).ShouldBeTrue();
        task.ShouldBeSameAs(load);
    }

    [Test]
    public void A_path_that_is_not_in_the_graph_is_not_found()
    {
        var builder = new GraphBuilder();
        builder.Group("shard-1").Task("load", _ => Task.CompletedTask);

        var graph = builder.Build();

        graph.TryGetNode(NodePath.Parse("load"), out var node).ShouldBeFalse();
        node.ShouldBeNull();
        graph.TryGetNode(default, out _).ShouldBeFalse();
    }
}
