using Dagline.TestSupport;

namespace Dagline.Tests;

public class ReporterTests
{
    [Test]
    public async Task A_paused_reporter_does_not_delay_the_run_and_the_run_returns_once_it_has_handled_every_event()
    {
        var builder = new GraphBuilder();
        var prepare = builder.Task("prepare", _ => Task.CompletedTask);
        builder.Task("publish", _ => Task.CompletedTask).Requires(prepare);
        var paused = new PausedReporter();
        var watching = new RecordingReporter();

        var run = Runner.RunAsync(
            builder.Build(),
            new RunOptions { Journal = Journal.None, Reporters = [paused, watching] });
        await paused.Paused;
        await watching.Ended;
        var handedWhilePaused = paused.Lines;
        var returnedWhilePaused = run.IsCompleted;
        paused.Release();
        var result = await run;

        result.Outcome.ShouldBe(RunOutcome.Complete);
        watching.Lines.ShouldBe(
        [
            "run started",
            "prepare: pending -> ready",
            "prepare: ready -> running",
            "prepare: running -> succeeded",
            "publish: pending -> ready",
            "publish: ready -> running",
            "publish: running -> succeeded",
            "run ended: complete",
        ]);
        handedWhilePaused.ShouldBe(["run started"]);
        returnedWhilePaused.ShouldBeFalse();
        paused.Lines.ShouldBe(watching.Lines);
    }
}
