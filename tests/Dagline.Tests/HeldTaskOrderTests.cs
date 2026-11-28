using Dagline.TestSupport;

namespace Dagline.Tests;

/// <summary>
/// Which held tasks start, and when, as a full limit gives slots back: in declaration order, and a held task that does not fit
/// never holds up those behind it.
/// </summary>
public class HeldTaskOrderTests
{
    [Test]
    public async Task A_held_task_still_held_by_a_second_full_limit_does_not_hold_up_a_task_behind_it()
    {
        // Not repeated: every body that matters is paused and the test releases one at a time, so each start asserted on can
        // happen at one point only.
        var firstLoad = new PausedBody();
        var export = new PausedBody();
        var secondLoad = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("load-1", firstLoad.Body).Limit("database");
        builder.Task("export", export.Body).Limit("files");
        builder.Task("merge", _ => Task.CompletedTask).Limit("database").Limit("files");
        builder.Task("load-2", secondLoad.Body).Limit("database");
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(
            builder.Build(),
            new RunOptions
            {
                Journal = Journal.None,
                Limits = new Dictionary<string, int> { ["database"] = 1, ["files"] = 1 },
                Reporters = [reporter],
            });
        await firstLoad.Started;
        await export.Started;
        firstLoad.Release();
        await secondLoad.Started;
        export.Release();
        secondLoad.Release();
        var result = await run;

        // When load-1 gives database back, merge is tried first and turned away by files, and load-2 starts in the same walk.
        result.Outcome.ShouldBe(RunOutcome.Complete);
        var lines = reporter.Lines.ToList();
        var secondLoadStarted = lines.IndexOf("load-2: ready -> running");
        secondLoadStarted.ShouldBeGreaterThan(lines.IndexOf("load-1: running -> succeeded"));
        secondLoadStarted.ShouldBeLessThan(lines.IndexOf("export: running -> succeeded"));
        var mergeStarted = lines.IndexOf("merge: ready -> running");
        mergeStarted.ShouldBeGreaterThan(lines.IndexOf("export: running -> succeeded"));
        mergeStarted.ShouldBeGreaterThan(lines.IndexOf("load-2: running -> succeeded"));
    }

    [Test]
    public async Task Held_tasks_not_reached_once_the_max_running_tasks_is_reached_start_when_a_task_holding_no_limit_settles()
    {
        // Not repeated: every body is paused and the test releases one at a time, so each start asserted on can happen at one
        // point only.
        var firstLoad = new PausedBody();
        var secondLoad = new PausedBody();
        var firstReport = new PausedBody();
        var secondReport = new PausedBody();
        var thirdLoad = new PausedBody();
        var builder = new GraphBuilder();
        var load = builder.Task("load-1", firstLoad.Body).Limit("database");
        builder.Task("load-2", secondLoad.Body).Limit("database");
        builder.Task("report-1", firstReport.Body).Requires(load);
        builder.Task("report-2", secondReport.Body).Requires(load);
        builder.Task("load-3", thirdLoad.Body).Limit("database");
        builder.Task("load-4", _ => Task.CompletedTask).Limit("database");
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(
            builder.Build(),
            new RunOptions
            {
                Journal = Journal.None,
                MaxRunningTasks = 3,
                Limits = new Dictionary<string, int> { ["database"] = 2 },
                Reporters = [reporter],
            });
        await firstLoad.Started;
        await secondLoad.Started;

        // load-1 gives database back, and report-1 and report-2, declared before load-3, take the last two running slots.
        firstLoad.Release();
        await firstReport.Started;
        await secondReport.Started;

        // report-1 holds no limit, so it gives no slot of database back, but load-3 was never reached and still fits.
        firstReport.Release();
        await thirdLoad.Started;
        secondLoad.Release();
        secondReport.Release();
        thirdLoad.Release();
        var result = await run;

        result.Outcome.ShouldBe(RunOutcome.Complete);
        var lines = reporter.Lines.ToList();
        var thirdLoadStarted = lines.IndexOf("load-3: ready -> running");
        thirdLoadStarted.ShouldBeGreaterThan(lines.IndexOf("report-1: running -> succeeded"));
        thirdLoadStarted.ShouldBeLessThan(lines.IndexOf("load-2: running -> succeeded"));
        thirdLoadStarted.ShouldBeLessThan(lines.IndexOf("report-2: running -> succeeded"));
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_limit_named_on_a_group_holds_back_the_tasks_beneath_it_as_one_named_on_each_task_does(bool namedOnTheGroup)
    {
        // Not repeated: every body is paused and the test releases one at a time, so each start asserted on can happen at one
        // point only.
        var holder = new PausedBody();
        var firstLoad = new PausedBody();
        var secondLoad = new PausedBody();
        var publish = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("holder", holder.Body).Limit("database");
        var shard = builder.Group("shard-1");
        var loads = new[] { shard.Task("load-1", firstLoad.Body), shard.Task("load-2", secondLoad.Body) };
        if (namedOnTheGroup)
        {
            shard.Limit("database");
        }
        else
        {
            foreach (var task in loads)
            {
                task.Limit("database");
            }
        }

        builder.Task("publish", publish.Body);
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(
            builder.Build(),
            new RunOptions
            {
                Journal = Journal.None,
                Limits = new Dictionary<string, int> { ["database"] = 1 },
                Reporters = [reporter],
            });
        await holder.Started;
        await publish.Started;
        holder.Release();
        await firstLoad.Started;
        firstLoad.Release();
        await secondLoad.Started;
        secondLoad.Release();
        publish.Release();
        var result = await run;

        // The loads are held behind holder while publish, declared after them, starts in the first walk.
        result.Outcome.ShouldBe(RunOutcome.Complete);
        var lines = reporter.Lines.ToList();
        lines.IndexOf("publish: ready -> running").ShouldBeLessThan(lines.IndexOf("holder: running -> succeeded"));
        lines.IndexOf("shard-1/load-1: ready -> running").ShouldBeGreaterThan(lines.IndexOf("holder: running -> succeeded"));
        lines.IndexOf("shard-1/load-2: ready -> running").ShouldBeGreaterThan(lines.IndexOf("shard-1/load-1: running -> succeeded"));
        lines.IndexOf("shard-1/load-2: ready -> running").ShouldBeLessThan(lines.IndexOf("publish: running -> succeeded"));
        reporter.Snapshots.Max(snapshot => snapshot.Limits["database"].InUse).ShouldBe(1);
    }
}
