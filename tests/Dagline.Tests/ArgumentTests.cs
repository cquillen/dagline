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
    }

    [Test]
    public void RunOptions_defaults_to_the_system_clock_and_no_reporters()
    {
        var options = new RunOptions { Journal = Journal.None };

        options.TimeProvider.ShouldBeSameAs(TimeProvider.System);
        options.Reporters.ShouldBeEmpty();
    }

    [Test]
    public void The_builder_and_its_handles_reject_null()
    {
        var builder = new GraphBuilder();
        var task = builder.Task("prepare", _ => Task.CompletedTask);

        Should.Throw<ArgumentNullException>(() => builder.Task(null!, _ => Task.CompletedTask)).ParamName.ShouldBe("segment");
        Should.Throw<ArgumentNullException>(() => builder.Task("publish", null!)).ParamName.ShouldBe("body");
        Should.Throw<ArgumentNullException>(() => task.Requires(null!)).ParamName.ShouldBe("upstream");
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
