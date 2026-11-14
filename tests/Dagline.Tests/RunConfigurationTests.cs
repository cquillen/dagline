using Dagline.TestSupport;

namespace Dagline.Tests;

public class RunConfigurationTests
{
    [Test]
    public void A_limit_the_graph_names_with_no_size_is_refused_before_anything_happens()
    {
        var bodyRan = false;
        var builder = new GraphBuilder();
        builder.Task("load", _ => RecordBodyRan()).Limit("database");
        builder.Group("shard-1").Limit("network");
        var graph = builder.Build();
        var reporter = new RecordingReporter();
        var options = new RunOptions
        {
            Journal = Journal.None,
            Limits = new Dictionary<string, int> { ["files"] = 2 },
            Reporters = [reporter],
        };

        var exception = Should.Throw<RunConfigurationException>(() => { _ = Runner.RunAsync(graph, options); });

        exception.ShouldBeAssignableTo<DaglineException>();
        Lines(exception).ShouldBe(
        [
            "The options cannot be used to run the graph:",
            "- The graph names the limit 'database', and the options give it no size.",
            "- The graph names the limit 'network', and the options give it no size.",
        ]);
        reporter.Events.ShouldBeEmpty();
        bodyRan.ShouldBeFalse();

        Task RecordBodyRan()
        {
            bodyRan = true;
            return Task.CompletedTask;
        }
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public void A_size_below_one_is_refused_before_anything_happens(int size)
    {
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask).Limit("database");
        var graph = builder.Build();
        var reporter = new RecordingReporter();
        var options = new RunOptions
        {
            Journal = Journal.None,
            Limits = new Dictionary<string, int> { ["database"] = size },
            Reporters = [reporter],
        };

        var exception = Should.Throw<RunConfigurationException>(() => { _ = Runner.RunAsync(graph, options); });

        Lines(exception).ShouldBe(
        [
            "The options cannot be used to run the graph:",
            $"- The size of the limit 'database' is {size}. It must be at least 1.",
        ]);
        reporter.Events.ShouldBeEmpty();
    }

    [Test]
    [Arguments(0)]
    [Arguments(-3)]
    public void A_max_running_tasks_below_one_is_refused_before_anything_happens(int maxRunningTasks)
    {
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask);
        var graph = builder.Build();
        var reporter = new RecordingReporter();
        var options = new RunOptions { Journal = Journal.None, MaxRunningTasks = maxRunningTasks, Reporters = [reporter] };

        var exception = Should.Throw<RunConfigurationException>(() => { _ = Runner.RunAsync(graph, options); });

        Lines(exception).ShouldBe(
        [
            "The options cannot be used to run the graph:",
            $"- MaxRunningTasks is {maxRunningTasks}. It must be at least 1.",
        ]);
        reporter.Events.ShouldBeEmpty();
    }

    [Test]
    public void The_message_lists_every_problem()
    {
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask).Limit("database").Limit("files");
        builder.Task("publish", _ => Task.CompletedTask).Limit("network");
        var graph = builder.Build();
        var options = new RunOptions
        {
            Journal = Journal.None,
            MaxRunningTasks = 0,
            Limits = new Dictionary<string, int> { ["files"] = 0 },
        };

        var exception = Should.Throw<RunConfigurationException>(() => { _ = Runner.RunAsync(graph, options); });

        Lines(exception).ShouldBe(
        [
            "The options cannot be used to run the graph:",
            "- MaxRunningTasks is 0. It must be at least 1.",
            "- The size of the limit 'files' is 0. It must be at least 1.",
            "- The graph names the limit 'database', and the options give it no size.",
            "- The graph names the limit 'network', and the options give it no size.",
        ]);
    }

    [Test]
    public async Task A_size_given_for_a_limit_the_graph_does_not_name_is_ignored()
    {
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask).Limit("database");

        var result = await Runner.RunAsync(
            builder.Build(),
            new RunOptions
            {
                Journal = Journal.None,
                Limits = new Dictionary<string, int> { ["database"] = 1, ["network"] = 4 },
            });

        result.Outcome.ShouldBe(RunOutcome.Complete);
    }

    [Test]
    public void A_size_below_one_is_refused_even_for_a_limit_the_graph_does_not_name()
    {
        var graph = new GraphBuilder().Build();
        var options = new RunOptions { Journal = Journal.None, Limits = new Dictionary<string, int> { ["network"] = 0 } };

        var exception = Should.Throw<RunConfigurationException>(() => { _ = Runner.RunAsync(graph, options); });

        exception.Message.ShouldContain("'network'");
    }

    [Test]
    public void Limit_names_are_compared_ordinally_whatever_comparer_the_sizes_were_given_with()
    {
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask).Limit("Database");
        var graph = builder.Build();
        var options = new RunOptions
        {
            Journal = Journal.None,
            Limits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["database"] = 1 },
        };

        var exception = Should.Throw<RunConfigurationException>(() => { _ = Runner.RunAsync(graph, options); });

        exception.Message.ShouldContain("'Database'");
    }

    private static string[] Lines(Exception exception) => exception.Message.Split(Environment.NewLine);
}
