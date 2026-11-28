using Dagline.TestSupport;

namespace Dagline.Testing.Tests;

public class InstantClockRunTests
{
    private static readonly DateTimeOffset _start = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task A_graph_whose_tasks_wait_for_hours_completes_without_real_waiting()
    {
        var clock = new InstantClock();
        var firstChecks = 0;
        var secondChecks = 0;
        var builder = new GraphBuilder();
        var first = builder.Task("first", context => context.WaitUntilAsync(
            TimeSpan.FromHours(1),
            _ => Task.FromResult(++firstChecks == 4),
            "the first export to finish"));
        var second = builder.Task("second", context => context.WaitUntilAsync(
            TimeSpan.FromHours(2),
            _ => Task.FromResult(++secondChecks == 3),
            "the second export to finish"));
        second.Requires(first);

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, TimeProvider = clock });

        result.Outcome.ShouldBe(RunOutcome.Complete);
        firstChecks.ShouldBe(4);
        secondChecks.ShouldBe(3);
        clock.GetUtcNow().ShouldBe(_start.AddHours(7));
        result.Snapshot[second.Path].Settled.ShouldBe(_start.AddHours(7));
    }

    [Test]
    public async Task A_check_that_never_returns_true_fails_its_task_once_the_guard_is_passed_and_the_run_ends()
    {
        var clock = new InstantClock(TimeSpan.FromDays(1));
        var checks = 0;
        var builder = new GraphBuilder();
        var stuck = builder.Task("stuck", context => context.WaitUntilAsync(
            TimeSpan.FromHours(1),
            _ =>
            {
                checks++;
                return Task.FromResult(false);
            }));
        var other = builder.Task("other", _ => Task.CompletedTask);

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, TimeProvider = clock });

        result.Outcome.ShouldBe(RunOutcome.Incomplete);
        result.Snapshot[stuck.Path].Status.ShouldBe(NodeStatus.Failed);
        result.Snapshot[stuck.Path].Failure.ShouldBe(FailureKind.Unexpected);
        result.Snapshot[stuck.Path].Message!.ShouldContain("guard");
        result.Snapshot[other.Path].Status.ShouldBe(NodeStatus.Succeeded);
        checks.ShouldBe(26);
    }

    [Test]
    public async Task A_run_whose_clock_is_past_its_guard_at_the_end_drops_a_busy_reporter_at_once_and_returns_its_result()
    {
        var clock = new InstantClock(TimeSpan.FromDays(1));
        var builder = new GraphBuilder();
        var stuck = builder.Task("stuck", context => context.WaitUntilAsync(TimeSpan.FromHours(1), _ => Task.FromResult(false)));
        var reporter = new HeldUntilDroppedReporter();

        var result = await Runner.RunAsync(
            builder.Build(),
            new RunOptions { Journal = Journal.None, TimeProvider = clock, Reporters = [reporter] });

        result.Outcome.ShouldBe(RunOutcome.Incomplete);
        result.Snapshot[stuck.Path].Status.ShouldBe(NodeStatus.Failed);
        var error = result.ReporterErrors.ShouldHaveSingleItem();
        error.Reporter.ShouldBeSameAs(reporter);
        error.Exception.ShouldBeAssignableTo<OperationCanceledException>().InnerException.ShouldBeOfType<ClockGuardException>();
    }

    [Test]
    public async Task A_stop_on_a_clock_past_its_guard_ends_the_grace_period_at_once_and_lets_go_of_the_task_still_running()
    {
        var clock = new InstantClock(TimeSpan.FromDays(1));
        var pastTheGuard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverReleased = new TaskCompletionSource();
        var builder = new GraphBuilder();
        builder.Task("long", async context =>
        {
            await Task.Delay(TimeSpan.FromDays(2), context.TimeProvider);
            pastTheGuard.SetResult();
        });
        var stubborn = builder.Task("stubborn", async _ =>
        {
            started.SetResult();
            await neverReleased.Task;
        });
        var recorder = new RecordingReporter();
        var run = Runner.Start(
            builder.Build(),
            new RunOptions
            {
                Journal = Journal.None,
                TimeProvider = clock,
                Reporters = [recorder],
                ReporterWait = Timeout.InfiniteTimeSpan,
            });
        await Task.WhenAll(pastTheGuard.Task, started.Task).WaitAsync(TimeSpan.FromSeconds(30));

        run.Stop();
        var result = await run.Result.WaitAsync(TimeSpan.FromSeconds(30));

        result.Outcome.ShouldBe(RunOutcome.Stopped);
        result.ReporterErrors.ShouldBeEmpty();
        result.Snapshot[stubborn.Path].Status.ShouldBe(NodeStatus.Cancelled);
        result.Snapshot[stubborn.Path].Message.ShouldBe("still running when the run stopped waiting");
        recorder.Events.Where(e => e is StopRequested or GraceEnded).Select(e => e.GetType())
            .ShouldBe([typeof(StopRequested), typeof(GraceEnded)]);
    }

    [Test]
    public async Task A_run_with_no_reporter_ends_with_the_clock_where_its_last_task_left_it()
    {
        var clock = new InstantClock();
        var checks = 0;
        var builder = new GraphBuilder();
        builder.Task("wait", context => context.WaitUntilAsync(TimeSpan.FromHours(1), _ => Task.FromResult(++checks == 2)));

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, TimeProvider = clock });

        result.Snapshot.Ended.ShouldBe(_start.AddHours(1));
        clock.GetUtcNow().ShouldBe(_start.AddHours(1));
    }

    /// <summary>Handles every event at once but the one that says the run ended, whose call returns only once its token is cancelled.</summary>
    private sealed class HeldUntilDroppedReporter : IReporter
    {
        public async ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken)
        {
            if (runEvent is RunEnded)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
        }
    }
}
