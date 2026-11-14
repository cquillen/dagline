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
    public async Task The_message_of_an_unexpected_failure_is_one_line_with_each_line_break_of_the_exception_message_replaced_by_a_space()
    {
        var builder = new GraphBuilder();
        var load = builder.Task(
            "load",
            _ => throw new InvalidOperationException("entity 7 has no owner\r\nentity 9 has no region\nentity 11 has no owner"));

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });

        result.Snapshot[load.Path].Message.ShouldBe("entity 7 has no owner entity 9 has no region entity 11 has no owner");
    }

    [Test]
    public async Task An_unexpected_failure_replaces_the_message_and_adds_its_type_and_stack_trace_after_the_detail_lines_already_set()
    {
        var builder = new GraphBuilder();
        var load = builder.Task("load", async context =>
        {
            context.SetMessage("loading entities");
            context.AddDetail("tenant-a: 25 entities", "tenant-b: 15 entities");
            await BreakAfterAnAwaitAsync(context);
        });

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });

        var failed = result.Snapshot[load.Path];
        failed.Failure.ShouldBe(FailureKind.Unexpected);
        failed.Message.ShouldBe("entity 7 has no owner");
        failed.Detail.Take(3).ShouldBe(["tenant-a: 25 entities", "tenant-b: 15 entities", "System.InvalidOperationException"]);
        failed.Detail.Skip(3).ShouldContain(line => line.Contains(nameof(BreakAfterAnAwaitAsync)));
        failed.DetailOmitted.ShouldBe(0);
    }

    [Test]
    public async Task The_type_and_stack_trace_of_an_unexpected_failure_count_towards_the_hundred_detail_lines_kept()
    {
        var builder = new GraphBuilder();
        var load = builder.Task("load", async context =>
        {
            context.AddDetail([.. Enumerable.Range(1, 99).Select(entity => $"entity {entity}: loaded")]);
            await BreakAfterAnAwaitAsync(context);
        });

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });

        var failed = result.Snapshot[load.Path];
        failed.Detail.Count.ShouldBe(100);
        failed.Detail[98].ShouldBe("entity 99: loaded");
        failed.Detail[99].ShouldBe("System.InvalidOperationException");
        failed.DetailOmitted.ShouldBeGreaterThan(0);
    }

    // A run that cannot settle a task never ends, so this test and the two after it state a shorter timeout than the suite's 30 seconds.
    [Test]
    [Timeout(5_000)]
    public async Task An_exception_whose_message_cannot_be_read_fails_its_task_under_the_name_of_its_type_and_nothing_else(
        CancellationToken cancellationToken)
    {
        var builder = new GraphBuilder();
        var load = builder.Task("load", _ => throw new ExceptionWithNoReadableMessage());
        var report = builder.Task("report", _ => Task.CompletedTask).After(load);
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] })
            .WaitAsync(cancellationToken);

        result.Outcome.ShouldBe(RunOutcome.Incomplete);
        reporter.Lines.ShouldBe(
        [
            "run started",
            "load: pending -> ready",
            "load: ready -> running",
            "load: running -> failed",
            "report: pending -> ready",
            "report: ready -> running",
            "report: running -> succeeded",
            "run ended: incomplete",
        ]);
        var failed = result.Snapshot[load.Path];
        failed.Failure.ShouldBe(FailureKind.Unexpected);
        failed.Message.ShouldBe("Dagline.Tests.FailureTests+ExceptionWithNoReadableMessage");
        failed.Detail[0].ShouldBe("Dagline.Tests.FailureTests+ExceptionWithNoReadableMessage");
        result.Snapshot[report.Path].Status.ShouldBe(NodeStatus.Succeeded);
    }

    [Test]
    [Timeout(5_000)]
    public async Task An_exception_whose_stack_trace_cannot_be_read_fails_its_task_with_its_message_and_its_type_alone(
        CancellationToken cancellationToken)
    {
        var builder = new GraphBuilder();
        var load = builder.Task("load", _ => throw new ExceptionWithNoReadableStackTrace("entity 7 has no owner"));

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None }).WaitAsync(cancellationToken);

        result.Outcome.ShouldBe(RunOutcome.Incomplete);
        var failed = result.Snapshot[load.Path];
        failed.Failure.ShouldBe(FailureKind.Unexpected);
        failed.Message.ShouldBe("entity 7 has no owner");
        failed.Detail.ShouldBe(["Dagline.Tests.FailureTests+ExceptionWithNoReadableStackTrace"]);
    }

    [Test]
    [Timeout(5_000)]
    public async Task An_exception_that_gives_no_message_fails_its_task_under_the_name_of_its_type(CancellationToken cancellationToken)
    {
        var builder = new GraphBuilder();
        var load = builder.Task("load", _ => throw new ExceptionWithNoMessage());

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None }).WaitAsync(cancellationToken);

        result.Outcome.ShouldBe(RunOutcome.Incomplete);
        var failed = result.Snapshot[load.Path];
        failed.Failure.ShouldBe(FailureKind.Unexpected);
        failed.Message.ShouldBe("Dagline.Tests.FailureTests+ExceptionWithNoMessage");
        failed.Detail[0].ShouldBe("Dagline.Tests.FailureTests+ExceptionWithNoMessage");
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

    private sealed class ExceptionWithNoReadableMessage : Exception
    {
        public override string Message => throw new InvalidOperationException("the message cannot be read");
    }

    private sealed class ExceptionWithNoReadableStackTrace(string message) : Exception(message)
    {
        public override string? StackTrace => throw new InvalidOperationException("the stack trace cannot be read");
    }

    private sealed class ExceptionWithNoMessage : Exception
    {
        public override string Message => null!;
    }
}
