using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace Dagline.Tests;

public class WarningTests
{
    [Test]
    public async Task A_warning_is_an_event_when_it_is_added_and_stays_on_the_task_without_changing_its_status_or_blocking_a_dependent()
    {
        var clock = new FakeTimeProvider();
        var started = clock.GetUtcNow();
        var builder = new GraphBuilder();
        var load = builder.Task("load", context =>
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            context.Warn("entity 9 has no region");
            return Task.CompletedTask;
        });
        var publish = builder.Task("publish", _ => Task.CompletedTask).Requires(load);
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(
            builder.Build(),
            new RunOptions { Journal = Journal.None, TimeProvider = clock, Reporters = [reporter] });

        result.Outcome.ShouldBe(RunOutcome.Complete);
        reporter.Lines.ShouldBe(
        [
            "run started",
            "load: pending -> ready",
            "load: ready -> running",
            "warning load: entity 9 has no region",
            "load: running -> succeeded",
            "publish: pending -> ready",
            "publish: ready -> running",
            "publish: running -> succeeded",
            "run ended: complete",
        ]);
        var added = reporter.Events.OfType<WarningAdded>().ShouldHaveSingleItem();
        added.At.ShouldBe(started + TimeSpan.FromSeconds(2));
        added.Path.ShouldBe(load.Path);
        added.Warning.ShouldBe("entity 9 has no region");

        var asOfTheWarning = reporter.Snapshots[3][load.Path];
        asOfTheWarning.Status.ShouldBe(NodeStatus.Running);
        asOfTheWarning.Warnings.ShouldBe(["entity 9 has no region"]);
        reporter.Snapshots[2][load.Path].Warnings.ShouldBeEmpty();

        var settled = result.Snapshot[load.Path];
        settled.Status.ShouldBe(NodeStatus.Succeeded);
        settled.Warnings.ShouldBe(["entity 9 has no region"]);
        settled.WarningsOmitted.ShouldBe(0);
        result.Snapshot[publish.Path].Status.ShouldBe(NodeStatus.Succeeded);
        result.Snapshot[publish.Path].Warnings.ShouldBeEmpty();
    }

    [Test]
    public async Task The_warnings_of_a_task_that_fails_stay_on_it_in_the_order_added()
    {
        var builder = new GraphBuilder();
        var load = builder.Task("load", context =>
        {
            context.Warn("entity 9 has no region");
            context.Warn("entity 12 has no region");
            throw new InvalidOperationException("entity 18 has no owner");
        });

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });

        var failed = result.Snapshot[load.Path];
        failed.Status.ShouldBe(NodeStatus.Failed);
        failed.Warnings.ShouldBe(["entity 9 has no region", "entity 12 has no region"]);
    }

    [Test]
    public async Task Each_line_break_in_a_warning_is_replaced_by_a_space()
    {
        var builder = new GraphBuilder();
        var load = builder.Task("load", context =>
        {
            context.Warn("entity 9\r\nhas no region\nor no owner\rat all");
            return Task.CompletedTask;
        });
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        reporter.Events.OfType<WarningAdded>().ShouldHaveSingleItem().Warning.ShouldBe("entity 9 has no region or no owner at all");
        result.Snapshot[load.Path].Warnings.ShouldBe(["entity 9 has no region or no owner at all"]);
    }

    [Test]
    public async Task Only_the_first_hundred_warnings_of_a_task_are_kept_and_published_and_the_rest_are_counted()
    {
        var builder = new GraphBuilder();
        var load = builder.Task("load", context =>
        {
            for (var entity = 1; entity <= 103; entity++)
            {
                context.Warn($"entity {entity} has no region");
            }

            return Task.CompletedTask;
        });
        var verify = builder.Task("verify", context =>
        {
            context.Warn("entity 7 has no owner");
            return Task.CompletedTask;
        });
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        var expected = Enumerable.Range(1, 100).Select(entity => $"entity {entity} has no region").ToList();
        var settled = result.Snapshot[load.Path];
        settled.Status.ShouldBe(NodeStatus.Succeeded);
        settled.Warnings.ShouldBe(expected);
        settled.WarningsOmitted.ShouldBe(3);
        reporter.Events.OfType<WarningAdded>().Where(added => added.Path == load.Path).Select(added => added.Warning).ShouldBe(expected);
        result.Snapshot[verify.Path].Warnings.ShouldBe(["entity 7 has no owner"]);
        result.Snapshot[verify.Path].WarningsOmitted.ShouldBe(0);
        result.Snapshot.WarningCount.ShouldBe(104);
    }

    [Test]
    public async Task The_snapshot_counts_every_warning_and_lists_the_tasks_that_have_any_in_declaration_order()
    {
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1");
        var load = shard.Task("load", context =>
        {
            context.Warn("entity 9 has no region");
            context.Warn("entity 12 has no region");
            return Task.CompletedTask;
        });
        shard.Task("verify", _ => Task.CompletedTask);
        var publish = builder.Task("publish", context =>
        {
            context.Warn("nothing to publish for tenant-b");
            throw new InvalidOperationException("the store is closed");
        });
        load.After(publish);
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        reporter.Snapshots[0].WarningCount.ShouldBe(0);
        reporter.Snapshots[0].WithWarnings.ShouldBeEmpty();

        var firstWarning = reporter.Snapshots[reporter.Lines.ToList().IndexOf("warning publish: nothing to publish for tenant-b")];
        firstWarning.WarningCount.ShouldBe(1);
        firstWarning.WithWarnings.Select(task => task.Path).ShouldBe([publish.Path]);

        result.Snapshot.WarningCount.ShouldBe(3);
        result.Snapshot.WithWarnings.Select(task => task.Path).ShouldBe([load.Path, publish.Path]);
        result.Snapshot.WithWarnings[0].ShouldBeSameAs(result.Snapshot[load.Path]);
        result.Snapshot.WithWarnings[1].ShouldBeSameAs(result.Snapshot[publish.Path]);
    }
}
