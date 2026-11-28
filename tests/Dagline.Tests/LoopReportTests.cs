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
}
