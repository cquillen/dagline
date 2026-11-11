using Dagline.TestSupport;

namespace Dagline.Tests;

public class FailureTests
{
    [Test]
    public async Task An_exception_that_escapes_a_task_body_fails_the_task_as_an_unexpected_failure()
    {
        var builder = new GraphBuilder();
        var load = builder.Task("load", BreakAfterAnAwaitAsync);
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        result.Outcome.ShouldBe(RunOutcome.Incomplete);
        reporter.Lines.ShouldBe(
        [
            "run started",
            "load: pending -> ready",
            "load: ready -> running",
            "load: running -> failed",
            "run ended: incomplete",
        ]);
        var failed = result.Snapshot[load.Path];
        failed.Status.ShouldBe(NodeStatus.Failed);
        failed.Failure.ShouldBe(FailureKind.Unexpected);
        failed.Message.ShouldBe("entity 7 has no owner");
        failed.Detail[0].ShouldBe("System.InvalidOperationException");
        failed.Detail.Skip(1).ShouldContain(line => line.Contains(nameof(BreakAfterAnAwaitAsync)));
        failed.Detail.ShouldAllBe(line => !line.Contains('\n') && !line.Contains('\r'));
        reporter.Events.OfType<TaskStatusChanged>().Last().Node.ShouldBeSameAs(failed);
    }

    private static async Task BreakAfterAnAwaitAsync(TaskContext context)
    {
        await Task.Yield();
        throw new InvalidOperationException("entity 7 has no owner");
    }
}
