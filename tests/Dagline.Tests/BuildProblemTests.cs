namespace Dagline.Tests;

public class BuildProblemTests
{
    private const string _segmentRule = "a segment is one or more of A-Z a-z 0-9 . _ -";

    [Test]
    public void A_node_with_an_invalid_segment_still_gets_a_handle_and_Build_reports_it()
    {
        var builder = new GraphBuilder();

        var task = builder.Task("shard 1", _ => Task.CompletedTask);

        task.ShouldNotBeNull();
        ProblemsOf(builder).ShouldBe(
        [
            new GraphProblem(null, $"'shard 1' is not a valid segment for a node at the root: {_segmentRule}"),
        ]);
    }

    [Test]
    [Arguments("")]
    [Arguments("load/entities")]
    [Arguments("load entities")]
    [Arguments("loäd")]
    public void An_invalid_segment_is_reported_for_a_task_and_for_a_group_and_names_the_group_that_holds_the_node(string segment)
    {
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1");

        shard.Task(segment, _ => Task.CompletedTask).ShouldNotBeNull();
        shard.Group("tenant-a").Group(segment).ShouldNotBeNull();

        ProblemsOf(builder).ShouldBe(
        [
            new GraphProblem(null, $"'{segment}' is not a valid segment for a node in 'shard-1': {_segmentRule}"),
            new GraphProblem(null, $"'{segment}' is not a valid segment for a node in 'shard-1/tenant-a': {_segmentRule}"),
        ]);
    }

    [Test]
    public void A_node_beneath_a_group_with_an_invalid_segment_is_not_a_problem_of_its_own()
    {
        var builder = new GraphBuilder();

        builder.Group("shard 1").Group("tenant-a").Task("load", _ => Task.CompletedTask);

        ProblemsOf(builder).ShouldHaveSingleItem().Message.ShouldStartWith("'shard 1' is not a valid segment");
    }

    [Test]
    public void Two_tasks_added_with_one_segment_under_one_parent_are_a_duplicate_path()
    {
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1");

        shard.Task("load", _ => Task.CompletedTask);
        shard.Task("load", _ => Task.CompletedTask).ShouldNotBeNull();

        ProblemsOf(builder).ShouldBe(
        [
            new GraphProblem(NodePath.Parse("shard-1/load"), "'shard-1/load' was added 2 times: a path is one node."),
        ]);
    }

    [Test]
    public void A_task_and_a_group_on_one_path_are_a_duplicate_path()
    {
        var builder = new GraphBuilder();

        builder.Task("load", _ => Task.CompletedTask);
        builder.Group("load").ShouldNotBeNull();

        ProblemsOf(builder).ShouldBe(
        [
            new GraphProblem(NodePath.Parse("load"), "'load' was added 2 times: a path is one node."),
        ]);
    }

    [Test]
    public void A_group_added_again_with_the_same_segment_is_a_duplicate_path_and_not_the_same_group_reopened()
    {
        var builder = new GraphBuilder();

        builder.Group("shard-1").Task("load", _ => Task.CompletedTask);
        builder.Group("shard-1").Task("verify", _ => Task.CompletedTask);
        builder.Group("shard-1").Task("verify", _ => Task.CompletedTask);

        ProblemsOf(builder).ShouldBe(
        [
            new GraphProblem(
                NodePath.Parse("shard-1"),
                "'shard-1' was added 3 times: a path is one node. Adding a group again does not reopen it: keep its handle to add more children."),
        ]);
    }

    [Test]
    public void A_problem_of_a_node_beneath_an_invalid_segment_has_no_path_and_an_invalid_segment_is_never_a_duplicate()
    {
        var builder = new GraphBuilder();
        var shard = builder.Group("shard 1");

        shard.Task("load", _ => Task.CompletedTask);
        shard.Task("load", _ => Task.CompletedTask);
        builder.Group("shard 1");

        ProblemsOf(builder).ShouldBe(
        [
            new GraphProblem(null, $"'shard 1' is not a valid segment for a node at the root: {_segmentRule}"),
            new GraphProblem(null, $"'shard 1' is not a valid segment for a node at the root: {_segmentRule}"),
            new GraphProblem(null, "'shard 1/load' was added 2 times: a path is one node."),
        ]);
    }

    [Test]
    public void Siblings_that_differ_only_by_case_are_reported_together()
    {
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1");

        shard.Task("load", _ => Task.CompletedTask);
        shard.Task("verify", _ => Task.CompletedTask);
        shard.Group("Load");
        shard.Task("LOAD", _ => Task.CompletedTask);

        ProblemsOf(builder).ShouldBe(
        [
            new GraphProblem(
                NodePath.Parse("shard-1/load"),
                "'shard-1/load', 'shard-1/Load' and 'shard-1/LOAD' differ only by case: siblings must differ by more than case."),
        ]);
    }

