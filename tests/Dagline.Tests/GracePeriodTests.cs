using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace Dagline.Tests;

public class GracePeriodTests
{
    private const int _repeats = 50;
    private const string _leftBehind = "still running when the run stopped waiting";
    private static readonly DateTimeOffset _runStart = new(2026, 11, 11, 4, 12, 0, TimeSpan.Zero);

    [Test]
    public void The_grace_period_is_30_seconds_by_default()
    {
        new RunOptions { Journal = Journal.None }.GracePeriod.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task A_reporter_that_hears_the_stop_finds_the_grace_period_already_running_on_the_runs_clock()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var fake = new FakeTimeProvider(_runStart);
            var clock = new WatchedClock(fake);
            var slow = new PausedBody();
            var builder = new GraphBuilder();
            builder.Task("slow", slow.Body);
            using var stop = new CancellationTokenSource();
            var listener = new StopListener(clock);

            var run = Runner.RunAsync(
                builder.Build(),
                new RunOptions { Journal = Journal.None, TimeProvider = clock, Reporters = [listener] },
                cancellationToken: stop.Token);
            await slow.Started;
            await stop.CancelAsync();
            var gracePeriod = await listener.PendingTimerWhenHeard;
            fake.Advance(TimeSpan.FromSeconds(30));
            var result = await run;

            // Moving the clock on as soon as the stop is heard must move it from the stop: the grace period ends 30 seconds after it.
            gracePeriod.ShouldBe(TimeSpan.FromSeconds(30));
            result.Outcome.ShouldBe(RunOutcome.Stopped);
            result.Snapshot.Ended.ShouldBe(_runStart.AddSeconds(30));
            slow.Release();
        }
    }

    [Test]
    public async Task When_the_grace_period_runs_out_the_run_lets_go_of_its_running_tasks_in_declaration_order()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var clock = new FakeTimeProvider(_runStart);
            var slow = new PausedBody();
            var honour = new StoppedBody();
            var stuck = new PausedBody();
            var builder = new GraphBuilder();
            var slowTask = builder.Task("slow", slow.Body);
            builder.Task("honour", honour.Body);
            var stuckTask = builder.Task("stuck", stuck.Body);
            var tidy = builder.Task("tidy", _ => Task.CompletedTask).After(slowTask);
            using var stop = new CancellationTokenSource();
            var reporter = new RecordingReporter();
            var watched = new WatchedReporter(reporter);

            var run = Runner.RunAsync(
                builder.Build(),
                new RunOptions { Journal = Journal.None, TimeProvider = clock, Reporters = [watched] },
                cancellationToken: stop.Token);
            await slow.Started;
            await honour.Started;
            await stuck.Started;
            clock.Advance(TimeSpan.FromSeconds(5));
            await stop.CancelAsync();
            await watched.Handled("honour: running -> cancelled");
            // The grace period's timer exists once the stop is heard. Moving the clock to its end in one step keeps it there while the
            // run lets go, so the grace end's time is the end of the grace period on either engine.
            clock.Advance(TimeSpan.FromSeconds(30));
            var result = await run;

            result.Outcome.ShouldBe(RunOutcome.Stopped);
            reporter.Lines.Skip(7).ShouldBe(
            [
                "stop requested",
                "honour: running -> cancelled",
                "grace period over",
                "slow: running -> cancelled",
                "stuck: running -> cancelled",
                "tidy: pending -> ready",
                "run ended: stopped",
            ]);
            var requested = reporter.Events.OfType<StopRequested>().Single();
            (requested.At, requested.By, requested.GracePeriod).ShouldBe((_runStart.AddSeconds(5), StopRequester.App, TimeSpan.FromSeconds(30)));
            var graceEnded = reporter.Events.OfType<GraceEnded>().Single();
            graceEnded.At.ShouldBe(_runStart.AddSeconds(35));
            result.Snapshot[slowTask.Path].Message.ShouldBe(_leftBehind);
            result.Snapshot[stuckTask.Path].Settled.ShouldBe(_runStart.AddSeconds(35));
            result.Snapshot[tidy.Path].Status.ShouldBe(NodeStatus.Ready);
            result.Snapshot.RunningTasks.InUse.ShouldBe(0);
            (await slow.Started).CancellationToken.IsCancellationRequested.ShouldBeTrue();
            slow.Release();
            stuck.Release();
        }
    }

    [Test]
    public async Task The_wait_ends_when_the_last_running_task_ends_before_the_grace_period_is_over()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var clock = new FakeTimeProvider(_runStart);
            var load = new StoppedBody();
            var builder = new GraphBuilder();
            builder.Task("load", load.Body);
            using var stop = new CancellationTokenSource();
            var reporter = new RecordingReporter();

            var run = Runner.RunAsync(
                builder.Build(),
                new RunOptions { Journal = Journal.None, TimeProvider = clock, Reporters = [reporter] },
                cancellationToken: stop.Token);
            await load.Started;
            await stop.CancelAsync();
            var result = await run;

            result.Outcome.ShouldBe(RunOutcome.Stopped);
            reporter.Events.OfType<GraceEnded>().ShouldBeEmpty();
            reporter.Lines[^1].ShouldBe("run ended: stopped");
            result.Snapshot.Ended.ShouldBe(_runStart);
        }
    }

    [Test]
    public async Task Neither_a_drain_nor_cancelling_the_token_again_ends_the_wait_before_the_grace_period_runs_out()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var clock = new FakeTimeProvider(_runStart);
            var load = new PausedBody();
            var builder = new GraphBuilder();
            var loadTask = builder.Task("load", load.Body);
            using var stop = new CancellationTokenSource();
            var reporter = new RecordingReporter();
            var watched = new WatchedReporter(reporter);

            var run = Runner.Start(
                builder.Build(),
                new RunOptions { Journal = Journal.None, TimeProvider = clock, Reporters = [watched] },
                stop.Token);
            await load.Started;
            await stop.CancelAsync();
            await watched.Handled("stop requested");
            run.Drain();
            await stop.CancelAsync();
            // The grace period's timer exists once the stop is heard. Moving the clock to its end in one step keeps it there while the
            // run lets go, so the grace end's time is the end of the grace period on either engine.
            clock.Advance(TimeSpan.FromSeconds(30));
            var result = await run.Result;
            load.Release();

            result.Outcome.ShouldBe(RunOutcome.Stopped);
            reporter.Lines.ShouldBe(
            [
                "run started",
                "load: pending -> ready",
                "load: ready -> running",
                "stop requested",
                "grace period over",
                "load: running -> cancelled",
                "run ended: stopped",
            ]);
            reporter.Events.OfType<GraceEnded>().Single().At.ShouldBe(_runStart.AddSeconds(30));
            result.Snapshot[loadTask.Path].Message.ShouldBe(_leftBehind);
        }
    }

    [Test]
    public async Task A_grace_period_of_zero_lets_go_at_once()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var load = new PausedBody();
            var builder = new GraphBuilder();
            builder.Task("load", load.Body);
            using var stop = new CancellationTokenSource();
            var reporter = new RecordingReporter();

            var run = Runner.RunAsync(
                builder.Build(),
                new RunOptions { Journal = Journal.None, GracePeriod = TimeSpan.Zero, Reporters = [reporter] },
                cancellationToken: stop.Token);
            await load.Started;
            await stop.CancelAsync();
            var result = await run;

            reporter.Lines.Skip(3).ShouldBe(
            [
                "stop requested",
                "grace period over",
                "load: running -> cancelled",
                "run ended: stopped",
            ]);
            var requested = reporter.Events.OfType<StopRequested>().Single();
            reporter.Events.OfType<GraceEnded>().Single().At.ShouldBe(requested.At);
            StopFacts(result.Snapshot).ShouldBe((requested.At, TimeSpan.Zero, requested.At, StopRequester.App));
            load.Release();
        }
    }

    [Test]
    public async Task An_unlimited_grace_period_waits_until_the_bodies_end()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var clock = new FakeTimeProvider(_runStart);
            var load = new PausedBody();
            var builder = new GraphBuilder();
            var loadTask = builder.Task("load", load.Body);
            using var stop = new CancellationTokenSource();
            var reporter = new RecordingReporter();
            var watched = new WatchedReporter(reporter);

            var run = Runner.RunAsync(
                builder.Build(),
                new RunOptions { Journal = Journal.None, TimeProvider = clock, GracePeriod = Timeout.InfiniteTimeSpan, Reporters = [watched] },
                cancellationToken: stop.Token);
            await load.Started;
            await stop.CancelAsync();
            await watched.Handled("stop requested");
            clock.Advance(TimeSpan.FromDays(1));
            load.Release();
            var result = await run;

            reporter.Events.OfType<GraceEnded>().ShouldBeEmpty();
            result.Snapshot[loadTask.Path].Status.ShouldBe(NodeStatus.Succeeded);
            StopFacts(result.Snapshot).ShouldBe((_runStart, Timeout.InfiniteTimeSpan, Deadline: null, StopRequester.App));
            reporter.Events.OfType<StopRequested>().Single().GracePeriod.ShouldBe(Timeout.InfiniteTimeSpan);
        }
    }


    [Test]
    public async Task The_snapshot_carries_the_grace_period_and_the_deadline()
    {
        var clock = new FakeTimeProvider(_runStart);
        var load = new StoppedBody();
        var builder = new GraphBuilder();
        builder.Task("load", load.Body);
        using var stop = new CancellationTokenSource();
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(
            builder.Build(),
            new RunOptions { Journal = Journal.None, TimeProvider = clock, GracePeriod = TimeSpan.FromMinutes(2), Reporters = [reporter] },
            cancellationToken: stop.Token);
        await load.Started;
        clock.Advance(TimeSpan.FromSeconds(10));
        await stop.CancelAsync();
        var result = await run;

        var requestedAt = _runStart.AddSeconds(10);
        StopFacts(result.Snapshot).ShouldBe((requestedAt, TimeSpan.FromMinutes(2), requestedAt.AddMinutes(2), StopRequester.App));
    }

    [Test]
    [Arguments(-2)]
    [Arguments(-60_000)]
    public void A_negative_grace_period_is_one_more_problem_refused_before_the_run_exists(int milliseconds)
    {
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask);
        var reporter = new RecordingReporter();
        var gracePeriod = TimeSpan.FromMilliseconds(milliseconds);
        var options = new RunOptions { Journal = Journal.None, MaxRunningTasks = 0, GracePeriod = gracePeriod, Reporters = [reporter] };

        var exception = Should.Throw<RunConfigurationException>(() => { _ = Runner.RunAsync(builder.Build(), options); });

        exception.Message.Split(Environment.NewLine).ShouldBe(
        [
            "The options cannot be used to run the graph:",
            "- MaxRunningTasks is 0. It must be at least 1.",
            $"- The grace period is {gracePeriod}. It must be zero or more, or Timeout.InfiniteTimeSpan to wait with no limit.",
        ]);
        reporter.Events.ShouldBeEmpty();
    }

    [Test]
    [Arguments(42_949_672_940_001L)]
    [Arguments(long.MaxValue)]
    public void A_grace_period_beyond_the_timer_maximum_is_one_more_problem_refused_before_the_run_exists(long ticks)
    {
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask);
        var reporter = new RecordingReporter();
        var gracePeriod = TimeSpan.FromTicks(ticks);
        var options = new RunOptions { Journal = Journal.None, MaxRunningTasks = 0, GracePeriod = gracePeriod, Reporters = [reporter] };

        var exception = Should.Throw<RunConfigurationException>(() => { _ = Runner.RunAsync(builder.Build(), options); });

        exception.Message.Split(Environment.NewLine).ShouldBe(
        [
            "The options cannot be used to run the graph:",
            "- MaxRunningTasks is 0. It must be at least 1.",
            $"- The grace period is {gracePeriod}. It must be at most 49.17:02:47.2940000, about 49.7 days, or Timeout.InfiniteTimeSpan to wait with no limit.",
        ]);
        reporter.Events.ShouldBeEmpty();
    }

    [Test]
    public async Task A_grace_period_at_the_timer_maximum_is_allowed_on_the_system_clock()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var load = new StoppedBody();
            var builder = new GraphBuilder();
            builder.Task("load", load.Body);
            using var stop = new CancellationTokenSource();
            var gracePeriod = TimeSpan.FromMilliseconds(4_294_967_294L);

            var run = Runner.RunAsync(
                builder.Build(),
                new RunOptions { Journal = Journal.None, GracePeriod = gracePeriod },
                cancellationToken: stop.Token);
            await load.Started;
            await stop.CancelAsync();
            var result = await run;

            result.Outcome.ShouldBe(RunOutcome.Stopped);
            result.Snapshot.Stop!.GracePeriod.ShouldBe(gracePeriod);
        }
    }

    [Test]
    public async Task A_task_left_behind_keeps_its_warnings_detail_and_frozen_progress_and_its_message_is_replaced()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var load = new PausedBody();
            var builder = new GraphBuilder();
            var loadTask = builder.Task("load", async context =>
            {
                context.SetStatusText("loading entities");
                context.SetCount(40, 100);
                context.AddWarning("entity 9 has no region");
                context.SetMessage("loaded 40 of 100 entities");
                context.AddDetail("stopped at entity 41");
                await load.Body(context);
            });
            using var stop = new CancellationTokenSource();

            var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, GracePeriod = TimeSpan.Zero }, cancellationToken: stop.Token);
            await load.Started;
            await stop.CancelAsync();
            var result = await run;

            var leftBehind = result.Snapshot[loadTask.Path];
            leftBehind.Status.ShouldBe(NodeStatus.Cancelled);
            leftBehind.Message.ShouldBe(_leftBehind);
            leftBehind.StatusText.ShouldBe("loading entities");
            leftBehind.Count.ShouldBe(40);
            leftBehind.Total.ShouldBe(100);
            leftBehind.Warnings.ShouldBe(["entity 9 has no region"]);
            leftBehind.Detail.ShouldBe(["stopped at entity 41"]);
            leftBehind.Failure.ShouldBeNull();
            load.Release();
        }
    }

    [Test]
    public async Task A_task_left_behind_after_it_reported_a_failure_is_cancelled_and_blocks_nothing()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var load = new PausedBody();
            var builder = new GraphBuilder();
            var loadTask = builder.Task("load", async context =>
            {
                _ = context.Fail("2 bad entities", "entity 3: missing owner");
                await load.Body(context);
            });
            var publishTask = builder.Task("publish", _ => Task.CompletedTask).Requires(loadTask);
            using var stop = new CancellationTokenSource();

            var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, GracePeriod = TimeSpan.Zero }, cancellationToken: stop.Token);
            await load.Started;
            await stop.CancelAsync();
            var result = await run;

            var leftBehind = result.Snapshot[loadTask.Path];
            leftBehind.Status.ShouldBe(NodeStatus.Cancelled);
            leftBehind.Failure.ShouldBeNull();
            leftBehind.Message.ShouldBe(_leftBehind);
            leftBehind.Detail.ShouldBe(["entity 3: missing owner"]);
            result.Snapshot[publishTask.Path].Status.ShouldBe(NodeStatus.Pending);
            result.Outcome.ShouldBe(RunOutcome.Stopped);
            load.Release();
        }
    }

    [Test]
    public async Task A_task_left_behind_with_a_waiting_scope_open_stops_waiting_immediately_before_it_is_cancelled()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var poll = new PausedBody();
            var builder = new GraphBuilder();
            builder.Task("job", async context =>
            {
                using (context.BeginWaiting("job 17 to finish"))
                {
                    await poll.Body(context);
                }
            });
            using var stop = new CancellationTokenSource();
            var reporter = new RecordingReporter();

            var run = Runner.RunAsync(
                builder.Build(),
                new RunOptions { Journal = Journal.None, GracePeriod = TimeSpan.Zero, Reporters = [reporter] },
                cancellationToken: stop.Token);
            await poll.Started;
            await stop.CancelAsync();
            await run;

            reporter.Lines.Skip(4).ShouldBe(
            [
                "stop requested",
                "grace period over",
                "waited job",
                "job: running -> cancelled",
                "run ended: stopped",
            ]);
            poll.Release();
        }
    }

    [Test]
    public async Task What_a_task_left_behind_reports_does_nothing_and_its_context_still_answers()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var clock = new FakeTimeProvider(_runStart);
            var load = new PausedBody();
            var builder = new GraphBuilder();
            var prepare = builder.Task("prepare", _ => Task.CompletedTask);
            var loadTask = builder.Task("load", load.Body).Requires(prepare);
            using var stop = new CancellationTokenSource();
            var reporter = new RecordingReporter();

            var run = Runner.RunAsync(
                builder.Build(),
                new RunOptions { Journal = Journal.None, TimeProvider = clock, GracePeriod = TimeSpan.Zero, Reporters = [reporter] },
                cancellationToken: stop.Token);
            var context = await load.Started;
            await stop.CancelAsync();
            var result = await run;
            var source = await reporter.Source;
            var events = reporter.Events.Count;

            context.SetStatusText("still going");
            context.SetCount(99, 100);
            context.AddWarning("late warning");
            context.SetMessage("late message");
            context.AddDetail("late detail");
            _ = context.Fail("late failure");
            context.BeginWaiting("late wait").Dispose();

            reporter.Events.Count.ShouldBe(events);
            source.Current[loadTask.Path].ShouldBe(result.Snapshot[loadTask.Path]);
            context.StatusOf(prepare).ShouldBe(NodeStatus.Succeeded);
            context.Path.ShouldBe(loadTask.Path);
            context.TimeProvider.ShouldBeSameAs(clock);
            context.CancellationToken.IsCancellationRequested.ShouldBeTrue();
            load.Release();
        }
    }

    [Test]
    public async Task The_token_of_a_task_let_go_is_signalled_by_the_time_the_run_returns_without_waiting_for_its_callbacks()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            using var callbackMayEnd = new ManualResetEventSlim();
            var load = new PausedBody();
            var builder = new GraphBuilder();
            builder.Task("load", async context =>
            {
                using var blocking = context.CancellationToken.Register(() => callbackMayEnd.Wait(TimeSpan.FromSeconds(30)));
                await load.Body(context);
            });
            using var stop = new CancellationTokenSource();

            var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, GracePeriod = TimeSpan.Zero }, cancellationToken: stop.Token);
            var context = await load.Started;
            await stop.CancelAsync();
            var result = await run;
            var signalled = context.CancellationToken.IsCancellationRequested;
            callbackMayEnd.Set();

            result.Outcome.ShouldBe(RunOutcome.Stopped);
            signalled.ShouldBeTrue();
            load.Release();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_body_let_go_that_ends_later_changes_nothing_and_raises_nothing(bool throws)
    {
        var unobserved = new List<Exception>();
        TaskScheduler.UnobservedTaskException += Record;
        try
        {
            for (var repeat = 0; repeat < _repeats; repeat++)
            {
                var load = new PausedBody();
                var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var builder = new GraphBuilder();
                var loadTask = builder.Task("load", async context =>
                {
                    try
                    {
                        await load.Body(context);
                    }
                    finally
                    {
                        ended.TrySetResult();
                    }
                });
                using var stop = new CancellationTokenSource();
                var reporter = new RecordingReporter();

                var run = Runner.RunAsync(
                    builder.Build(),
                    new RunOptions { Journal = Journal.None, GracePeriod = TimeSpan.Zero, Reporters = [reporter] },
                    cancellationToken: stop.Token);
                await load.Started;
                await stop.CancelAsync();
                var result = await run;
                var source = await reporter.Source;
                var events = reporter.Events.Count;

                if (throws)
                {
                    load.Throw(new InvalidOperationException("the store went away"));
                }
                else
                {
                    load.Release();
                }

                await ended.Task;
                await Task.Yield();

                reporter.Events.Count.ShouldBe(events);
                source.Current[loadTask.Path].ShouldBe(result.Snapshot[loadTask.Path]);
                source.Current[loadTask.Path].Status.ShouldBe(NodeStatus.Cancelled);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            unobserved.ShouldBeEmpty();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Record;
        }

        void Record(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            lock (unobserved)
            {
                unobserved.Add(args.Exception);
            }
        }
    }

    private static (DateTimeOffset RequestedAt, TimeSpan GracePeriod, DateTimeOffset? Deadline, StopRequester By) StopFacts(Snapshot snapshot)
    {
        var stop = snapshot.Stop.ShouldNotBeNull();
        return (stop.RequestedAt, stop.GracePeriod, stop.Deadline, stop.By);
    }

    /// <summary>
    /// A reporter that looks, as it hears <see cref="StopRequested"/>, for a timer already created on the run's clock, and says
    /// the due time of the one it finds, or <see langword="null"/> when there is none yet.
    /// </summary>
    private sealed class StopListener(WatchedClock clock) : IReporter
    {
        private readonly TaskCompletionSource<TimeSpan?> _heard = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TimeSpan?> PendingTimerWhenHeard => _heard.Task;

        public ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken)
        {
            if (runEvent is StopRequested)
            {
                _heard.TrySetResult(clock.TryTakeTimer(out var dueTime) ? dueTime : null);
            }

            return ValueTask.CompletedTask;
        }
    }
}
