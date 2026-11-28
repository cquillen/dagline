namespace Dagline.Prototype.Tests;

/// <summary>
/// Dagline.Testing's RunUntilWaitingThenStopAsync, rebuilt as "pump until the wait is decided, then stop": no clock that reads the
/// live snapshot to decide each timer, only a clock the pump fires and a condition on what the loop has applied.
/// </summary>
public class HarnessTests
{
    private static readonly NodePath _task = NodePath.Parse("task");

    private sealed record Outcome(ProtoResult Result, bool Waited, string? WaitingText, TimeSpan TimePassed, bool NeededRealTime);

    private static Outcome RunUntilWaitingThenStop(ProtoBody body)
    {
        var host = new ManualHost();
        var clock = new PumpedClock();
        var started = clock.GetUtcNow();
        var reporter = new Recorder();
        var bodies = new ProtoBodies();
        var graph = new GraphBuilder();
        graph.Task("task", bodies.Of(body));
        var run = ProtoRun.Start(
            graph.Build(),
            bodies,
            new ProtoOptions { TimeProvider = clock, GracePeriod = Timeout.InfiniteTimeSpan, Reporters = [reporter] },
            host);
        var neededRealTime = false;

        // Until the wait is decided, time is instant: whenever nothing is left to pump, the next timer fires.
        bool Decided() => run.Result.IsCompleted || run.Current[_task].IsWaiting;
        while (true)
        {
            host.PumpUntilIdle();
            if (Decided())
            {
                break;
            }

            if (!clock.FireNext())
            {
                // Nothing queued and no timer: the body is somewhere the host cannot see, so only real time can tell.
                neededRealTime = true;
                host.PumpUntil(() => Decided() || clock.HasTimers, TimeSpan.FromSeconds(10)).ShouldBeTrue("the body neither waited nor ended");
            }
        }

        // From the stop on no timer fires: the stop ends the delay the body is in, and time stays where the wait began.
        run.Stop();
        host.PumpUntilIdle();
        if (!run.Result.IsCompleted)
        {
            neededRealTime = true;
            host.PumpUntil(() => run.Result.IsCompleted, TimeSpan.FromSeconds(10)).ShouldBeTrue("the body ignored the stop");
        }

        var began = reporter.Events.OfType<WaitingChanged>().FirstOrDefault(waiting => waiting.Waiting);
        return new Outcome(run.Result.Result, began is not null, began?.Text, clock.GetUtcNow() - started, neededRealTime);
    }

    [Test]
    public void A_body_that_delays_then_waits_is_stopped_at_its_wait_after_one_check()
    {
        var checks = 0;
        var stoppedOn = 0;

        var outcome = RunUntilWaitingThenStop(async context =>
        {
            await Task.Delay(TimeSpan.FromHours(1), context.TimeProvider, context.CancellationToken);
            try
            {
                await context.WaitUntilOnTheCallersContextAsync(
                    TimeSpan.FromSeconds(30),
                    _ =>
                    {
                        checks++;
                        return Task.FromResult(false);
                    },
                    "external job");
            }
            finally
            {
                stoppedOn = Environment.CurrentManagedThreadId;
            }
        });

        outcome.Waited.ShouldBeTrue();
        outcome.WaitingText.ShouldBe("external job");
        checks.ShouldBe(1);
        outcome.TimePassed.ShouldBe(TimeSpan.FromHours(1));
        outcome.Result.Outcome.ShouldBe(RunOutcome.Stopped);
        outcome.Result.Snapshot[_task].Status.ShouldBe(NodeStatus.Cancelled);

        // Every await in the body captured the host's context, so the whole run was one script on this thread.
        outcome.NeededRealTime.ShouldBeFalse();
        stoppedOn.ShouldBe(Environment.CurrentManagedThreadId);
    }

    [Test]
    public void The_same_body_on_Daglines_own_helper_leaves_the_host_at_the_stop_and_needs_real_time()
    {
        var checks = 0;
        var stoppedOn = 0;

        var outcome = RunUntilWaitingThenStop(async context =>
        {
            await Task.Delay(TimeSpan.FromHours(1), context.TimeProvider, context.CancellationToken);
            try
            {
                // As TaskContext.WaitUntilAsync is written today: ConfigureAwait(false) on the delay.
                await context.WaitUntilAsync(
                    TimeSpan.FromSeconds(30),
                    _ =>
                    {
                        checks++;
                        return Task.FromResult(false);
                    },
                    "external job");
            }
            finally
            {
                stoppedOn = Environment.CurrentManagedThreadId;
            }
        });

        // The outcome is the same and still exact...
        outcome.Waited.ShouldBeTrue();
        checks.ShouldBe(1);
        outcome.TimePassed.ShouldBe(TimeSpan.FromHours(1));
        outcome.Result.Snapshot[_task].Status.ShouldBe(NodeStatus.Cancelled);

        // ...but the runtime resumes a cancelled Task.Delay awaited with ConfigureAwait(false) on the thread pool, whoever cancels.
        // The helper's own tail ran there. The body's code came back to this thread, since its own await captured the host's
        // context, but only once the pool had got to it: the host was idle meanwhile, and the pump had to wait in real time.
        outcome.NeededRealTime.ShouldBeTrue();
        stoppedOn.ShouldBe(Environment.CurrentManagedThreadId);
    }

    [Test]
    public void A_body_that_never_waits_runs_to_its_end_with_every_delay_instant()
    {
        var outcome = RunUntilWaitingThenStop(async context =>
        {
            await Task.Delay(TimeSpan.FromHours(2), context.TimeProvider, context.CancellationToken);
            await Task.Delay(TimeSpan.FromHours(3), context.TimeProvider, context.CancellationToken);
        });

        outcome.Waited.ShouldBeFalse();
        outcome.TimePassed.ShouldBe(TimeSpan.FromHours(5));
        outcome.Result.Snapshot[_task].Status.ShouldBe(NodeStatus.Succeeded);
        outcome.NeededRealTime.ShouldBeFalse();
    }

    [Test]
    public void A_wait_is_not_in_the_current_snapshot_when_the_call_that_began_it_returns()
    {
        // Why the harness's present clock cannot be kept: it decides each timer by asking the live snapshot whether the task is
        // waiting, at the moment the body makes the timer. Under one loop that answer is still "no" then.
        var scenario = new Scenario();
        var gate = new Gate();
        bool? waitingWhenBeginReturned = null;
        scenario.Task("task", async context =>
        {
            using (context.BeginWaiting("external job"))
            {
                waitingWhenBeginReturned = scenario.Run.Current[_task].IsWaiting;
                await gate.Task;
            }
        });
        scenario.Start();

        scenario.Host.PumpLoop();
        scenario.Host.PumpBodies();
        waitingWhenBeginReturned.ShouldBe(false);

        scenario.Host.PumpLoop();
        scenario.Run.Current[_task].IsWaiting.ShouldBeTrue();
    }
}
