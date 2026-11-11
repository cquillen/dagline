using Dagline.TestSupport;

namespace Dagline.Tests;

public class RunTests
{
    [Test]
    public async Task A_chain_of_tasks_runs_in_order_and_the_run_is_complete()
    {
        var builder = new GraphBuilder();
        var prepare = builder.Task("prepare", _ => Task.CompletedTask);
        var process = builder.Task("process", _ => Task.CompletedTask).Requires(prepare);
        builder.Task("publish", _ => Task.CompletedTask).Requires(process);
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(
            builder.Build(),
            new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        result.Outcome.ShouldBe(RunOutcome.Complete);
        reporter.Lines.ShouldBe(
        [
            "run started",
            "prepare: pending -> ready",
            "prepare: ready -> running",
            "prepare: running -> succeeded",
            "process: pending -> ready",
            "process: ready -> running",
            "process: running -> succeeded",
            "publish: pending -> ready",
            "publish: ready -> running",
            "publish: running -> succeeded",
            "run ended: complete",
        ]);
    }
}
