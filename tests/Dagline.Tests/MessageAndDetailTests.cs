namespace Dagline.Tests;

public class MessageAndDetailTests
{
    [Test]
    public async Task A_task_that_succeeds_keeps_the_message_it_last_set_and_every_detail_line_it_added()
    {
        var builder = new GraphBuilder();
        var load = builder.Task("load", context =>
        {
            context.SetMessage("checking entities");
            context.AddDetail("tenant-a: 25 entities");
            context.SetMessage("checked 40 entities");
            context.AddDetail("tenant-b: 15 entities", "tenant-c: none");
            return Task.CompletedTask;
        });

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });

        result.Outcome.ShouldBe(RunOutcome.Complete);
        var succeeded = result.Snapshot[load.Path];
        succeeded.Status.ShouldBe(NodeStatus.Succeeded);
        succeeded.Failure.ShouldBeNull();
        succeeded.Message.ShouldBe("checked 40 entities");
        succeeded.Detail.ShouldBe(["tenant-a: 25 entities", "tenant-b: 15 entities", "tenant-c: none"]);
        succeeded.DetailOmitted.ShouldBe(0);
    }

    [Test]
    public async Task Each_line_break_in_a_message_is_replaced_by_a_space_and_text_given_as_detail_is_split_into_one_line_per_break()
    {
        var builder = new GraphBuilder();
        var load = builder.Task("load", context =>
        {
            context.SetMessage("checked 40 entities\r\nin 3 tenants\nand found\r2 problems");
            context.AddDetail("entity 3: missing owner\r\nentity 17: bad date", "entity 20: no region\n\nentity 21: no region\rentity 22: no region");
            return Task.CompletedTask;
        });

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });

        var succeeded = result.Snapshot[load.Path];
        succeeded.Message.ShouldBe("checked 40 entities in 3 tenants and found 2 problems");
        succeeded.Detail.ShouldBe(
        [
            "entity 3: missing owner",
            "entity 17: bad date",
            "entity 20: no region",
            string.Empty,
            "entity 21: no region",
            "entity 22: no region",
        ]);
    }

    [Test]
    public async Task Only_the_first_hundred_detail_lines_of_a_task_are_kept_and_the_rest_are_counted_apart_from_its_warnings()
    {
        var builder = new GraphBuilder();
        var load = builder.Task("load", context =>
        {
            context.AddDetail([.. Enumerable.Range(1, 60).Select(entity => $"entity {entity}: missing owner")]);
            context.AddWarning("entity 9 has no region");
            context.AddDetail(string.Join('\n', Enumerable.Range(61, 43).Select(entity => $"entity {entity}: missing owner")));
            context.AddDetail("entity 104: missing owner");
            return Task.CompletedTask;
        });

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });

        var succeeded = result.Snapshot[load.Path];
        succeeded.Detail.ShouldBe(Enumerable.Range(1, 100).Select(entity => $"entity {entity}: missing owner"));
        succeeded.DetailOmitted.ShouldBe(4);
        succeeded.Warnings.ShouldBe(["entity 9 has no region"]);
        succeeded.WarningsOmitted.ShouldBe(0);
    }

    [Test]
    public async Task A_null_message_warning_or_detail_is_rejected_and_changes_nothing()
    {
        var builder = new GraphBuilder();
        var load = builder.Task("load", context =>
        {
            Should.Throw<ArgumentNullException>(() => context.SetMessage(null!)).ParamName.ShouldBe("message");
            Should.Throw<ArgumentNullException>(() => context.AddWarning(null!)).ParamName.ShouldBe("warning");
            Should.Throw<ArgumentNullException>(() => context.AddDetail(null!)).ParamName.ShouldBe("lines");
            Should.Throw<ArgumentException>(() => context.AddDetail("entity 3: missing owner", null!)).ParamName.ShouldBe("lines");
            return Task.CompletedTask;
        });

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });

        var succeeded = result.Snapshot[load.Path];
        succeeded.Status.ShouldBe(NodeStatus.Succeeded);
        succeeded.Message.ShouldBeNull();
        succeeded.Detail.ShouldBeEmpty();
        succeeded.Warnings.ShouldBeEmpty();
    }
}
