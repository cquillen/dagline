using Dagline.TestSupport;

namespace Dagline.Tests;

/// <summary>
/// What a task says about itself, on the new engine (ADR 0015): a kept warning, a wait, the message, a detail line and a reported
/// failure are inputs, applied when the loop takes them, and progress is a sample, read by whoever builds a snapshot. Each test is
/// scripted on the test's own thread, so it states when the loop applies what.
/// </summary>
public class LoopReportTests
{
    [Test]
    public async Task A_kept_warning_is_not_in_the_current_snapshot_when_the_call_returns_and_is_once_the_loop_has_applied_it()
    {
        var load = new PausedBody();
        var builder = new GraphBuilder();
        var loadTask = builder.Task("load", load.Body);
        var reporter = new RecordingReporter();
        var run = ScriptedDriver.Start(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        run.Pump();
        var context = await load.Started;

        context.AddWarning("entity 9 has no region");
        var whenTheCallReturns = run.Current[loadTask.Path];
        run.PumpLoop().ShouldBe(1);
        var onceApplied = run.Current[loadTask.Path];
        load.Release();
        await run.Finish();

        whenTheCallReturns.Warnings.ShouldBeEmpty();
        onceApplied.Warnings.ShouldBe(["entity 9 has no region"]);
        reporter.Lines.ShouldBe(
        [
            "run started",
            "load: pending -> ready",
            "load: ready -> running",
            "warning load: entity 9 has no region",
            "load: running -> succeeded",
            "run ended: complete",
        ]);
    }

    [Test]
    public async Task Overlapping_waiting_scopes_give_one_begin_and_one_end_however_they_overlap()
    {
        var job = new PausedBody();
        var builder = new GraphBuilder();
        var jobTask = builder.Task("job", job.Body);
        var reporter = new RecordingReporter();
        var run = ScriptedDriver.Start(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        run.Pump();
        var context = await job.Started;

        var first = context.BeginWaiting("job 17 to finish");
        var second = context.BeginWaiting("job 18 to finish");
        first.Dispose();
        run.Pump();
        var whileTheSecondIsOpen = run.Current[jobTask.Path];
        second.Dispose();
        second.Dispose();
        run.Pump();
        var onceBothHaveClosed = run.Current[jobTask.Path];
        job.Release();
        await run.Finish();

        whileTheSecondIsOpen.WaitingText.ShouldBe("job 17 to finish");
        onceBothHaveClosed.WaitingSince.ShouldBeNull();
        reporter.Lines.ShouldBe(
        [
            "run started",
            "job: pending -> ready",
            "job: ready -> running",
            "waiting job: job 17 to finish",
            "waited job",
            "job: running -> succeeded",
            "run ended: complete",
        ]);
    }

    [Test]
    public async Task A_task_that_settles_with_a_wait_open_stops_waiting_just_before_it_settles()
    {
        var job = new PausedBody();
        var builder = new GraphBuilder();
        var jobTask = builder.Task("job", job.Body);
        var reporter = new RecordingReporter();
        var run = ScriptedDriver.Start(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        run.Pump();
        var context = await job.Started;

        var leftOpen = context.BeginWaiting("job 17 to finish");
        run.Pump();
        job.Release();
        var result = await run.Finish();
        leftOpen.Dispose();
        run.Pump();

        reporter.Lines.ShouldBe(
        [
            "run started",
            "job: pending -> ready",
            "job: ready -> running",
            "waiting job: job 17 to finish",
            "waited job",
            "job: running -> succeeded",
            "run ended: complete",
        ]);
        var settled = reporter.Events.OfType<TaskStatusChanged>().Single(changed => changed.To is NodeStatus.Succeeded);
        reporter.Events.OfType<WaitingChanged>().Last().At.ShouldBe(settled.At);
        settled.Node.WaitingSince.ShouldBeNull();
        result.Snapshot[jobTask.Path].WaitingSince.ShouldBeNull();
    }

    [Test]
    public async Task The_message_and_detail_lines_are_no_event_and_show_once_the_loop_has_applied_them_and_found_its_inbox_empty()
    {
        var load = new PausedBody();
        var builder = new GraphBuilder();
        var loadTask = builder.Task("load", load.Body);
        var reporter = new RecordingReporter();
        var run = ScriptedDriver.Start(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        run.Pump();
        var context = await load.Started;
        var eventsBefore = reporter.Events.Count;

        context.SetMessage("checking entities");
        context.AddDetail("tenant-a: 25 entities", "tenant-b: none");
        context.SetMessage("checked 40 entities");
        var whenTheCallsReturn = run.Current[loadTask.Path];
        run.PumpLoop().ShouldBe(1);
        var onceApplied = run.Current[loadTask.Path];
        run.Pump();
        var eventsAfter = reporter.Events.Count;
        load.Release();
        var result = await run.Finish();

        whenTheCallsReturn.Message.ShouldBeNull();
        whenTheCallsReturn.Detail.ShouldBeEmpty();
        onceApplied.Message.ShouldBe("checked 40 entities");
        onceApplied.Detail.ShouldBe(["tenant-a: 25 entities", "tenant-b: none"]);
        eventsAfter.ShouldBe(eventsBefore);
        result.Snapshot[loadTask.Path].Message.ShouldBe("checked 40 entities");
    }

    [Test]
    public async Task A_reported_failure_is_applied_in_the_order_of_the_calls_and_wins_over_the_bodys_end()
    {
        var load = new PausedBody();
        var builder = new GraphBuilder();
        var loadTask = builder.Task("load", load.Body);
        var run = ScriptedDriver.Start(builder.Build(), new RunOptions { Journal = Journal.None });
        run.Pump();
        var context = await load.Started;

        context.SetMessage("checking entities");
        context.Fail("2 bad entities", "entity 3: missing owner");
        context.AddDetail("entity 17: bad date");
        load.Release();
        var result = await run.Finish();

        var failed = result.Snapshot[loadTask.Path];
        failed.Status.ShouldBe(NodeStatus.Failed);
        failed.Failure.ShouldBe(FailureKind.Reported);
        failed.Message.ShouldBe("2 bad entities");
        failed.Detail.ShouldBe(["entity 3: missing owner", "entity 17: bad date"]);
    }
}
