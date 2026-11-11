using Dagline.TestSupport;

namespace Dagline.Tests;

public class ArgumentTests
{
    [Test]
    public void RunAsync_rejects_a_null_graph_or_null_options_at_the_call()
    {
        var graph = new GraphBuilder().Build();
        var options = new RunOptions { Journal = Journal.None };

        Should.Throw<ArgumentNullException>(() => { _ = Runner.RunAsync(null!, options); }).ParamName.ShouldBe("graph");
        Should.Throw<ArgumentNullException>(() => { _ = Runner.RunAsync(graph, null!); }).ParamName.ShouldBe("options");
    }

    [Test]
    public void RunAsync_rejects_a_null_reporter_at_the_call()
    {
        var graph = new GraphBuilder().Build();
        var options = new RunOptions { Journal = Journal.None, Reporters = [new RecordingReporter(), null!] };

        Should.Throw<ArgumentException>(() => { _ = Runner.RunAsync(graph, options); }).ParamName.ShouldBe("options");
    }

    [Test]
    public void RunOptions_rejects_null_for_every_property()
    {
        Should.Throw<ArgumentNullException>(() => new RunOptions { Journal = null! });
        Should.Throw<ArgumentNullException>(() => new RunOptions { Journal = Journal.None, TimeProvider = null! });
        Should.Throw<ArgumentNullException>(() => new RunOptions { Journal = Journal.None, Reporters = null! });
        Should.Throw<ArgumentNullException>(() => new RunOptions { Journal = Journal.None, Limits = null! });
    }

    [Test]
    public void RunOptions_defaults_to_the_system_clock_and_no_reporters()
    {
        var options = new RunOptions { Journal = Journal.None };

        options.TimeProvider.ShouldBeSameAs(TimeProvider.System);
        options.Reporters.ShouldBeEmpty();
    }

    [Test]
    public void RunOptions_defaults_to_a_cap_of_sixteen_and_no_sizes()
    {
        var options = new RunOptions { Journal = Journal.None };

        options.Cap.ShouldBe(16);
        options.Limits.ShouldBeEmpty();
    }

    [Test]
    public void A_limit_name_that_is_null_or_empty_is_rejected_on_a_task_and_on_a_group()
    {
        var builder = new GraphBuilder();
        var task = builder.Task("prepare", _ => Task.CompletedTask);
        var group = builder.Group("shard-1");

        Should.Throw<ArgumentNullException>(() => task.Limit(null!)).ParamName.ShouldBe("name");
        Should.Throw<ArgumentException>(() => task.Limit(string.Empty)).ParamName.ShouldBe("name");
        Should.Throw<ArgumentNullException>(() => group.Limit(null!)).ParamName.ShouldBe("name");
        Should.Throw<ArgumentException>(() => group.Limit(string.Empty)).ParamName.ShouldBe("name");
    }

    [Test]
    public void The_builder_and_its_handles_reject_null()
    {
        var builder = new GraphBuilder();
        var task = builder.Task("prepare", _ => Task.CompletedTask);

        Should.Throw<ArgumentNullException>(() => builder.Task(null!, _ => Task.CompletedTask)).ParamName.ShouldBe("segment");
        Should.Throw<ArgumentNullException>(() => builder.Task("publish", null!)).ParamName.ShouldBe("body");
        Should.Throw<ArgumentNullException>(() => task.Requires((Node)null!)).ParamName.ShouldBe("upstream");
    }

    [Test]
    public void Adding_a_node_to_a_group_rejects_null()
    {
        var group = new GraphBuilder().Group("shard-1");

        Should.Throw<ArgumentNullException>(() => new GraphBuilder().Group(null!)).ParamName.ShouldBe("segment");
        Should.Throw<ArgumentNullException>(() => group.Group(null!)).ParamName.ShouldBe("segment");
        Should.Throw<ArgumentNullException>(() => group.Task(null!, _ => Task.CompletedTask)).ParamName.ShouldBe("segment");
        Should.Throw<ArgumentNullException>(() => group.Task("load", null!)).ParamName.ShouldBe("body");
    }

    [Test]
    public void A_title_and_a_dependency_reject_null_on_a_task_and_on_a_group()
    {
        var builder = new GraphBuilder();
        var task = builder.Task("prepare", _ => Task.CompletedTask);
        var group = builder.Group("shard-1");

        Should.Throw<ArgumentNullException>(() => task.Title(null!)).ParamName.ShouldBe("title");
        Should.Throw<ArgumentNullException>(() => task.After((Node)null!)).ParamName.ShouldBe("upstream");
        Should.Throw<ArgumentNullException>(() => task.Requires((IEnumerable<Node>)null!)).ParamName.ShouldBe("upstreams");
        Should.Throw<ArgumentNullException>(() => task.After((IEnumerable<Node>)null!)).ParamName.ShouldBe("upstreams");
        Should.Throw<ArgumentNullException>(() => group.Title(null!)).ParamName.ShouldBe("title");
        Should.Throw<ArgumentNullException>(() => group.Requires((Node)null!)).ParamName.ShouldBe("upstream");
        Should.Throw<ArgumentNullException>(() => group.After((Node)null!)).ParamName.ShouldBe("upstream");
        Should.Throw<ArgumentNullException>(() => group.Requires((IEnumerable<Node>)null!)).ParamName.ShouldBe("upstreams");
        Should.Throw<ArgumentNullException>(() => group.After((IEnumerable<Node>)null!)).ParamName.ShouldBe("upstreams");
    }

    [Test]
    public async Task A_collection_with_a_null_handle_is_rejected_and_declares_nothing()
    {
        var builder = new GraphBuilder();
        var prepare = builder.Task("prepare", _ => Task.CompletedTask);
        var publish = builder.Task("publish", _ => Task.CompletedTask);
        var group = builder.Group("shard-1");

        Should.Throw<ArgumentException>(() => publish.Requires([prepare, null!])).ParamName.ShouldBe("upstreams");
        Should.Throw<ArgumentException>(() => publish.After([prepare, null!])).ParamName.ShouldBe("upstreams");
        Should.Throw<ArgumentException>(() => group.Requires([prepare, null!])).ParamName.ShouldBe("upstreams");
        Should.Throw<ArgumentException>(() => group.After([prepare, null!])).ParamName.ShouldBe("upstreams");

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });
        result.Snapshot[publish.Path].Dependencies.ShouldBeEmpty();
        result.Snapshot[group.Path].Dependencies.ShouldBeEmpty();
    }

    [Test]
    public async Task A_reporter_added_to_the_list_after_the_run_has_started_receives_nothing()
    {
        var prepare = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("prepare", prepare.Body);
        var late = new RecordingReporter();
        var reporters = new List<IReporter> { new RecordingReporter() };

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = reporters });
        await prepare.Started;
        reporters.Add(late);
        prepare.Release();
        await run;

        late.Events.ShouldBeEmpty();
    }
}
