using Microsoft.Extensions.Time.Testing;

namespace Dagline.Prototype.Tests;

/// <summary>The invariants the old engine documented, and the rules of the design that the five cases do not reach.</summary>
public class InvariantTests
{
    private static readonly TimeSpan _tenSeconds = TimeSpan.FromSeconds(10);

    [Test]
    public void The_core_orders_a_stops_effects_signal_then_timer_then_publish()
    {
        var graph = new GraphBuilder();
        var bodies = new ProtoBodies();
        graph.Task("load", bodies.Of(_ => Task.CompletedTask));
        var built = graph.Build();
        var core = new RunCore(
            built,
            RunSettings.From(built, new RunOptions { Journal = Journal.None, GracePeriod = _tenSeconds }),
            hasJournal: false,
            source: new NoSource());
        var now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        core.Apply(Input.Start, now);

        var stop = core.Apply(Input.Stop, now).Select(Describe).ToArray();

        // The tasks' token is signalled and the grace timer exists before any reporter can hear of the stop.
        stop.ShouldBe(["SignalTasks", "ArmGrace 00:00:10", "Publish stop requested by App"]);
    }

    [Test]
    public void The_core_accepts_only_the_result_of_the_write_while_one_is_under_way()
    {
        var graph = new GraphBuilder();
        var bodies = new ProtoBodies();
        graph.Task("load", bodies.Of(_ => Task.CompletedTask));
        var built = graph.Build();
        var core = new RunCore(built, RunSettings.From(built, new RunOptions { Journal = Journal.None }), hasJournal: true, new NoSource());
        var now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // The start's entry is the last effect, and nothing is published before its result is known.
        core.Apply(Input.Start, now).Select(Describe).ToArray().ShouldBe(["Record run started (ToDisk)"]);
        core.AwaitsWrite.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => core.Apply(Input.Stop, now));

