using Dagline.TestSupport;

namespace Dagline.Tests;

public class CapTests
{
    [Test]
    public async Task With_a_cap_of_one_the_ready_tasks_run_one_at_a_time_in_declaration_order()
    {
        var builder = new GraphBuilder();
        builder.Task("shard-1", _ => Task.CompletedTask);
        builder.Task("shard-2", _ => Task.CompletedTask);
        builder.Task("shard-3", _ => Task.CompletedTask);
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(
            builder.Build(),
            new RunOptions { Journal = Journal.None, Cap = 1, Reporters = [reporter] });

        result.Outcome.ShouldBe(RunOutcome.Complete);
        reporter.Lines.ShouldBe(
        [
            "run started",
            "shard-1: pending -> ready",
            "shard-2: pending -> ready",
            "shard-3: pending -> ready",
            "shard-1: ready -> running",
            "shard-1: running -> succeeded",
            "shard-2: ready -> running",
            "shard-2: running -> succeeded",
            "shard-3: ready -> running",
            "shard-3: running -> succeeded",
            "run ended: complete",
        ]);
    }
}
