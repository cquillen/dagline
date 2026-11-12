using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;

namespace Dagline.Testing.Tests;

public class TaskHarnessStopTests
{
    private const int _repeats = 50;

    [Test]
    public async Task A_task_that_waits_through_the_helper_is_stopped_at_its_wait_and_comes_back_cancelled()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            await using var harness = TaskHarness.For(context =>
                context.WaitUntil(TimeSpan.FromMinutes(5), _ => Task.FromResult(false), "the export to finish"));

            var outcome = await harness.RunUntilWaitingThenStopAsync();

            outcome.Status.ShouldBe(NodeStatus.Cancelled);
            outcome.Waited.ShouldBeTrue();
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
                    await context.WaitUntil(TimeSpan.FromMinutes(5), _ => Task.FromResult(false));
                }
                catch (OperationCanceledException)
                {
                    context.Warn("stopped while the export was still running");
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
    public async Task A_task_that_settles_without_waiting_comes_back_with_its_own_outcome()
    {
        await using var harness = TaskHarness.For(context =>
        {
            context.Message("nothing to wait for");
            return Task.CompletedTask;
        });

        var outcome = await harness.RunUntilWaitingThenStopAsync();

        outcome.Status.ShouldBe(NodeStatus.Succeeded);
        outcome.Message.ShouldBe("nothing to wait for");
        outcome.Waited.ShouldBeFalse();
    }

    [Test]
    public async Task With_no_clock_passed_the_run_is_on_a_clock_that_is_not_public_and_on_which_no_delay_ends()
    {
        TimeProvider? seen = null;
        await using var harness = TaskHarness.For(async context =>
        {
            seen = context.TimeProvider;
            await context.WaitUntil(TimeSpan.FromMilliseconds(1), _ => Task.FromResult(false));
        });

        var outcome = await harness.RunUntilWaitingThenStopAsync();

        outcome.Status.ShouldBe(NodeStatus.Cancelled);
        seen.ShouldNotBeNull();
        seen.ShouldNotBeOfType<InstantClock>();
        seen.GetType().IsVisible.ShouldBeFalse();
    }

    [Test]
    public async Task A_clock_passed_in_is_used_as_is()
    {
        var clock = new FakeTimeProvider();
        TimeProvider? seen = null;
        await using var harness = TaskHarness.For(
            async context =>
            {
                seen = context.TimeProvider;
                await context.WaitUntil(TimeSpan.FromMinutes(5), _ => Task.FromResult(false));
            },
            clock);

        var outcome = await harness.RunUntilWaitingThenStopAsync();

        outcome.Status.ShouldBe(NodeStatus.Cancelled);
        seen.ShouldBeSameAs(clock);
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
                        using (context.Waiting())
                        {
                            await Task.Delay(Timeout.InfiniteTimeSpan, context.TimeProvider, context.CancellationToken);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        for (var step = 0; step < 10; step++)
                        {
                            await Task.Delay(TimeSpan.FromHours(1), context.TimeProvider);
                        }

                        context.Message("cleaned up after the stop");
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
        var seen = new ConcurrentQueue<string>();
        await using var harness = TaskHarness.For(async context =>
        {
            seen.Enqueue(context.Checkpoint);
            await context.SaveCheckpointAsync("job-17");
            await context.WaitUntil(TimeSpan.FromMinutes(5), _ => Task.FromResult(context.Checkpoint == "job-17" && seen.Count == 2));
        });

        var first = await harness.RunUntilWaitingThenStopAsync();
        var second = await harness.RunAsync();

        first.Status.ShouldBe(NodeStatus.Cancelled);
        second.Status.ShouldBe(NodeStatus.Succeeded);
        seen.ShouldBe(["", "job-17"]);
    }
}