    [Test]
    public void Nodes_that_differ_only_by_case_under_different_parents_are_not_a_problem()
    {
        var builder = new GraphBuilder();

        builder.Group("shard-1").Task("load", _ => Task.CompletedTask);
        builder.Group("shard-2").Task("Load", _ => Task.CompletedTask);
        builder.Task("LOAD", _ => Task.CompletedTask);

        builder.Build().Select(node => node.Path.ToString()).ShouldBe(["shard-1", "shard-1/load", "shard-2", "shard-2/Load", "LOAD"]);
    }

    [Test]
    public void A_duplicate_path_beside_a_sibling_that_differs_by_case_is_two_problems()
    {
        var builder = new GraphBuilder();

        builder.Task("load", _ => Task.CompletedTask);
        builder.Task("load", _ => Task.CompletedTask);
        builder.Task("Load", _ => Task.CompletedTask);

        ProblemsOf(builder).ShouldBe(
        [
            new GraphProblem(NodePath.Parse("load"), "'load' was added 2 times: a path is one node."),
            new GraphProblem(NodePath.Parse("load"), "'load' and 'Load' differ only by case: siblings must differ by more than case."),
        ]);
    }

    [Test]
    public void A_dependency_on_a_handle_from_another_builder_is_reported()
    {
        var other = new GraphBuilder();
        var load = other.Task("load", _ => Task.CompletedTask);
        var shard = other.Group("shard-1");
        var builder = new GraphBuilder();

        builder.Task("load", _ => Task.CompletedTask);
        builder.Task("publish", _ => Task.CompletedTask).Requires(load);
        builder.Group("reports").After([shard]);

        ProblemsOf(builder).ShouldBe(
        [
            new GraphProblem(NodePath.Parse("publish"), "'publish' requires 'load', a handle from another builder."),
            new GraphProblem(NodePath.Parse("reports"), "'reports' runs after 'shard-1', a handle from another builder."),
        ]);
        other.Build().Count.ShouldBe(2);
    }

    [Test]
    public void A_node_that_depends_on_itself_is_reported_once_however_often_it_was_declared()
    {
        var builder = new GraphBuilder();
        var load = builder.Task("load", _ => Task.CompletedTask);
        var shard = builder.Group("shard-1");

        load.After(load).Requires(load);
        shard.After([shard, shard]);

        ProblemsOf(builder).ShouldBe(
        [
            new GraphProblem(NodePath.Parse("load"), "'load' requires itself."),
            new GraphProblem(NodePath.Parse("shard-1"), "'shard-1' runs after itself."),
        ]);
    }

    [Test]
    public void A_dependency_of_a_node_on_its_own_ancestor_is_reported()
    {
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1");
        var tenant = shard.Group("tenant-a");

        tenant.Task("load", _ => Task.CompletedTask).Requires(shard);
        tenant.After(shard);

        ProblemsOf(builder).ShouldBe(
        [
            new GraphProblem(NodePath.Parse("shard-1/tenant-a/load"), "'shard-1/tenant-a/load' requires 'shard-1', its own ancestor."),
            new GraphProblem(NodePath.Parse("shard-1/tenant-a"), "'shard-1/tenant-a' runs after 'shard-1', its own ancestor."),
        ]);
    }

    [Test]
    public void A_dependency_of_a_group_on_its_own_descendant_is_reported()
    {
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1");
        var tenant = shard.Group("tenant-a");
        var load = tenant.Task("load", _ => Task.CompletedTask);

        shard.Requires(load);
        shard.After(tenant);

        ProblemsOf(builder).ShouldBe(
        [
            new GraphProblem(NodePath.Parse("shard-1"), "'shard-1' requires 'shard-1/tenant-a/load', its own descendant."),
            new GraphProblem(NodePath.Parse("shard-1"), "'shard-1' runs after 'shard-1/tenant-a', its own descendant."),
        ]);
    }

    [Test]
    public void A_cycle_is_reported_with_every_node_of_it_named_and_both_dependency_kinds_count()
    {
        var builder = new GraphBuilder();
        var load = builder.Task("load", _ => Task.CompletedTask);
        var verify = builder.Task("verify", _ => Task.CompletedTask);
        var publish = builder.Task("publish", _ => Task.CompletedTask);
        builder.Task("report", _ => Task.CompletedTask).After(publish);

        publish.Requires(verify);
        verify.After(load);
        load.Requires(publish);

        ProblemsOf(builder).ShouldBe(
        [
            new GraphProblem(
                NodePath.Parse("publish"),
                "These dependencies form a cycle: 'publish' requires 'verify', 'verify' runs after 'load', 'load' requires 'publish'."),
        ]);
    }

