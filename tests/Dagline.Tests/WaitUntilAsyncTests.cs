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
    public async Task Checks_are_one_interval_apart_from_the_end_of_one_check_to_the_start_of_the_next()
    {
        var fake = new FakeTimeProvider(_runStart);
        var clock = new WatchedClock(fake);
        var interval = TimeSpan.FromSeconds(30);
        var checkTakes = TimeSpan.FromSeconds(4);
        var starts = new List<DateTimeOffset>();
        var ends = new List<DateTimeOffset>();
        var builder = new GraphBuilder();
        builder.Task("job", context => context.WaitUntilAsync(
            interval,
            _ =>
            {
                starts.Add(fake.GetUtcNow());
                fake.Advance(checkTakes);
                ends.Add(fake.GetUtcNow());
                return Task.FromResult(starts.Count == 3);
            }));
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, TimeProvider = clock, Reporters = [reporter] });
        for (var delay = 0; delay < 2; delay++)
        {
            (await clock.NextTimerAsync()).ShouldBe(interval);
            fake.Advance(interval);
        }

        var result = await run;

        result.Outcome.ShouldBe(RunOutcome.Complete);
        starts.ShouldBe([_runStart, _runStart.AddSeconds(34), _runStart.AddSeconds(68)]);
        ends.ShouldBe([_runStart.AddSeconds(4), _runStart.AddSeconds(38), _runStart.AddSeconds(72)]);
        WaitFacts(reporter.Events.OfType<WaitingChanged>().Last())
            .ShouldBe((_runStart.AddSeconds(72), NodePath.Parse("job"), Waiting: false, null, Since: _runStart));
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
    public async Task The_task_waits_with_the_helpers_text_from_entry_until_the_helper_ends_and_the_check_gets_the_contexts_token()
    {
        var fake = new FakeTimeProvider(_runStart);
        var clock = new WatchedClock(fake);
        var interval = TimeSpan.FromSeconds(30);
        var tokens = new List<CancellationToken>();
        TaskContext? kept = null;
        NodeSnapshot? duringTheDelay = null;
        var builder = new GraphBuilder();
        var jobTask = builder.Task("job", async context =>
        {
            kept = context;
            await context.WaitUntilAsync(
                interval,
                token =>
                {
                    tokens.Add(token);
                    return Task.FromResult(tokens.Count == 2);
                },
                "job 17 to finish");
            context.SetMessage("job 17 finished");
        });
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, TimeProvider = clock, Reporters = [reporter] });
        var source = await reporter.Source;
        await clock.NextTimerAsync();
        duringTheDelay = source.Current[jobTask.Path];
        fake.Advance(interval);
        var result = await run;

        duringTheDelay.WaitingSince.ShouldBe(_runStart);
        duringTheDelay.WaitingText.ShouldBe("job 17 to finish");
        tokens.ShouldBe([kept!.CancellationToken, kept.CancellationToken]);
        result.Snapshot[jobTask.Path].WaitingSince.ShouldBeNull();
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
            .ShouldBe((_runStart.AddSeconds(30), jobTask.Path, Waiting: false, "job 17 to finish", Since: _runStart));
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
    public async Task Progress_set_inside_a_check_is_in_the_snapshot()
    {
        var fake = new FakeTimeProvider(_runStart);
        var clock = new WatchedClock(fake);
        var interval = TimeSpan.FromSeconds(30);
        var builder = new GraphBuilder();
        var jobTask = builder.Task("job", context => context.WaitUntilAsync(
            interval,
            _ =>
            {
                context.SetStatusText("job 17 is 40% done");
                context.SetCount(40, 100);
                return Task.FromResult(context.TimeProvider.GetUtcNow() > _runStart);
            },
            "job 17 to finish"));
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, TimeProvider = clock, Reporters = [reporter] });
        var source = await reporter.Source;
        await clock.NextTimerAsync();
        var waiting = source.Current[jobTask.Path];
        fake.Advance(interval);
        await run;

        waiting.WaitingText.ShouldBe("job 17 to finish");
        waiting.StatusText.ShouldBe("job 17 is 40% done");
        waiting.Count.ShouldBe(40);
        waiting.Total.ShouldBe(100);
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
            var stop = new StopSignal();
            var reporter = new RecordingReporter();

            var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, TimeProvider = clock, Reporters = [reporter] }, stop);
            await clock.NextTimerAsync();
            stop.Request();
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
            var stop = new StopSignal();
            var reporter = new RecordingReporter();

            var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] }, stop);
            await load.Started;
            stop.Request();
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
            var stop = new StopSignal();
            var reporter = new RecordingReporter();

            var run = Runner.RunAsync(
                builder.Build(),
                new RunOptions { Journal = Journal.None, GracePeriod = TimeSpan.Zero, Reporters = [reporter] },
                stop);
            await checking.Task;
            stop.Request();
            var result = await run;
            var source = await reporter.Source;
            var events = reporter.Events.Count;
            checkResult.SetResult(false);
            await ended.Task;

            reporter.Lines.Skip(4).ShouldBe(
            [
                "stop requested",
                "stopped waiting: grace period over",
                "waited job",
                "job: running -> cancelled",
                "run ended: stopped",
            ]);
            reporter.Events.Count.ShouldBe(events);
            source.Current[jobTask.Path].ShouldBe(result.Snapshot[jobTask.Path]);
            source.Current[jobTask.Path].WaitingSince.ShouldBeNull();
        }
    }

    [Test]
    public async Task A_hand_written_loop_inside_a_waiting_scope_is_marked_exactly_as_the_helper_is()
    {
        var interval = TimeSpan.FromSeconds(30);

        var (helperLines, helperWaits, helperJob) = await CheckTwice(context => context.WaitUntilAsync(
            interval,
            _ => Task.FromResult(context.TimeProvider.GetUtcNow() > _runStart),
            "job 17 to finish"));
        var (loopLines, loopWaits, loopJob) = await CheckTwice(async context =>
        {
            using (context.BeginWaiting("job 17 to finish"))
            {
                while (context.TimeProvider.GetUtcNow() == _runStart)
                {
                    await Task.Delay(interval, context.TimeProvider, context.CancellationToken);
                }
            }
        });

        loopLines.ShouldBe(helperLines);
        loopWaits.ShouldBe(helperWaits);
        loopJob.ShouldBe(helperJob);
        helperWaits.Count.ShouldBe(2);

        async Task<(IReadOnlyList<string> Lines, IReadOnlyList<WaitingChanged> Waits, string Job)> CheckTwice(TaskBody body)
        {
            var fake = new FakeTimeProvider(_runStart);
            var clock = new WatchedClock(fake);
            var builder = new GraphBuilder();
            var jobTask = builder.Task("job", body);
            var reporter = new RecordingReporter();

            var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, TimeProvider = clock, Reporters = [reporter] });
            await clock.NextTimerAsync();
            fake.Advance(interval);
            var result = await run;

            var job = result.Snapshot[jobTask.Path];
            return (reporter.Lines, [.. reporter.Events.OfType<WaitingChanged>()], $"{job.Status} {job.WaitingSince} {job.WaitingText} {job.Started:O} {job.Settled:O}");
        }
    }

    private static (DateTimeOffset At, NodePath Path, bool Waiting, string? Text, DateTimeOffset Since) WaitFacts(WaitingChanged wait)
        => (wait.At, wait.Path, wait.Waiting, wait.Text, wait.Since);
}
