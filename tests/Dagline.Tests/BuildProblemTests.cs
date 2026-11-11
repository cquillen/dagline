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

    private static IReadOnlyList<GraphProblem> ProblemsOf(GraphBuilder builder)
        => Should.Throw<GraphException>(() => builder.Build()).Problems;
}