        // The start of a task is an entry too, so the step is cut a second time before the body starts.
        core.Apply(Input.Written(null), now).Select(Describe).ToArray().ShouldBe(
        [
            "Publish run started",
            "Publish load: pending -> ready",
            "Record load started (None)",
        ]);
        core.Apply(Input.Written(null), now).Select(Describe).ToArray().ShouldBe(["Publish load: ready -> running", "Start load"]);
    }

    [Test]
    public void The_tasks_token_is_signalled_before_the_runs_result_is_available()
    {
        var scenario = new Scenario();
        scenario.Task("load", async _ => await new Gate().Task);
        var run = scenario.Start(gracePeriod: TimeSpan.Zero);
        bool? signalledWhenTheResultCame = null;
        run.Result.ContinueWith(
            _ => signalledWhenTheResultCame = run.TasksToken.IsCancellationRequested,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        scenario.Host.PumpUntilIdle();

        run.Stop();
        scenario.Host.PumpUntilIdle();

        signalledWhenTheResultCame.ShouldBe(true);
    }

    [Test]
    public async Task On_the_thread_pool_a_reporter_that_hears_of_the_stop_finds_the_grace_period_running()
    {
        var clock = new FakeTimeProvider();
        var graph = new GraphBuilder();
        var bodies = new ProtoBodies();
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        graph.Task("load", bodies.Of(async _ =>
        {
            running.SetResult();
            await new Gate().Task;
        }));
        var reporter = new Recorder
        {
            // Moves the clock on by the grace period the moment the stop is heard. Had the timer not existed yet, the clock would
            // have moved before it started, the grace period would never end, and this test would time out.
            OnEvent = (runEvent, _) =>
            {
                if (runEvent is StopRequested)
                {
                    clock.Advance(_tenSeconds);
                }
            },
        };
        var run = ProtoRun.Start(graph.Build(), bodies, new ProtoOptions { GracePeriod = _tenSeconds, TimeProvider = clock, Reporters = [reporter] });
        await running.Task;

        run.Stop();
        var result = await run.Result;

        result.Outcome.ShouldBe(RunOutcome.Stopped);
        reporter.Lines.ShouldContain("grace period over");
        run.TasksToken.IsCancellationRequested.ShouldBeTrue();
    }

    [Test]
    public async Task On_the_thread_pool_a_reporter_never_reads_a_current_snapshot_older_than_an_event_it_holds()
    {
        var graph = new GraphBuilder();
        var bodies = new ProtoBodies();
        TaskNode? previous = null;
        for (var index = 0; index < 60; index++)
        {
            var task = graph.Task($"step-{index}", bodies.Of(async context =>
            {
                context.SetCount(1, 2);
                await Task.Yield();
                context.AddWarning("checked");
            }));
            if (index % 3 == 2)
            {
                task.Requires(previous!);
            }

            previous = task;
        }

        ISnapshotSource? source = null;
        var older = 0;
        var reporter = new Recorder
        {
            OnEvent = (runEvent, snapshot) =>
            {
                source ??= (runEvent as RunStarted)?.Source;
                if (Age(source!.Current) < Age(snapshot))
                {
                    Interlocked.Increment(ref older);
                }
            },
        };

        var result = await ProtoRun.Start(graph.Build(), bodies, new ProtoOptions { Reporters = [reporter] }).Result;

        result.Outcome.ShouldBe(RunOutcome.Complete);
        result.Snapshot.WarningCount.ShouldBe(60);
        older.ShouldBe(0);

        // How far a snapshot has got: every status change and every warning moves it on, and nothing moves it back.
        static int Age(Snapshot snapshot) => snapshot.Nodes.Where(node => !node.IsGroup).Sum(node => (int)node.Status * 10 + node.WarningCount);
    }

    [Test]
    public void A_token_already_cancelled_starts_nothing()
    {
        var scenario = new Scenario();
        var started = false;
        scenario.Task("load", _ =>
        {
            started = true;
            return Task.CompletedTask;
        });
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        scenario.Start(cancellationToken: cancelled.Token);

        scenario.Host.PumpUntilIdle();

        // The stop was posted before the start, held by the core, and applied once the tasks were ready and before any start.
        scenario.Events.ShouldBe(["run started", "load: pending -> ready", "stop requested by App", "run ended: stopped"]);
        started.ShouldBeFalse();
        scenario.Result.Outcome.ShouldBe(RunOutcome.Stopped);
    }

    [Test]
    public void A_drain_starts_nothing_new_and_lets_the_running_task_finish_untold()
    {
        var scenario = new Scenario();
        var gate = new Gate();
        var publishStarted = false;
        var load = scenario.Task("load", async _ => await gate.Task);
        scenario.Task("publish", _ =>
        {
            publishStarted = true;
            return Task.CompletedTask;
        }).Requires(load);
        var run = scenario.Start();
        scenario.Host.PumpUntilIdle();

        run.Drain();
        scenario.Host.PumpUntilIdle();
        run.TasksToken.IsCancellationRequested.ShouldBeFalse();
        gate.Open();
        scenario.Host.PumpUntilIdle();

        scenario.Events[3..].ShouldBe(["drain requested", "load: running -> succeeded", "publish: pending -> ready", "run ended: stopped"]);
        publishStarted.ShouldBeFalse();
    }

    [Test]
    public void A_step_keeps_one_time_across_its_write()
    {
        var scenario = new Scenario();
        var gate = new Gate();
        var load = scenario.Task("load", async _ => await gate.Task);
        scenario.Task("publish", _ => new Gate().Task).Requires(load);

        // The write of load's settle takes five seconds of the run's clock.
        scenario.Journal.FailsWhen = entries =>
        {
            if (entries.Any(entry => entry is TaskSettledEntry))
            {
                scenario.Clock.Advance(TimeSpan.FromSeconds(5));
            }

            return false;
        };
        scenario.Start();
        scenario.Host.PumpUntilIdle();
        scenario.Clock.Advance(TimeSpan.FromMinutes(1));
        var settledAt = scenario.Clock.GetUtcNow();

        gate.Open();
        scenario.Host.PumpUntilIdle();

        // Everything the settle caused, decided after its write and after the write that follows, carries the settle's time.
        var afterTheSettle = scenario.Reporter.Events[3..];
        afterTheSettle.Select(Recorder.Describe).ToArray().ShouldBe(
            ["load: running -> succeeded", "publish: pending -> ready", "publish: ready -> running"]);
        afterTheSettle.ShouldAllBe(runEvent => runEvent.At == settledAt);
        scenario.Clock.GetUtcNow().ShouldBe(settledAt + TimeSpan.FromSeconds(5));
    }

    [Test]
    public void The_run_waits_for_a_busy_reporter_for_the_reporter_wait_on_its_clock_and_then_drops_it()
    {
        var scenario = new Scenario();
        scenario.Task("load", _ => Task.CompletedTask);
        var stuck = new StuckReporter();
        using var appToken = new CancellationTokenSource();
        scenario.Start(reporterWait: _tenSeconds, reporters: [scenario.Reporter, stuck], cancellationToken: appToken.Token);

        scenario.Host.PumpUntilIdle();
        scenario.Events[^1].ShouldBe("run ended: complete");
        scenario.Run.Result.IsCompleted.ShouldBeFalse();

        // The run's token means a stop and nothing else: after the end it changes nothing, the wait included.
        appToken.Cancel();
        scenario.Clock.Advance(TimeSpan.FromSeconds(9));
        scenario.Host.PumpUntilIdle();
        scenario.Run.Result.IsCompleted.ShouldBeFalse();

        scenario.Clock.Advance(TimeSpan.FromSeconds(1));
        scenario.Host.PumpUntilIdle();

        var error = scenario.Result.ReporterErrors.ShouldHaveSingleItem();
        error.Reporter.ShouldBe(stuck);
        error.Exception.ShouldBeOfType<OperationCanceledException>();
        stuck.Token.IsCancellationRequested.ShouldBeTrue();
        scenario.Result.Outcome.ShouldBe(RunOutcome.Complete);
    }

    [Test]
    public void Under_the_manual_host_the_loop_the_bodies_the_journal_and_the_reporters_all_run_on_the_tests_thread()
    {
        var scenario = new Scenario();
        var test = Environment.CurrentManagedThreadId;
        var threads = new HashSet<int>();
        var gate = new Gate();
        scenario.Task("load", async context =>
        {
            threads.Add(Environment.CurrentManagedThreadId);
            await gate.Task;
            threads.Add(Environment.CurrentManagedThreadId);
            await context.SaveCheckpointAsync("job-17");
            threads.Add(Environment.CurrentManagedThreadId);
            await Task.Delay(_tenSeconds, context.TimeProvider, context.CancellationToken);
            threads.Add(Environment.CurrentManagedThreadId);
        });
        scenario.Journal.FailsWhen = _ =>
        {
            threads.Add(Environment.CurrentManagedThreadId);
            return false;
        };
        scenario.Start();

        scenario.Host.PumpUntilIdle();
        gate.Open();
        scenario.Host.PumpUntilIdle();
        scenario.Clock.Advance(_tenSeconds);
        scenario.Host.PumpUntilIdle();

        scenario.Result.Outcome.ShouldBe(RunOutcome.Complete);
        threads.ShouldBe([test]);
        scenario.Reporter.Threads.ShouldBe([test]);
    }

    [Test]
    public void Progress_is_sampled_and_never_posted_and_counts_beyond_the_kept_are_sampled_too()
    {
        var scenario = new Scenario();
        var gate = new Gate();
        scenario.Task("load", async context =>
        {
            for (var report = 1; report <= 1_000; report++)
            {
                context.SetCount(report, 1_000);
            }

            for (var warning = 1; warning <= 130; warning++)
            {
                context.AddWarning($"warning {warning}");
            }

            context.SetStatusText("almost there");
            await gate.Task;
        });
        var run = scenario.Start(journal: false);
        scenario.Host.PumpLoop();
        scenario.Host.PumpBodies();

        // 1,000 progress reports and 130 warnings posted 100 inputs: the kept warnings. The rest only overwrote the task's slot.
        run.Inbox.Posted.ShouldBe(1 + 100);

        // The loop has applied none of them yet. A reader of the current snapshot sees the progress already, sampled at the read;
        // the count of warnings beyond the kept is held back until the kept ones are there.
        var before = run.Current[NodePath.Parse("load")];
        before.Count.ShouldBe(1_000);
        before.StatusText.ShouldBe("almost there");
        before.Warnings.Count.ShouldBe(0);
        before.WarningsOmitted.ShouldBe(0);

        scenario.Host.PumpUntilIdle();
        var after = run.Current[NodePath.Parse("load")];
        after.Warnings.Count.ShouldBe(100);
        after.WarningsOmitted.ShouldBe(30);
        scenario.Events.Count(line => line.StartsWith("warning ", StringComparison.Ordinal)).ShouldBe(100);

        gate.Open();
        scenario.Host.PumpUntilIdle();
        scenario.Result.Snapshot.WarningCount.ShouldBe(130);
        scenario.Result.Snapshot[NodePath.Parse("load")].Count.ShouldBe(1_000);
    }

    private static string Describe(Effect effect) => effect.Kind switch
    {
        EffectKind.Publish => $"Publish {Recorder.Describe(effect.Event!)}",
        EffectKind.Record => $"Record {string.Join(", ", effect.Entries!.Select(MemoryJournal.Describe))} ({effect.Flush})",
        EffectKind.Start => $"Start {effect.Task!.Path}",
        EffectKind.ArmGrace => $"ArmGrace {effect.Period}",
        _ => effect.Kind.ToString(),
    };

    private sealed class NoSource : ISnapshotSource
    {
        public Snapshot Current => throw new NotSupportedException();
    }

    private sealed class StuckReporter : IReporter
    {
        private readonly TaskCompletionSource _never = new();

        public CancellationToken Token { get; private set; }

        public ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            return runEvent is RunEnded ? new ValueTask(_never.Task) : ValueTask.CompletedTask;
        }
    }
}
