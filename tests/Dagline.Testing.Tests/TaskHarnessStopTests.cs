using System.Collections.Concurrent;
using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace Dagline.Testing.Tests;

[NotInParallel(DiskHeavy.Key)]
public class TaskHarnessStopTests
{
    private const int _repeats = 50;

    [Test]
    public async Task A_task_that_waits_through_the_helper_is_stopped_at_its_wait_and_comes_back_cancelled()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            await using var harness = TaskHarness.For(context =>
                context.WaitUntilAsync(TimeSpan.FromMinutes(5), _ => Task.FromResult(false), "the export to finish"));

            var outcome = await harness.RunUntilWaitingThenStopAsync();

            outcome.Status.ShouldBe(NodeStatus.Cancelled);
            outcome.Waited.ShouldBeTrue();
        }
    }

    [Test]
    public async Task With_no_clock_passed_a_delay_before_the_wait_ends_at_once_and_the_task_is_stopped_at_its_wait()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            await using var harness = TaskHarness.For(async context =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), context.TimeProvider, context.CancellationToken);
                await context.WaitUntilAsync(TimeSpan.FromMinutes(5), _ => Task.FromResult(false), "the export to finish");
            });

            var outcome = await harness.RunUntilWaitingThenStopAsync();

            outcome.Status.ShouldBe(NodeStatus.Cancelled);
            outcome.Waited.ShouldBeTrue();
        }
    }

    [Test]
    public async Task With_no_clock_passed_a_retry_loop_around_the_trigger_reaches_the_wait_and_the_task_is_stopped_at_it()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var attempts = 0;
            await using var harness = TaskHarness.For(async context =>
            {
                while (++attempts < 3)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), context.TimeProvider, context.CancellationToken);
                }

                await context.WaitUntilAsync(TimeSpan.FromMinutes(5), _ => Task.FromResult(false), "the export to finish");
            });

            var outcome = await harness.RunUntilWaitingThenStopAsync();

            outcome.Status.ShouldBe(NodeStatus.Cancelled);
            outcome.Waited.ShouldBeTrue();
            attempts.ShouldBe(3);
        }
    }

    [Test]
    public async Task With_no_clock_passed_a_hand_written_wait_after_a_delay_is_stopped_at_its_wait()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            await using var harness = TaskHarness.For(async context =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), context.TimeProvider, context.CancellationToken);
                using (context.BeginWaiting("the export to finish"))
                {
                    while (true)
                    {
                        await Task.Delay(TimeSpan.FromMinutes(1), context.TimeProvider, context.CancellationToken);
                    }
                }
            });

            var outcome = await harness.RunUntilWaitingThenStopAsync();

            outcome.Status.ShouldBe(NodeStatus.Cancelled);
            outcome.Waited.ShouldBeTrue();
        }
    }

    [Test]
    public async Task With_no_clock_passed_a_delay_in_cleanup_after_the_stop_ends_and_the_task_returns_the_outcome_it_chose()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            await using var harness = TaskHarness.For(async context =>
            {
                try
                {
                    await context.WaitUntilAsync(TimeSpan.FromMinutes(5), _ => Task.FromResult(false));
                }
                catch (OperationCanceledException)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), context.TimeProvider);
                    throw context.Fail("stopped before the export finished");
                }
            });

            var outcome = await harness.RunUntilWaitingThenStopAsync();

            outcome.Status.ShouldBe(NodeStatus.Failed);
            outcome.Message.ShouldBe("stopped before the export finished");
        }
    }

    [Test]
    public async Task With_no_clock_passed_a_periodic_timer_made_before_the_wait_does_not_fire_while_the_task_waits()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var firings = 0;
            var firingsAfterWait = 0;
            await using var harness = TaskHarness.For(async context =>
            {
                var thirdFiring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var waitBegun = new ManualResetEventSlim();
                await using var timer = context.TimeProvider.CreateTimer(
                    _ =>
                    {
                        if (Interlocked.Increment(ref firings) == 3)
                        {
                            // The third firing is held under way across the start of the wait, so the next is decided during it.
                            thirdFiring.TrySetResult();
                            waitBegun.Wait();
                        }
                    },
                    null,
                    TimeSpan.FromMinutes(1),
                    TimeSpan.FromMinutes(1));
                await thirdFiring.Task;
                try
                {
                    using (context.BeginWaiting())
                    {
                        waitBegun.Set();
                        await Task.Delay(Timeout.InfiniteTimeSpan, context.TimeProvider, context.CancellationToken);
                    }
                }
                finally
                {
                    firingsAfterWait = Volatile.Read(ref firings);
                }
            });

            var outcome = await harness.RunUntilWaitingThenStopAsync();

            outcome.Status.ShouldBe(NodeStatus.Cancelled);
            firingsAfterWait.ShouldBe(3);
        }
    }

    [Test]
    public async Task With_no_clock_passed_a_timer_that_comes_due_after_the_wait_begins_does_not_fire()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var fired = false;
            var dueAfterTheWaitBegan = false;
            await using var harness = TaskHarness.For(async context =>
            {
                var due = context.TimeProvider.GetUtcNow() + TimeSpan.FromMinutes(1);
                await using var timer = context.TimeProvider.CreateTimer(
                    _ => Volatile.Write(ref fired, true),
                    null,
                    TimeSpan.FromMinutes(1),
                    Timeout.InfiniteTimeSpan);
                using (context.BeginWaiting())
                {
                    // The clock moves on as a firing comes due, before the firing decides whether to call back. Time not yet
                    // moved once the wait has begun means the firing decides while the task waits, so it must not call back.
                    dueAfterTheWaitBegan = context.TimeProvider.GetUtcNow() < due;
                    SpinWait.SpinUntil(() => context.TimeProvider.GetUtcNow() >= due);
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.TimeProvider, context.CancellationToken);
                }
            });

            var outcome = await harness.RunUntilWaitingThenStopAsync();

            outcome.Status.ShouldBe(NodeStatus.Cancelled);
            (dueAfterTheWaitBegan && Volatile.Read(ref fired)).ShouldBeFalse();
        }
    }

    [Test]
    public async Task With_no_clock_passed_a_timer_made_in_one_run_is_decided_by_that_run_and_not_by_a_later_one()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ITimer? fromTheFirstRun = null;
            await using var harness = TaskHarness.For(async context =>
            {
                if (fromTheFirstRun is null)
                {
                    fromTheFirstRun = context.TimeProvider.CreateTimer(
                        _ => fired.TrySetResult(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                    await context.WaitUntilAsync(TimeSpan.FromMinutes(5), _ => Task.FromResult(false));
                    return;
                }

                using (context.BeginWaiting())
                {
                    // The first run has ended, and its task is no longer waiting in it, so the timer fires.
                    fromTheFirstRun.Change(TimeSpan.FromMinutes(1), Timeout.InfiniteTimeSpan);
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.TimeProvider, context.CancellationToken);
                }
            });

            await harness.RunUntilWaitingThenStopAsync();
            var second = await harness.RunUntilWaitingThenStopAsync();

            second.Status.ShouldBe(NodeStatus.Cancelled);
            await fired.Task;
            await fromTheFirstRun!.DisposeAsync();
        }
    }

    [Test]
    public async Task With_no_clock_passed_a_body_that_delays_for_ever_without_waiting_throws_the_clock_guard()
    {
        await using var harness = TaskHarness.For(async context =>
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromHours(1), context.TimeProvider, context.CancellationToken);
            }
        });

        await Should.ThrowAsync<ClockGuardException>(() => harness.RunUntilWaitingThenStopAsync());
    }

    [Test]
    public async Task With_no_clock_passed_fake_time_carries_from_a_run_until_waiting_to_a_later_run()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var seen = new ConcurrentQueue<DateTimeOffset>();
            await using var harness = TaskHarness.For(async context =>
            {
                await Task.Delay(TimeSpan.FromHours(1), context.TimeProvider, context.CancellationToken);
                seen.Enqueue(context.TimeProvider.GetUtcNow());
                await context.WaitUntilAsync(TimeSpan.FromMinutes(5), _ => Task.FromResult(seen.Count == 2));
            });

            await harness.RunUntilWaitingThenStopAsync();
            await harness.RunAsync();

            var times = seen.ToArray();
            times.Length.ShouldBe(2);
            times[1].ShouldBeGreaterThan(times[0]);
        }
    }

    [Test]
    public async Task A_task_that_handles_the_stop_itself_comes_back_with_the_outcome_it_chose()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            await using var harness = TaskHarness.For(async context =>
            {
                try
                {
                    await context.WaitUntilAsync(TimeSpan.FromMinutes(5), _ => Task.FromResult(false));
                }
                catch (OperationCanceledException)
                {
                    context.AddWarning("stopped while the export was still running");
                    throw context.Fail("stopped before the export finished", "job 17 is still running");
                }
            });

            var outcome = await harness.RunUntilWaitingThenStopAsync();

            outcome.Status.ShouldBe(NodeStatus.Failed);
            outcome.Failure.ShouldBe(FailureKind.Reported);
            outcome.Message.ShouldBe("stopped before the export finished");
            outcome.Detail.ShouldBe(["job 17 is still running"]);
            outcome.Warnings.ShouldBe(["stopped while the export was still running"]);
            outcome.Waited.ShouldBeTrue();
        }
    }

    [Test]
    public async Task Cancelling_the_token_passed_to_a_run_stops_it_and_a_body_that_honours_its_token_returns_the_outcome_it_chose()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var harness = TaskHarness.For(async context =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.TimeProvider, context.CancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw context.Fail("stopped before the export finished");
                }
            });
            using var stop = new CancellationTokenSource();

            var running = harness.RunAsync(stop.Token);
            await started.Task;
            await stop.CancelAsync();
            var outcome = await running;

            outcome.Status.ShouldBe(NodeStatus.Failed);
            outcome.Failure.ShouldBe(FailureKind.Reported);
            outcome.Message.ShouldBe("stopped before the export finished");
        }
    }

    [Test]
    public async Task Cancelling_the_callers_token_stops_a_run_until_waiting_before_the_task_has_begun_waiting()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var harness = TaskHarness.For(async context =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, context.TimeProvider, context.CancellationToken);
                await context.WaitUntilAsync(TimeSpan.FromMinutes(5), _ => Task.FromResult(false));
            });
            using var stop = new CancellationTokenSource();

            var running = harness.RunUntilWaitingThenStopAsync(stop.Token);
            await started.Task;
            await stop.CancelAsync();
            var outcome = await running;

            outcome.Status.ShouldBe(NodeStatus.Cancelled);
            outcome.Waited.ShouldBeFalse();
        }
    }

    [Test]
    public async Task A_token_already_cancelled_runs_no_body_and_returns_the_task_as_it_was_left()
    {
        var bodyRan = false;
        await using var harness = TaskHarness.For(_ =>
        {
            bodyRan = true;
            return Task.CompletedTask;
        });
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();

        var outcome = await harness.RunAsync(stop.Token);

        outcome.Status.ShouldBe(NodeStatus.Ready);
        bodyRan.ShouldBeFalse();
    }

    [Test]
    public async Task A_task_that_settles_without_waiting_comes_back_with_its_own_outcome()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            await using var harness = TaskHarness.For(context =>
            {
                context.SetMessage("nothing to wait for");
                return Task.CompletedTask;
            });

            var outcome = await harness.RunUntilWaitingThenStopAsync();

            outcome.Status.ShouldBe(NodeStatus.Succeeded);
            outcome.Message.ShouldBe("nothing to wait for");
            outcome.Waited.ShouldBeFalse();
        }
    }

    [Test]
    public async Task With_no_clock_passed_the_run_is_on_a_clock_that_is_not_public_and_on_which_no_delay_ends_while_the_task_waits()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            TimeProvider? seen = null;
            var checks = 0;
            await using var harness = TaskHarness.For(async context =>
            {
                seen = context.TimeProvider;
                await context.WaitUntilAsync(TimeSpan.FromMilliseconds(1), _ => Task.FromResult(Interlocked.Increment(ref checks) > 1));
            });

            var outcome = await harness.RunUntilWaitingThenStopAsync();

            outcome.Status.ShouldBe(NodeStatus.Cancelled);
            outcome.Waited.ShouldBeTrue();
            checks.ShouldBe(1);
            seen.ShouldNotBeNull();
            seen.ShouldNotBeOfType<InstantClock>();
            seen.GetType().IsVisible.ShouldBeFalse();
        }
    }

    [Test]
    public async Task A_clock_passed_in_is_used_as_is()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var clock = new FakeTimeProvider();
            TimeProvider? seen = null;
            await using var harness = TaskHarness.For(
                async context =>
                {
                    seen = context.TimeProvider;
                    await context.WaitUntilAsync(TimeSpan.FromMinutes(5), _ => Task.FromResult(false));
                },
                clock);

            var outcome = await harness.RunUntilWaitingThenStopAsync();

            outcome.Status.ShouldBe(NodeStatus.Cancelled);
            seen.ShouldBeSameAs(clock);
        }
    }

    [Test]
    public async Task The_run_never_lets_go_of_a_task_that_takes_its_time_to_end_after_the_stop()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            // On an instant clock a limited grace period would end at once and the run would let go of the task.
            await using var harness = TaskHarness.For(
                async context =>
                {
                    try
                    {
                        using (context.BeginWaiting())
                        {
                            await Task.Delay(Timeout.InfiniteTimeSpan, context.TimeProvider, context.CancellationToken);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        for (var hour = 0; hour < 10; hour++)
                        {
                            await Task.Delay(TimeSpan.FromHours(1), context.TimeProvider);
                        }

                        context.SetMessage("cleaned up after the stop");
                        throw;
                    }
                },
                new InstantClock());

            var outcome = await harness.RunUntilWaitingThenStopAsync();

            outcome.Status.ShouldBe(NodeStatus.Cancelled);
            outcome.Message.ShouldBe("cleaned up after the stop");
        }
    }

    [Test]
    public async Task A_second_run_hands_back_the_checkpoint_saved_before_the_stop()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var seen = new ConcurrentQueue<string>();
            await using var harness = TaskHarness.For(async context =>
            {
                seen.Enqueue(context.Checkpoint);
                await context.SaveCheckpointAsync("job-17");
                await context.WaitUntilAsync(TimeSpan.FromMinutes(5), _ => Task.FromResult(context.Checkpoint == "job-17" && seen.Count == 2));
            });

            var first = await harness.RunUntilWaitingThenStopAsync();
            var second = await harness.RunAsync();

            first.Status.ShouldBe(NodeStatus.Cancelled);
            second.Status.ShouldBe(NodeStatus.Succeeded);
            seen.ShouldBe(["", "job-17"]);
        }
    }
}
