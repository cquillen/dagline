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

    [Test]
    public async Task An_exception_thrown_before_the_first_await_of_a_body_or_carried_by_the_task_it_returns_fails_the_task_the_same_way()
    {
        var builder = new GraphBuilder();
        var load = builder.Task("load", _ => throw new InvalidOperationException("no entities to load"));
        var verify = builder.Task("verify", _ => Task.FromException(new FormatException("entity 3 has a bad date")));

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });

        result.Outcome.ShouldBe(RunOutcome.Incomplete);
        var loadFailed = result.Snapshot[load.Path];
        loadFailed.Status.ShouldBe(NodeStatus.Failed);
        loadFailed.Failure.ShouldBe(FailureKind.Unexpected);
        loadFailed.Message.ShouldBe("no entities to load");
        loadFailed.Detail[0].ShouldBe("System.InvalidOperationException");
        var verifyFailed = result.Snapshot[verify.Path];
        verifyFailed.Status.ShouldBe(NodeStatus.Failed);
        verifyFailed.Failure.ShouldBe(FailureKind.Unexpected);
        verifyFailed.Message.ShouldBe("entity 3 has a bad date");
        verifyFailed.Detail[0].ShouldBe("System.FormatException");
    }

    [Test]
    public async Task A_cancellation_exception_that_escapes_a_body_is_an_unexpected_failure()
    {
        var builder = new GraphBuilder();
        var load = builder.Task("load", _ => throw new OperationCanceledException("gave up waiting for the store"));
        var verify = builder.Task("verify", _ => Task.FromCanceled(new CancellationToken(canceled: true)));
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        result.Outcome.ShouldBe(RunOutcome.Incomplete);
        reporter.Lines.ShouldContain("load: running -> failed");
        reporter.Lines.ShouldContain("verify: running -> failed");
        var loadFailed = result.Snapshot[load.Path];
        loadFailed.Failure.ShouldBe(FailureKind.Unexpected);
        loadFailed.Message.ShouldBe("gave up waiting for the store");
        loadFailed.Detail[0].ShouldBe("System.OperationCanceledException");
        var verifyFailed = result.Snapshot[verify.Path];
        verifyFailed.Failure.ShouldBe(FailureKind.Unexpected);
        verifyFailed.Detail[0].ShouldBe("System.Threading.Tasks.TaskCanceledException");
    }

    [Test]
    public async Task A_task_that_has_not_failed_has_no_failure_kind_no_message_and_no_detail()
    {
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1");
        var load = shard.Task("load", _ => Task.CompletedTask);

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });

        foreach (var node in new[] { result.Snapshot[shard.Path], result.Snapshot[load.Path] })
        {
            node.Failure.ShouldBeNull();
            node.Message.ShouldBeNull();
            node.Detail.ShouldBeEmpty();
        }
    }

    private static async Task BreakAfterAnAwaitAsync(TaskContext context)
    {
        await Task.Yield();
        throw new InvalidOperationException("entity 7 has no owner");
    }
}
