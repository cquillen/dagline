using Dagline.TestSupport;

namespace Dagline.Tests;

public class FailureSummaryTests
{
    [Test]
    public async Task Failed_lists_the_failed_tasks_in_declaration_order_whatever_order_they_failed_in()
    {
        var load = new PausedBody();
        var verifySettled = new PausedBody();
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1");
        var loadTask = shard.Task("load", async context =>
        {
            await load.Body(context);
            throw new InvalidOperationException("no entities to load");
        });
        var verify = builder.Task("verify", _ => throw new InvalidOperationException("entity 3 has a bad date"));
        builder.Task("publish", _ => Task.CompletedTask).Requires(verify);
        builder.Task("count", _ => Task.CompletedTask);
        builder.Task("verify-settled", verifySettled.Body).After(verify);
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        var source = await reporter.Source;
        await verifySettled.Started;
        var oneFailure = source.Current;
        load.Release();
        verifySettled.Release();
        var result = await run;

        reporter.Snapshots[0].Failed.ShouldBeEmpty();
        oneFailure.Failed.Select(task => task.Path).ShouldBe([verify.Path]);
        result.Snapshot.Failed.Select(task => task.Path).ShouldBe([loadTask.Path, verify.Path]);
        result.Snapshot.Failed[0].ShouldBeSameAs(result.Snapshot[loadTask.Path]);
        result.Snapshot.Failed[1].ShouldBeSameAs(result.Snapshot[verify.Path]);
        result.Snapshot[shard.Path].Status.ShouldBe(NodeStatus.Failed);
    }

    [Test]
    public async Task Blocked_gives_one_entry_for_each_failed_task_that_blocked_anything_with_its_tasks_and_its_roots()
    {
        var load = new PausedBody();
        var verifySettled = new PausedBody();
        var builder = new GraphBuilder();
        var loadTask = builder.Task("load", async context =>
        {
            await load.Body(context);
            throw new InvalidOperationException("no entities to load");
        });
        var verify = builder.Task("verify", _ => throw new InvalidOperationException("entity 3 has a bad date"));
        builder.Task("count", _ => throw new InvalidOperationException("the count is off by one"));
        var wholeGroup = builder.Group("shard-1").Requires(loadTask);
        var tenant = wholeGroup.Group("tenant-a");
        tenant.Task("process", _ => Task.CompletedTask);
        tenant.Task("store", _ => Task.CompletedTask);
        wholeGroup.Group("tenant-b").Task("process", _ => Task.CompletedTask);
        wholeGroup.Group("tenant-c");
        var mixedGroup = builder.Group("shard-2");
        var wholeSubgroup = mixedGroup.Group("tenant-a").Requires(loadTask);
        wholeSubgroup.Task("process", _ => Task.CompletedTask);
        var partSubgroup = mixedGroup.Group("tenant-b");
        partSubgroup.Task("process", _ => Task.CompletedTask);
        var check = partSubgroup.Task("check", _ => Task.CompletedTask).Requires(verify);
        var publish = builder.Task("publish", _ => Task.CompletedTask).Requires(wholeGroup);
        builder.Task("verify-settled", verifySettled.Body).After(verify);
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        var source = await reporter.Source;
        await verifySettled.Started;
        var oneCascade = source.Current;
        load.Release();
        verifySettled.Release();
        var result = await run;

        reporter.Snapshots[0].Blocked.ShouldBeEmpty();
        oneCascade.Blocked.Select(blocked => blocked.Cause).ShouldBe([verify.Path]);

        var blocked = result.Snapshot.Blocked;
        blocked.Select(entry => entry.Cause).ShouldBe([loadTask.Path, verify.Path]);
        blocked[0].Tasks.Select(task => task.Path.ToString()).ShouldBe(
        [
            "shard-1/tenant-a/process",
            "shard-1/tenant-a/store",
            "shard-1/tenant-b/process",
            "shard-2/tenant-a/process",
            "publish",
        ]);
        blocked[0].Roots.Select(root => root.Path).ShouldBe([wholeGroup.Path, wholeSubgroup.Path, publish.Path]);
        blocked[0].Roots[0].ShouldBeSameAs(result.Snapshot[wholeGroup.Path]);
        blocked[0].Tasks[4].ShouldBeSameAs(result.Snapshot[publish.Path]);
        blocked[1].Tasks.Select(task => task.Path).ShouldBe([check.Path]);
        blocked[1].Roots.Select(root => root.Path).ShouldBe([check.Path]);
    }

    [Test]
    public async Task A_group_that_holds_the_failed_task_is_not_a_root_of_what_that_task_blocked()
    {
        var builder = new GraphBuilder();
        var checks = builder.Group("checks");
        var verify = checks.Task("verify", _ => throw new InvalidOperationException("entity 3 has a bad date"));
        var sign = checks.Task("sign", _ => Task.CompletedTask).Requires(verify);
        var publish = builder.Task("publish", _ => Task.CompletedTask).Requires(checks);
        var notify = builder.Task("notify", _ => Task.CompletedTask).Requires(publish);

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });

        var blocked = result.Snapshot.Blocked.ShouldHaveSingleItem();
        blocked.Cause.ShouldBe(verify.Path);
        blocked.Tasks.Select(task => task.Path).ShouldBe([sign.Path, publish.Path, notify.Path]);
        blocked.Roots.Select(root => root.Path).ShouldBe([sign.Path, publish.Path, notify.Path]);
    }

    [Test]
    public async Task A_run_in_which_nothing_failed_has_no_failed_tasks_and_no_blocked_tasks()
    {
        var builder = new GraphBuilder();
        builder.Group("shard-1").Task("load", _ => Task.CompletedTask);

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });

        result.Snapshot.Failed.ShouldBeEmpty();
        result.Snapshot.Blocked.ShouldBeEmpty();
    }
}
