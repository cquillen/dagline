using Dagline.TestSupport;

namespace Dagline.Tests;

public class ReportedFailureTests
{
    [Test]
    public async Task Throwing_what_Fail_returns_ends_the_task_as_a_reported_failure_that_blocks_its_dependents()
    {
        var builder = new GraphBuilder();
        var verify = builder.Task("verify", context =>
        {
            context.Message("checking entities");
            context.Detail("checked 40 entities");
            throw context.Fail("2 bad entities", "entity 3: missing owner", "entity 17: bad date\nentity 17: no region");
        });
        var publish = builder.Task("publish", _ => Task.CompletedTask).Requires(verify);
        var report = builder.Task("report", _ => Task.CompletedTask).After(verify);
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        result.Outcome.ShouldBe(RunOutcome.Incomplete);
        reporter.Lines.ShouldBe(
        [
            "run started",
            "verify: pending -> ready",
            "verify: ready -> running",
            "verify: running -> failed",
            "publish: pending -> blocked",
            "report: pending -> ready",
            "report: ready -> running",
            "report: running -> succeeded",
            "run ended: incomplete",
        ]);
        var failed = result.Snapshot[verify.Path];
        failed.Status.ShouldBe(NodeStatus.Failed);
        failed.Failure.ShouldBe(FailureKind.Reported);
        failed.Message.ShouldBe("2 bad entities");
        failed.Detail.ShouldBe(["checked 40 entities", "entity 3: missing owner", "entity 17: bad date", "entity 17: no region"]);
        reporter.Events.OfType<TaskStatusChanged>().Single(changed => changed.To is NodeStatus.Failed).Node.ShouldBeSameAs(failed);
        result.Snapshot[publish.Path].Status.ShouldBe(NodeStatus.Blocked);
        result.Snapshot[publish.Path].BlockedBy.ShouldBe(verify.Path);
        result.Snapshot[report.Path].Status.ShouldBe(NodeStatus.Succeeded);
    }

    [Test]
    public async Task A_task_that_calls_Fail_and_then_returns_still_ends_as_a_reported_failure()
    {
        var builder = new GraphBuilder();
        var verify = builder.Task("verify", context =>
        {
            context.Fail("2 bad entities", "entity 3: missing owner");
            return Task.CompletedTask;
        });
        var publish = builder.Task("publish", _ => Task.CompletedTask).Requires(verify);

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });

        result.Outcome.ShouldBe(RunOutcome.Incomplete);
        var failed = result.Snapshot[verify.Path];
        failed.Status.ShouldBe(NodeStatus.Failed);
        failed.Failure.ShouldBe(FailureKind.Reported);
        failed.Message.ShouldBe("2 bad entities");
        failed.Detail.ShouldBe(["entity 3: missing owner"]);
        result.Snapshot[publish.Path].Status.ShouldBe(NodeStatus.Blocked);
    }

    [Test]
    public async Task A_task_that_calls_Fail_and_then_lets_another_exception_escape_still_ends_as_a_reported_failure()
    {
        var builder = new GraphBuilder();
        var verify = builder.Task("verify", context =>
        {
            context.Fail("2 bad entities", "entity 3: missing owner");
            throw new InvalidOperationException("the store is closed");
        });

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });

        var failed = result.Snapshot[verify.Path];
        failed.Status.ShouldBe(NodeStatus.Failed);
        failed.Failure.ShouldBe(FailureKind.Reported);
        failed.Message.ShouldBe("2 bad entities");
        failed.Detail.ShouldBe(["entity 3: missing owner"]);
    }

    [Test]
    public async Task A_reported_failure_exception_that_the_tasks_own_Fail_did_not_make_is_an_unexpected_failure()
    {
        ReportedFailureException? fromVerify = null;
        var builder = new GraphBuilder();
        var verify = builder.Task("verify", context =>
        {
            fromVerify = context.Fail("2 bad entities");
            throw fromVerify;
        });
        var report = builder.Task("report", _ => throw fromVerify!).After(verify);

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });

        result.Snapshot[verify.Path].Failure.ShouldBe(FailureKind.Reported);
        var failed = result.Snapshot[report.Path];
        failed.Status.ShouldBe(NodeStatus.Failed);
        failed.Failure.ShouldBe(FailureKind.Unexpected);
        failed.Message.ShouldBe("2 bad entities");
        failed.Detail[0].ShouldBe("Dagline.ReportedFailureException");
    }

    [Test]
    public async Task Fail_replaces_the_message_adds_to_the_detail_lines_and_returns_an_exception_that_carries_what_it_was_given()
    {
        ReportedFailureException? returned = null;
        var builder = new GraphBuilder();
        var verify = builder.Task("verify", context =>
        {
            context.Fail("1 bad entity", "entity 3: missing owner");
            returned = context.Fail("2 bad\r\nentities", "entity 17: bad date\nentity 17: no region", "entity 17: skipped");
            throw returned;
        });

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });

        var failed = result.Snapshot[verify.Path];
        failed.Failure.ShouldBe(FailureKind.Reported);
        failed.Message.ShouldBe("2 bad entities");
        failed.Detail.ShouldBe(["entity 3: missing owner", "entity 17: bad date", "entity 17: no region", "entity 17: skipped"]);
        returned.ShouldNotBeNull();
        returned.Message.ShouldBe("2 bad entities");
        returned.Detail.ShouldBe(["entity 17: bad date", "entity 17: no region", "entity 17: skipped"]);
    }

    [Test]
    public async Task Fail_rejects_a_null_message_or_a_null_detail_line_and_records_no_failure()
    {
        var builder = new GraphBuilder();
        var verify = builder.Task("verify", context =>
        {
            Should.Throw<ArgumentNullException>(() => context.Fail(null!)).ParamName.ShouldBe("message");
            Should.Throw<ArgumentNullException>(() => context.Fail("2 bad entities", null!)).ParamName.ShouldBe("detail");
            Should.Throw<ArgumentException>(() => context.Fail("2 bad entities", "entity 3", null!)).ParamName.ShouldBe("detail");
            return Task.CompletedTask;
        });

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });

        var succeeded = result.Snapshot[verify.Path];
        succeeded.Status.ShouldBe(NodeStatus.Succeeded);
        succeeded.Message.ShouldBeNull();
        succeeded.Detail.ShouldBeEmpty();
    }
}
