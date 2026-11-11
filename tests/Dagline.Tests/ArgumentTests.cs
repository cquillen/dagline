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

    [Test]
    public void An_event_rejects_null_when_it_is_made()
    {
        var at = DateTimeOffset.UnixEpoch;
        var path = NodePath.Parse("shard-1");

        Should.Throw<ArgumentNullException>(() => new RunStarted(at, null!)).ParamName.ShouldBe("Source");
        Should.Throw<ArgumentNullException>(() => new TaskStatusChanged(at, null!, NodeStatus.Pending, NodeStatus.Ready))
            .ParamName.ShouldBe("Node");
        Should.Throw<ArgumentNullException>(() => new WarningAdded(at, path, null!)).ParamName.ShouldBe("Warning");
        Should.Throw<ArgumentNullException>(() => new GroupSettled(at, path, NodeStatus.Succeeded, null!)).ParamName.ShouldBe("Counts");
    }

    [Test]
    public async Task An_event_rejects_null_when_it_is_copied_with_a_change()
    {
        var builder = new GraphBuilder();
        builder.Group("shard-1").Task("prepare", context =>
        {
            context.Warn("entity 9 has no region");
            return Task.CompletedTask;
        });
        var reporter = new RecordingReporter();
        await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        var started = reporter.Events.OfType<RunStarted>().Single();
        var changed = reporter.Events.OfType<TaskStatusChanged>().First();
        var warned = reporter.Events.OfType<WarningAdded>().Single();
        var settled = reporter.Events.OfType<GroupSettled>().Single();

        Should.Throw<ArgumentNullException>(() => started with { Source = null! });
        Should.Throw<ArgumentNullException>(() => changed with { Node = null! });
        Should.Throw<ArgumentNullException>(() => warned with { Warning = null! });
        Should.Throw<ArgumentNullException>(() => settled with { Counts = null! });
    }

    [Test]
    public void A_reporter_error_rejects_a_null_reporter_and_a_null_exception()
    {
        var reporter = new RecordingReporter();
        var exception = new InvalidOperationException("the writer is closed");
        var error = new ReporterError(reporter, Event: null, exception);

        Should.Throw<ArgumentNullException>(() => new ReporterError(null!, Event: null, exception)).ParamName.ShouldBe("Reporter");
        Should.Throw<ArgumentNullException>(() => new ReporterError(reporter, Event: null, null!)).ParamName.ShouldBe("Exception");
        Should.Throw<ArgumentNullException>(() => error with { Reporter = null! });
        Should.Throw<ArgumentNullException>(() => error with { Exception = null! });
        error.Event.ShouldBeNull();
    }

    [Test]
    public void What_a_failure_blocked_rejects_a_null_list()
    {
        var cause = NodePath.Parse("prepare");
        var blocked = new BlockedTasks(cause, [], []);

        Should.Throw<ArgumentNullException>(() => new BlockedTasks(cause, null!, [])).ParamName.ShouldBe("Tasks");
        Should.Throw<ArgumentNullException>(() => new BlockedTasks(cause, [], null!)).ParamName.ShouldBe("Roots");
        Should.Throw<ArgumentNullException>(() => blocked with { Tasks = null! });
        Should.Throw<ArgumentNullException>(() => blocked with { Roots = null! });
    }

    [Test]
    public void A_graph_problem_rejects_a_null_message()
    {
        var problem = new GraphProblem(Path: null, "'shard 1' is not a valid segment");

        Should.Throw<ArgumentNullException>(() => new GraphProblem(Path: null, null!)).ParamName.ShouldBe("Message");
        Should.Throw<ArgumentNullException>(() => problem with { Message = null! });
    }
}
