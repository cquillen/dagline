using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace Dagline.Tests;

public class WaitUntilAsyncTests
{
    private const int _repeats = 50;
    private static readonly DateTimeOffset _runStart = new(2026, 11, 11, 4, 12, 0, TimeSpan.Zero);

    [Test]
    public async Task The_helper_checks_at_once_and_returns_without_a_delay_when_the_check_is_true()
    {
        var clock = new FakeTimeProvider(_runStart);
        var checks = 0;
        var builder = new GraphBuilder();
        var jobTask = builder.Task("job", context => context.WaitUntilAsync(
            TimeSpan.FromSeconds(30),
            _ =>
            {
                checks++;
                return Task.FromResult(true);
            },
            "job 17 to finish"));
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, TimeProvider = clock, Reporters = [reporter] });

        checks.ShouldBe(1);
        result.Snapshot[jobTask.Path].Status.ShouldBe(NodeStatus.Succeeded);
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
        WaitFacts(reporter.Events.OfType<WaitingChanged>().Last())
            .ShouldBe((_runStart, jobTask.Path, Waiting: false, "job 17 to finish", Since: _runStart));
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task An_interval_of_zero_or_less_is_refused_at_the_call(int seconds)
    {
        var checks = 0;
        Exception? refused = null;
        var builder = new GraphBuilder();
        var jobTask = builder.Task("job", context =>
        {
            refused = Should.Throw<ArgumentOutOfRangeException>(() => context.WaitUntilAsync(
                TimeSpan.FromSeconds(seconds),
                _ =>
                {
                    checks++;
                    return Task.FromResult(true);
                },
                "job 17 to finish"));
            return Task.CompletedTask;
        });
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        refused.ShouldBeOfType<ArgumentOutOfRangeException>().ParamName.ShouldBe("interval");
        checks.ShouldBe(0);
        reporter.Events.OfType<WaitingChanged>().ShouldBeEmpty();
        result.Snapshot[jobTask.Path].Status.ShouldBe(NodeStatus.Succeeded);
    }

    [Test]
    public async Task A_check_that_throws_ends_the_wait_and_fails_the_task_as_an_unexpected_failure()
    {
        var builder = new GraphBuilder();
        var jobTask = builder.Task("job", context => context.WaitUntilAsync(
            TimeSpan.FromSeconds(30),
            _ => throw new InvalidOperationException("the job service is unreachable"),
            "job 17 to finish"));
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        var job = result.Snapshot[jobTask.Path];
        job.Status.ShouldBe(NodeStatus.Failed);
        job.Failure.ShouldBe(FailureKind.Unexpected);
        job.Message.ShouldBe("the job service is unreachable");
        reporter.Lines.ShouldBe(
        [
            "run started",
            "job: pending -> ready",
            "job: ready -> running",
            "waiting job: job 17 to finish",
            "waited job",
            "job: running -> failed",
            "run ended: incomplete",
        ]);
    }

    [Test]
    public async Task A_check_that_throws_what_fail_returns_fails_the_task_as_a_reported_failure()
    {
        var builder = new GraphBuilder();
        var jobTask = builder.Task("job", context => context.WaitUntilAsync(
            TimeSpan.FromSeconds(30),
            _ => throw context.Fail("job 17 ended in error", "step 3 of 5 failed"),
            "job 17 to finish"));
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        var job = result.Snapshot[jobTask.Path];
        job.Status.ShouldBe(NodeStatus.Failed);
        job.Failure.ShouldBe(FailureKind.Reported);
        job.Message.ShouldBe("job 17 ended in error");
        job.Detail.ShouldBe(["step 3 of 5 failed"]);
        reporter.Lines.Skip(3).ShouldBe(["waiting job: job 17 to finish", "waited job", "job: running -> failed", "run ended: incomplete"]);
    }

    [Test]
    public async Task A_check_that_catches_its_own_error_and_returns_false_keeps_the_wait_going()
    {
        var fake = new FakeTimeProvider(_runStart);
        var clock = new WatchedClock(fake);
        var interval = TimeSpan.FromSeconds(30);
        var checks = 0;
        var builder = new GraphBuilder();
        var jobTask = builder.Task("job", context => context.WaitUntilAsync(
            interval,
            _ =>
            {
                checks++;
                try
                {
                    if (checks == 1)
                    {
                        throw new HttpRequestException("connection reset");
                    }

                    return Task.FromResult(true);
                }
                catch (HttpRequestException exception)
                {
                    context.AddWarning($"check failed, trying again: {exception.Message}");
                    return Task.FromResult(false);
                }
            },
            "job 17 to finish"));
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, TimeProvider = clock, Reporters = [reporter] });
        await clock.NextTimerAsync();
        fake.Advance(interval);
        var result = await run;

        checks.ShouldBe(2);
        result.Snapshot[jobTask.Path].Status.ShouldBe(NodeStatus.Succeeded);
        reporter.Lines.Skip(3).ShouldBe(
        [
            "waiting job: job 17 to finish",
            "warning job: check failed, trying again: connection reset",
            "waited job",
            "job: running -> succeeded",
            "run ended: complete",
        ]);
    }

    [Test]
    public async Task A_stop_during_a_delay_ends_the_helper_at_once_and_the_task_is_cancelled_after_its_wait_ends()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var fake = new FakeTimeProvider(_runStart);
            var clock = new WatchedClock(fake);
            var checks = 0;
            var builder = new GraphBuilder();
            var jobTask = builder.Task("job", context => context.WaitUntilAsync(
                TimeSpan.FromSeconds(30),
                _ =>
                {
                    checks++;
                    return Task.FromResult(false);
                },
                "job 17 to finish"));
            using var stop = new CancellationTokenSource();
            var reporter = new RecordingReporter();

            var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, TimeProvider = clock, Reporters = [reporter] }, cancellationToken: stop.Token);
            await clock.NextTimerAsync();
            await stop.CancelAsync();
            var result = await run;

            checks.ShouldBe(1);
            result.Outcome.ShouldBe(RunOutcome.Stopped);
            result.Snapshot[jobTask.Path].Status.ShouldBe(NodeStatus.Cancelled);
            result.Snapshot[jobTask.Path].Settled.ShouldBe(_runStart);
            reporter.Lines.ShouldBe(
            [
                "run started",
                "job: pending -> ready",
                "job: ready -> running",
                "waiting job: job 17 to finish",
                "stop requested",
                "waited job",
                "job: running -> cancelled",
                "run ended: stopped",
            ]);
        }
    }

    [Test]
    public async Task A_helper_entered_after_a_stop_throws_without_calling_the_check_or_waiting()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var checks = 0;
            var load = new StoppedBody(_ => { });
            var builder = new GraphBuilder();
            var jobTask = builder.Task("job", async context =>
            {
                await load.Body(context);
                await context.WaitUntilAsync(
                    TimeSpan.FromSeconds(30),
                    _ =>
                    {
                        checks++;
                        return Task.FromResult(true);
                    },
                    "job 17 to finish");
            });
            using var stop = new CancellationTokenSource();
            var reporter = new RecordingReporter();

            var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] }, cancellationToken: stop.Token);
            await load.Started;
            await stop.CancelAsync();
            var result = await run;

            checks.ShouldBe(0);
            result.Snapshot[jobTask.Path].Status.ShouldBe(NodeStatus.Cancelled);
            reporter.Events.OfType<WaitingChanged>().ShouldBeEmpty();
        }
    }

    [Test]
    public async Task A_helper_let_go_mid_check_stops_waiting_before_it_is_cancelled_and_nothing_it_does_afterwards_shows()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var checkResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var builder = new GraphBuilder();
            var jobTask = builder.Task("job", async context =>
            {
                try
                {
                    await context.WaitUntilAsync(
                        TimeSpan.FromSeconds(30),
                        _ =>
                        {
                            checking.TrySetResult();
                            return checkResult.Task;
                        },
                        "job 17 to finish");
                }
                finally
                {
                    context.BeginWaiting("job 18 to finish").Dispose();
                    ended.TrySetResult();
                }
            });
            using var stop = new CancellationTokenSource();
            var reporter = new RecordingReporter();

            var run = Runner.RunAsync(
                builder.Build(),
                new RunOptions { Journal = Journal.None, GracePeriod = TimeSpan.Zero, Reporters = [reporter] },
                cancellationToken: stop.Token);
            await checking.Task;
            await stop.CancelAsync();
            var result = await run;
            var source = await reporter.Source;
            var events = reporter.Events.Count;
            checkResult.SetResult(false);
            await ended.Task;

            reporter.Lines.Skip(4).ShouldBe(
            [
                "stop requested",
                "grace period over",
                "waited job",
                "job: running -> cancelled",
                "run ended: stopped",
            ]);
            reporter.Events.Count.ShouldBe(events);
            source.Current[jobTask.Path].ShouldBe(result.Snapshot[jobTask.Path]);
            source.Current[jobTask.Path].WaitingSince.ShouldBeNull();
        }
    }

    private static (DateTimeOffset At, NodePath Path, bool Waiting, string? Text, DateTimeOffset Since) WaitFacts(WaitingChanged wait)
        => (wait.At, wait.Path, wait.Waiting, wait.Text, wait.Since);
}
