namespace Dagline.Tests;

public class StatusOfTests
{
    [Test]
    public async Task A_task_reads_the_status_of_an_upstream_it_declared_one_a_group_that_holds_it_declared_and_any_node_beneath_an_upstream_group()
    {
        Dictionary<string, NodeStatus> seen = [];
        var builder = new GraphBuilder();
        var checks = builder.Group("checks");
        var verify = checks.Task("verify", context => throw context.Fail("2 bad entities"));
        var tenant = checks.Group("tenant-a");
        var scan = tenant.Task("scan", _ => Task.CompletedTask);
        var load = builder.Task("load", _ => Task.CompletedTask);
        var publish = builder.Task("publish", _ => Task.CompletedTask).Requires(verify);
        var archive = builder.Group("archive");
        archive.Task("store", _ => Task.CompletedTask);
        var audit = builder.Group("audit").After(checks);
        audit.Task("summary", Summarise).Requires(load).After(publish).After(archive);

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });

        result.Snapshot[NodePath.Parse("audit/summary")].Status.ShouldBe(NodeStatus.Succeeded);
        seen.ShouldBe(
            new Dictionary<string, NodeStatus>
            {
                ["a task it requires"] = NodeStatus.Succeeded,
                ["a task it runs after"] = NodeStatus.Blocked,
                ["a group it runs after"] = NodeStatus.Succeeded,
                ["a group its own group runs after"] = NodeStatus.Failed,
                ["a task beneath that group"] = NodeStatus.Failed,
                ["a group beneath that group"] = NodeStatus.Succeeded,
                ["a task two levels beneath that group"] = NodeStatus.Succeeded,
            },
            ignoreOrder: true);

        Task Summarise(TaskContext context)
        {
            seen["a task it requires"] = context.StatusOf(load);
            seen["a task it runs after"] = context.StatusOf(publish);
            seen["a group it runs after"] = context.StatusOf(archive);
            seen["a group its own group runs after"] = context.StatusOf(checks);
            seen["a task beneath that group"] = context.StatusOf(verify);
            seen["a group beneath that group"] = context.StatusOf(tenant);
            seen["a task two levels beneath that group"] = context.StatusOf(scan);
            return Task.CompletedTask;
        }
    }

    [Test]
    public async Task The_status_of_a_node_the_task_does_not_depend_on_is_refused()
    {
        var otherBuilder = new GraphBuilder();
        var loadOfAnotherGraph = otherBuilder.Task("load", _ => Task.CompletedTask);
        otherBuilder.Build();

        var builder = new GraphBuilder();
        var load = builder.Task("load", _ => Task.CompletedTask);
        var unrelated = builder.Task("count", _ => Task.CompletedTask);
        var audit = builder.Group("audit");
        var sibling = audit.Task("collect", _ => Task.CompletedTask);
        TaskNode? summary = null;
        summary = audit.Task("summary", context =>
        {
            context.StatusOf(load).ShouldBe(NodeStatus.Succeeded);
            Should.Throw<ArgumentNullException>(() => context.StatusOf(null!)).ParamName.ShouldBe("dependency");
            Should.Throw<ArgumentException>(() => context.StatusOf(unrelated)).ParamName.ShouldBe("dependency");
            Should.Throw<ArgumentException>(() => context.StatusOf(sibling)).ParamName.ShouldBe("dependency");
            Should.Throw<ArgumentException>(() => context.StatusOf(audit)).ParamName.ShouldBe("dependency");
            Should.Throw<ArgumentException>(() => context.StatusOf(summary!)).ParamName.ShouldBe("dependency");
            Should.Throw<ArgumentException>(() => context.StatusOf(loadOfAnotherGraph)).ParamName.ShouldBe("dependency");
            return Task.CompletedTask;
        }).Requires(load);

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });

        // An assertion that fails inside the body fails the task, with the assertion's text as its message.
        result.Snapshot[summary.Path].Message.ShouldBeNull();
        result.Snapshot[summary.Path].Status.ShouldBe(NodeStatus.Succeeded);
    }
}