    [Test]
    public void A_cycle_through_a_group_is_reported_because_the_group_stands_for_the_tasks_beneath_it()
    {
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1");
        var load = shard.Group("tenant-a").Task("load", _ => Task.CompletedTask);
        var publish = builder.Task("publish", _ => Task.CompletedTask);

        publish.Requires(shard);
        load.After(publish);

        ProblemsOf(builder).ShouldBe(
        [
            new GraphProblem(
                NodePath.Parse("publish"),
                "These dependencies form a cycle: 'publish' requires 'shard-1', 'shard-1/tenant-a/load' runs after 'publish'."),
        ]);
    }

    [Test]
    public void A_cycle_through_a_dependency_declared_on_a_group_is_reported_because_it_applies_to_every_task_beneath()
    {
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1");
        var load = shard.Group("tenant-a").Task("load", _ => Task.CompletedTask);
        var publish = builder.Task("publish", _ => Task.CompletedTask);

        shard.Requires(publish);
        publish.Requires(load);

        ProblemsOf(builder).ShouldBe(
        [
            new GraphProblem(
                NodePath.Parse("shard-1"),
                "These dependencies form a cycle: 'shard-1' requires 'publish', 'publish' requires 'shard-1/tenant-a/load'."),
        ]);
    }

    [Test]
    public void A_cycle_through_an_empty_group_is_reported()
    {
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1");
        var publish = builder.Task("publish", _ => Task.CompletedTask);

        shard.After(publish);
        publish.Requires(shard);

        ProblemsOf(builder).ShouldBe(
        [
            new GraphProblem(
                NodePath.Parse("shard-1"),
                "These dependencies form a cycle: 'shard-1' runs after 'publish', 'publish' requires 'shard-1'."),
        ]);
    }

    [Test]
    public void Two_cycles_are_two_problems_and_a_cycle_many_tasks_close_is_reported_once()
    {
        var builder = new GraphBuilder();
        var load = builder.Task("load", _ => Task.CompletedTask);
        var verify = builder.Task("verify", _ => Task.CompletedTask);
        var shard = builder.Group("shard-1");
        var publish = builder.Task("publish", _ => Task.CompletedTask);

        load.Requires(verify);
        verify.Requires(load);
        foreach (var tenant in (string[])["tenant-a", "tenant-b", "tenant-c"])
        {
            shard.Task(tenant, _ => Task.CompletedTask).Requires(publish);
        }

        publish.Requires(shard);

        ProblemsOf(builder).Select(problem => problem.Message).ShouldBe(
        [
            "These dependencies form a cycle: 'load' requires 'verify', 'verify' requires 'load'.",
            "These dependencies form a cycle: 'shard-1/tenant-a' requires 'publish', 'publish' requires 'shard-1'.",
        ]);
    }

    [Test]
    public void Dependencies_that_only_share_nodes_are_not_a_cycle()
    {
        var builder = new GraphBuilder();
        var load = builder.Task("load", _ => Task.CompletedTask);
        var shard = builder.Group("shard-1").Requires(load);
        var prepare = shard.Task("prepare", _ => Task.CompletedTask);
        var tenant = shard.Group("tenant-a").After(prepare);
        var verify = tenant.Task("verify", _ => Task.CompletedTask).Requires([load, prepare]);
        var empty = builder.Group("shard-2").Requires(shard);
        builder.Task("publish", _ => Task.CompletedTask).Requires([shard, empty]).After(verify);
        builder.Task("report", _ => Task.CompletedTask).After([tenant, empty, load]);

        builder.Build().Count.ShouldBe(8);
    }

    [Test]
    public void A_chain_of_dependencies_as_long_as_a_large_graph_builds()
    {
        var builder = new GraphBuilder();
        var previous = builder.Task("task-0", _ => Task.CompletedTask);
        for (var index = 1; index < 20_000; index++)
        {
            previous = builder.Task($"task-{index}", _ => Task.CompletedTask).Requires(previous);
        }

        builder.Build().Count.ShouldBe(20_000);
    }

    private static IReadOnlyList<GraphProblem> ProblemsOf(GraphBuilder builder)
        => Should.Throw<GraphException>(() => builder.Build()).Problems;
}
