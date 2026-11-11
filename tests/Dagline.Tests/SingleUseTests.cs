namespace Dagline.Tests;

public class SingleUseTests
{
    [Test]
    public void After_Build_the_builder_rejects_every_call_that_would_change_the_graph()
    {
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask);
        var graph = builder.Build();

        Should.Throw<InvalidOperationException>(() => builder.Task("verify", _ => Task.CompletedTask));
        Should.Throw<InvalidOperationException>(() => builder.Group("shard-1"));

        graph.Select(node => node.Path.ToString()).ShouldBe(["load"]);
    }

    [Test]
    public void After_Build_a_task_handle_rejects_every_call_that_would_change_the_graph()
    {
        var builder = new GraphBuilder();
        var load = builder.Task("load", _ => Task.CompletedTask);
        var verify = builder.Task("verify", _ => Task.CompletedTask);
        builder.Build();

        Should.Throw<InvalidOperationException>(() => verify.Title("Verify entities"));
        Should.Throw<InvalidOperationException>(() => verify.Requires(load));
        Should.Throw<InvalidOperationException>(() => verify.Requires([load]));
        Should.Throw<InvalidOperationException>(() => verify.After(load));
        Should.Throw<InvalidOperationException>(() => verify.After([load]));
    }

    [Test]
    public void After_Build_a_group_handle_rejects_every_call_that_would_change_the_graph()
    {
        var builder = new GraphBuilder();
        var load = builder.Task("load", _ => Task.CompletedTask);
        var shard = builder.Group("shard-1");
        var tenant = shard.Group("tenant-a");
        builder.Build();

        Should.Throw<InvalidOperationException>(() => shard.Task("verify", _ => Task.CompletedTask));
        Should.Throw<InvalidOperationException>(() => tenant.Group("region-1"));
        Should.Throw<InvalidOperationException>(() => tenant.Title("Tenant A"));
        Should.Throw<InvalidOperationException>(() => shard.Requires(load));
        Should.Throw<InvalidOperationException>(() => shard.Requires([load]));
        Should.Throw<InvalidOperationException>(() => tenant.After(load));
        Should.Throw<InvalidOperationException>(() => tenant.After([load]));
    }

    [Test]
    public async Task A_call_rejected_after_Build_leaves_the_graph_as_it_was_built()
    {
        var builder = new GraphBuilder();
        var load = builder.Task("load", _ => Task.CompletedTask);
        var shard = builder.Group("shard-1");
        var verify = shard.Task("verify", _ => Task.CompletedTask);
        var graph = builder.Build();

        Should.Throw<InvalidOperationException>(() => verify.Title("Verify entities").Requires(load));
        Should.Throw<InvalidOperationException>(() => shard.After(load));
        Should.Throw<InvalidOperationException>(() => shard.Task("publish", _ => Task.CompletedTask));
        var result = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.None });

        result.Snapshot.Nodes.Select(node => node.Path).ShouldBe([load.Path, shard.Path, verify.Path]);
        result.Snapshot[verify.Path].Title.ShouldBeNull();
        result.Snapshot[verify.Path].Dependencies.ShouldBeEmpty();
        result.Snapshot[shard.Path].Dependencies.ShouldBeEmpty();
    }

    [Test]
    public void A_builder_builds_one_graph()
    {
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask);
        builder.Build();

        Should.Throw<InvalidOperationException>(() => builder.Build());
    }

    [Test]
    public void A_null_argument_is_still_the_error_after_Build()
    {
        var builder = new GraphBuilder();
        var load = builder.Task("load", _ => Task.CompletedTask);
        var shard = builder.Group("shard-1");
        builder.Build();

        Should.Throw<ArgumentNullException>(() => builder.Task(null!, _ => Task.CompletedTask)).ParamName.ShouldBe("segment");
        Should.Throw<ArgumentNullException>(() => shard.Group(null!)).ParamName.ShouldBe("segment");
        Should.Throw<ArgumentNullException>(() => load.Title(null!)).ParamName.ShouldBe("title");
        Should.Throw<ArgumentNullException>(() => load.Requires((Node)null!)).ParamName.ShouldBe("upstream");
        Should.Throw<ArgumentException>(() => shard.After([load, null!])).ParamName.ShouldBe("upstreams");
    }

    [Test]
    public void A_builder_whose_graph_had_problems_was_not_built_and_reports_them_again()
    {
        var builder = new GraphBuilder();
        var load = builder.Task("load", _ => Task.CompletedTask);
        load.Requires(load);

        Should.Throw<GraphException>(() => builder.Build());
        builder.Task("verify", _ => Task.CompletedTask).Requires(load);

        Should.Throw<GraphException>(() => builder.Build()).Problems.ShouldHaveSingleItem().Message.ShouldBe("'load' requires itself.");
    }
}
