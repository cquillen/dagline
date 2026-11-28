namespace Dagline.Testing.Tests;

public class ScriptedRunTests
{
    private static readonly TimeSpan _limit = TimeSpan.FromSeconds(10);

    private static readonly DateTimeOffset _start = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public void Nothing_of_the_run_happens_until_the_test_pumps_it()
    {
        var ran = false;
        var builder = new GraphBuilder();
        var task = builder.Task("task", _ =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        var scripted = ScriptedRun.Start(builder.Build(), new RunOptions { Journal = Journal.None });

        ran.ShouldBeFalse();
        scripted.Run.Current[task.Path].Status.ShouldBe(NodeStatus.Pending);
        scripted.Pump();
        ran.ShouldBeTrue();
        scripted.Run.Result.IsCompleted.ShouldBeTrue();
    }

    [Test]
    public void A_body_that_ends_before_the_stop_succeeds_and_one_that_ends_after_it_is_cancelled()
    {
        var firstEnds = new TaskCompletionSource();
        var secondEnds = new TaskCompletionSource();
        var builder = new GraphBuilder();
        var first = builder.Task("first", async _ => await firstEnds.Task);
        var second = builder.Task("second", async context =>
        {
            await secondEnds.Task;
            context.CancellationToken.ThrowIfCancellationRequested();
        });
        var after = builder.Task("after", _ => Task.CompletedTask);
        after.Requires(first);

        var scripted = ScriptedRun.Start(builder.Build(), new RunOptions { Journal = Journal.None });
        scripted.Pump();
        scripted.Run.Current[first.Path].Status.ShouldBe(NodeStatus.Running);
        scripted.Run.Current[second.Path].Status.ShouldBe(NodeStatus.Running);

        // The first body's end is posted before the stop, so its settle starts the task after it before the stop is applied; the
        // second body ends only once the stop has been applied.
        firstEnds.SetResult();
        scripted.PumpBodies();
        scripted.Run.Stop();
        scripted.PumpLoop();
        scripted.Run.Current[first.Path].Status.ShouldBe(NodeStatus.Succeeded);
        scripted.Run.Current[after.Path].Status.ShouldBe(NodeStatus.Running);
        scripted.Run.Current.Stop.ShouldNotBeNull();

        secondEnds.SetResult();
        var result = scripted.Finish(_limit);

        result.Outcome.ShouldBe(RunOutcome.Stopped);
        result.Snapshot[second.Path].Status.ShouldBe(NodeStatus.Cancelled);
        result.Snapshot[after.Path].Status.ShouldBe(NodeStatus.Succeeded);
    }

    [Test]
    public void A_report_shows_in_the_current_snapshot_once_the_loop_has_applied_it()
    {
        var release = new TaskCompletionSource();
        var builder = new GraphBuilder();
        var task = builder.Task("task", async context =>
        {
            context.AddWarning("slow");
            await release.Task;
        });

        var scripted = ScriptedRun.Start(builder.Build(), new RunOptions { Journal = Journal.None });
        scripted.PumpLoop();
        scripted.PumpBodies();
        scripted.Run.Current[task.Path].Warnings.ShouldBeEmpty();
        scripted.PumpLoop();
        scripted.Run.Current[task.Path].Warnings.ShouldBe(["slow"]);

        release.SetResult();
        scripted.Finish(_limit).Outcome.ShouldBe(RunOutcome.Complete);
    }

    [Test]
    public void Advancing_the_clock_fires_each_delay_that_comes_due_and_pumps_after_each()
    {
        var checks = 0;
        var builder = new GraphBuilder();
        var task = builder.Task("task", context => context.WaitUntilAsync(
            TimeSpan.FromHours(1),
            _ => Task.FromResult(++checks == 4),
            "the export to finish"));

        var scripted = ScriptedRun.Start(builder.Build(), new RunOptions { Journal = Journal.None });
        scripted.Pump();
        checks.ShouldBe(1);
        scripted.Advance(TimeSpan.FromHours(2.5));
        checks.ShouldBe(3);
        scripted.TimeProvider.GetUtcNow().ShouldBe(_start.AddHours(2.5));
        scripted.Advance(TimeSpan.FromMinutes(30));
        var result = scripted.Finish(_limit);

        checks.ShouldBe(4);
        result.Snapshot[task.Path].Settled.ShouldBe(_start.AddHours(3));
    }

    [Test]
    public void Firing_the_next_timer_moves_the_clock_to_when_it_is_due_and_pumps_nothing()
    {
        var resumed = false;
        var builder = new GraphBuilder();
        builder.Task("task", async context =>
        {
            await Task.Delay(TimeSpan.FromMinutes(5), context.TimeProvider, context.CancellationToken);
            resumed = true;
        });

        var scripted = ScriptedRun.Start(builder.Build(), new RunOptions { Journal = Journal.None });
        scripted.Pump();

        scripted.FireNextTimer().ShouldBeTrue();
        scripted.TimeProvider.GetUtcNow().ShouldBe(_start.AddMinutes(5));
        resumed.ShouldBeFalse();
        scripted.Pump();
        resumed.ShouldBeTrue();
        scripted.FireNextTimer().ShouldBeFalse();
        scripted.Finish(_limit).Outcome.ShouldBe(RunOutcome.Complete);
    }

    [Test]
    public void A_clock_passed_in_the_options_is_used_as_is_and_the_scripted_run_does_not_move_it()
    {
        var clock = new InstantClock();
        var scripted = ScriptedRun.Start(new GraphBuilder().Build(), new RunOptions { Journal = Journal.None, TimeProvider = clock });

        scripted.TimeProvider.ShouldBeSameAs(clock);
        Should.Throw<InvalidOperationException>(() => scripted.Advance(TimeSpan.FromMinutes(1)));
        Should.Throw<InvalidOperationException>(() => scripted.FireNextTimer());
        scripted.Finish(_limit).Outcome.ShouldBe(RunOutcome.Complete);
    }

    [Test]
    public void Finishing_a_run_that_waits_for_something_outside_the_pump_throws_once_the_limit_runs_out()
    {
        var never = new TaskCompletionSource();
        var builder = new GraphBuilder();
        builder.Task("task", async _ => await never.Task);

        var scripted = ScriptedRun.Start(builder.Build(), new RunOptions { Journal = Journal.None });

        Should.Throw<TimeoutException>(() => scripted.Finish(TimeSpan.FromMilliseconds(50)));
        never.SetResult();
        scripted.Finish(_limit).Outcome.ShouldBe(RunOutcome.Complete);
    }

    [Test]
    public async Task Pumping_until_a_condition_asynchronously_follows_a_body_that_awaits_work_on_the_thread_pool()
    {
        var builder = new GraphBuilder();
        var task = builder.Task("task", async _ => await Task.Run(() => { }));

        var scripted = ScriptedRun.Start(builder.Build(), new RunOptions { Journal = Journal.None });

        (await scripted.PumpUntilAsync(snapshot => snapshot.Ended is not null, _limit)).ShouldBeTrue();
        scripted.Run.Current[task.Path].Status.ShouldBe(NodeStatus.Succeeded);
    }

    // What stays on the pump: a body that awaits only what the test, the scripted run's clock or the run completes runs every
    // piece on the pumping thread, and pumping until nothing is queued finishes the run.

    [Test]
    public void A_body_that_awaits_a_task_the_test_completes_stays_on_the_pump()
    {
        foreach (var options in new[] { TaskCreationOptions.None, TaskCreationOptions.RunContinuationsAsynchronously })
        {
            var gate = new TaskCompletionSource(options);
            var probe = new Probe();
            probe.Start(async _ =>
            {
                await gate.Task;
                probe.Mark();
            });

            probe.Scripted.Pump();
            gate.SetResult();
            probe.Scripted.Pump();

            probe.ShouldHaveStayedOnThePump();
        }
    }

    [Test]
    public void A_body_that_awaits_a_task_the_test_completes_with_ConfigureAwait_false_stays_on_the_pump()
    {
        var gate = new TaskCompletionSource();
        var probe = new Probe();
        probe.Start(async _ =>
        {
            await gate.Task.ConfigureAwait(false);
            probe.Mark();
        });

        probe.Scripted.Pump();
        gate.SetResult();
        probe.Scripted.Pump();

        probe.ShouldHaveStayedOnThePump();
    }

    [Test]
    public void A_body_that_delays_on_the_scripted_runs_clock_stays_on_the_pump()
    {
        var probe = new Probe();
        probe.Start(async context =>
        {
            await Task.Delay(TimeSpan.FromHours(1), context.TimeProvider, context.CancellationToken);
            probe.Mark();
        });

        probe.Scripted.Pump();
        probe.Scripted.Advance(TimeSpan.FromHours(1));

        probe.ShouldHaveStayedOnThePump();
    }

    [Test]
    public void A_body_that_yields_or_saves_a_checkpoint_stays_on_the_pump()
    {
        var probe = new Probe();
        probe.Start(async context =>
        {
            await Task.Yield();
            probe.Mark();
            await context.SaveCheckpointAsync("job-17");
            probe.Mark();
        });

        probe.Scripted.Pump();

        probe.ShouldHaveStayedOnThePump();
    }

    [Test]
    public void A_stop_that_ends_a_delay_on_the_scripted_runs_clock_stays_on_the_pump()
    {
        var probe = new Probe();
        probe.Start(async context =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromHours(1), context.TimeProvider, context.CancellationToken);
            }
            finally
            {
                probe.Mark();
            }
        });

        probe.Scripted.Pump();
        probe.Scripted.Run.Stop();
        probe.Scripted.Pump();

        probe.ShouldHaveStayedOnThePump();
    }

    [Test]
    public void A_stop_of_a_task_waiting_in_the_wait_helper_stays_on_the_pump()
    {
        var probe = new Probe();
        probe.Start(async context =>
        {
            try
            {
                await context.WaitUntilAsync(TimeSpan.FromMinutes(1), _ => Task.FromResult(false));
            }
            finally
            {
                probe.Mark();
            }
        });

        probe.Scripted.Pump();
        probe.Scripted.Run.Stop();
        probe.Scripted.Pump();

        probe.ShouldHaveStayedOnThePump();
    }

    // What leaves the pump: work the host cannot see. Pumping until a condition holds, in real time, follows it.

    [Test]
    public void A_stop_that_ends_a_delay_awaited_with_ConfigureAwait_false_leaves_the_pump()
    {
        var probe = new Probe();
        probe.Start(async context =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromHours(1), context.TimeProvider, context.CancellationToken).ConfigureAwait(false);
            }
            finally
            {
                probe.Mark();
            }
        });

        probe.Scripted.Pump();
        probe.Scripted.Run.Stop();

        probe.Scripted.PumpUntil(snapshot => snapshot.Ended is not null, _limit).ShouldBeTrue();
        probe.RanOffThePump.ShouldBeTrue();
    }

    [Test]
    public void A_body_that_awaits_work_on_the_thread_pool_comes_back_to_the_pump_but_only_pumping_until_a_condition_follows_it()
    {
        var probe = new Probe();
        probe.Start(async _ =>
        {
            await Task.Run(() => { });
            probe.Mark();
        });

        probe.Scripted.PumpUntil(snapshot => snapshot.Ended is not null, _limit).ShouldBeTrue();

        probe.Marks.ShouldBe(1);
        probe.RanOffThePump.ShouldBeFalse();
    }

    [Test]
    public void A_body_that_delays_on_an_instant_clock_comes_back_to_the_pump_but_only_pumping_until_a_condition_follows_it()
    {
        var instant = new InstantClock();
        var probe = new Probe();
        probe.Start(async context =>
        {
            await Task.Delay(TimeSpan.FromHours(1), instant, context.CancellationToken);
            probe.Mark();
        });

        probe.Scripted.PumpUntil(snapshot => snapshot.Ended is not null, _limit).ShouldBeTrue();

        probe.Marks.ShouldBe(1);
        probe.RanOffThePump.ShouldBeFalse();
        probe.Scripted.TimeProvider.GetUtcNow().ShouldBe(_start);
    }

    /// <summary>
    /// A scripted run of one task whose body awaits one kind of thing and marks each piece that follows an await with the thread
    /// it ran on. Made on the test's thread, which is the one that pumps.
    /// </summary>
    private sealed class Probe
    {
        private readonly int _pumping = Environment.CurrentManagedThreadId;
        private int _marks;
        private int _offThePump;

        public ScriptedRun Scripted { get; private set; } = null!;

        public int Marks => Volatile.Read(ref _marks);

        public bool RanOffThePump => Volatile.Read(ref _offThePump) > 0;

        public void Start(TaskBody body)
        {
            var builder = new GraphBuilder();
            builder.Task("probe", body);
            Scripted = ScriptedRun.Start(builder.Build(), new RunOptions { Journal = Journal.None, GracePeriod = Timeout.InfiniteTimeSpan });
        }

        public void Mark()
        {
            Interlocked.Increment(ref _marks);
            if (Environment.CurrentManagedThreadId != _pumping)
            {
                Interlocked.Increment(ref _offThePump);
            }
        }

        public void ShouldHaveStayedOnThePump()
        {
            Scripted.Run.Result.IsCompleted.ShouldBeTrue("pumping until nothing was queued did not finish the run");
            Marks.ShouldBeGreaterThan(0);
            RanOffThePump.ShouldBeFalse();
        }
    }
}
