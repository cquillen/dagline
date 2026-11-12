using Dagline.TestSupport;

namespace Dagline.Tests;

public class ProgressTests
{
    [Test]
    public async Task Status_text_and_a_count_are_read_from_the_snapshot_source_while_the_task_runs()
    {
        var load = new PausedBody();
        var builder = new GraphBuilder();
        var loadTask = builder.Task("load", async context =>
        {
            context.Status("loading entities");
            context.Count(17, 40);
            await load.Body(context);
        });
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        var source = await reporter.Source;
        await load.Started;
        var running = source.Current[loadTask.Path];
        load.Release();
        await run;

        running.Status.ShouldBe(NodeStatus.Running);
        running.StatusText.ShouldBe("loading entities");
        running.Count.ShouldBe(17);
        running.Total.ShouldBe(40);
    }

    [Test]
    public async Task Status_text_is_kept_as_given_and_empty_status_text_clears_it()
    {
        var multiLine = new PausedBody();
        var empty = new PausedBody();
        var builder = new GraphBuilder();
        var loadTask = builder.Task("load", async context =>
        {
            context.Status("checking entities\r\nof shard-3");
            await multiLine.Body(context);
            context.Status("");
            await empty.Body(context);
        });
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        var source = await reporter.Source;
        await multiLine.Started;
        var given = source.Current[loadTask.Path];
        multiLine.Release();
        await empty.Started;
        var cleared = source.Current[loadTask.Path];
        empty.Release();
        await run;

        given.StatusText.ShouldBe("checking entities\r\nof shard-3");
        cleared.StatusText.ShouldBeNull();
    }

    [Test]
    public async Task Each_report_replaces_the_last_and_null_clears_the_status_text()
    {
        var first = new PausedBody();
        var second = new PausedBody();
        var builder = new GraphBuilder();
        var loadTask = builder.Task("load", async context =>
        {
            context.Status("loading entities");
            context.Status("checking entities");
            context.Count(3, 40);
            context.Count(4, 50);
            await first.Body(context);
            context.Status(null);
            context.Count(5);
            await second.Body(context);
        });
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        var source = await reporter.Source;
        await first.Started;
        var replaced = source.Current[loadTask.Path];
        first.Release();
        await second.Started;
        var cleared = source.Current[loadTask.Path];
        second.Release();
        await run;

        replaced.StatusText.ShouldBe("checking entities");
        replaced.Count.ShouldBe(4);
        replaced.Total.ShouldBe(50);
        cleared.StatusText.ShouldBeNull();
        cleared.Count.ShouldBe(5);
        cleared.Total.ShouldBeNull();
    }

    [Test]
    public async Task A_count_or_a_total_below_zero_is_rejected_and_changes_nothing()
    {
        var builder = new GraphBuilder();
        var loadTask = builder.Task("load", context =>
        {
            context.Count(0, 0);
            Should.Throw<ArgumentOutOfRangeException>(() => context.Count(-1)).ParamName.ShouldBe("current");
            Should.Throw<ArgumentOutOfRangeException>(() => context.Count(1, -1)).ParamName.ShouldBe("total");
            return Task.CompletedTask;
        });

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });

        var load = result.Snapshot[loadTask.Path];
        load.Status.ShouldBe(NodeStatus.Succeeded);
        load.Count.ShouldBe(0);
        load.Total.ShouldBe(0);
    }

    [Test]
    public async Task Progress_is_never_an_event_and_stays_as_the_task_left_it_in_every_snapshot_after_the_task_settles()
    {
        var verify = new PausedBody();
        var builder = new GraphBuilder();
        var loadTask = builder.Task("load", context =>
        {
            context.Status("loading entities");
            context.Count(17, 40);
            throw new InvalidOperationException("entity 18 has no owner");
        });
        builder.Task("verify", verify.Body).After(loadTask);
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        var source = await reporter.Source;
        await verify.Started;
        var afterTheSettle = source.Current[loadTask.Path];
        verify.Release();
        var result = await run;

        reporter.Lines.ShouldBe(
        [
            "run started",
            "load: pending -> ready",
            "load: ready -> running",
            "load: running -> failed",
            "verify: pending -> ready",
            "verify: ready -> running",
            "verify: running -> succeeded",
            "run ended: incomplete",
        ]);
        var settled = reporter.Events.OfType<TaskStatusChanged>().Single(changed => changed.To is NodeStatus.Failed).Node;
        foreach (var load in new[] { settled, afterTheSettle, result.Snapshot[loadTask.Path] })
        {
            load.Status.ShouldBe(NodeStatus.Failed);
            load.StatusText.ShouldBe("loading entities");
            load.Count.ShouldBe(17);
            load.Total.ShouldBe(40);
        }
    }

    [Test]
    public async Task A_task_that_reports_no_progress_and_a_group_have_no_status_text_and_no_count()
    {
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1");
        var load = shard.Task("load", _ => Task.CompletedTask);

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });

        foreach (var node in new[] { result.Snapshot[shard.Path], result.Snapshot[load.Path] })
        {
            node.StatusText.ShouldBeNull();
            node.Count.ShouldBeNull();
            node.Total.ShouldBeNull();
        }
    }
}
