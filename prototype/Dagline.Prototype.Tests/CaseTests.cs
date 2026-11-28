namespace Dagline.Prototype.Tests;

/// <summary>
/// The five cases, each scripted once on the manual host: every loop turn, body continuation and reporter call below runs on the
/// test's thread, inside a Pump call, in the order the test states. No test here repeats, sleeps or waits on real time.
/// </summary>
public class CaseTests
{
    private static readonly TimeSpan _tenSeconds = TimeSpan.FromSeconds(10);

    // ---- Case 1: a stop with a zero grace period racing a body's end ----

    [Test]
    public void Case1_stop_applied_first_then_the_body_ends()
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
        var run = scenario.Start(gracePeriod: TimeSpan.Zero);
        scenario.Host.PumpUntilIdle();

        run.Stop();
        gate.Open();
        scenario.Host.PumpUntilIdle();

        scenario.Events.ShouldBe(
        [
            "run started",
            "load: pending -> ready",
            "load: ready -> running",
            "stop requested by App",
            "grace period over",
            "load: running -> cancelled",
            "run ended: stopped",
        ]);
        scenario.Result.Outcome.ShouldBe(RunOutcome.Stopped);
        scenario.Result.Snapshot[NodePath.Parse("load")].Message.ShouldBe("still running when the run stopped waiting");
        scenario.Result.Snapshot[NodePath.Parse("publish")].Status.ShouldBe(NodeStatus.Pending);
        publishStarted.ShouldBeFalse();
        scenario.Journal.Describe().ShouldBe("run started; load started; load cancelled; run ended stopped");
    }

    [Test]
    public void Case1_the_body_ends_first_then_the_stop()
    {
        var scenario = new Scenario();
        var gate = new Gate();
        var publishSawTheTokenSignalled = (bool?)null;
        var load = scenario.Task("load", async _ => await gate.Task);
        scenario.Task("publish", context =>
        {
            publishSawTheTokenSignalled = context.CancellationToken.IsCancellationRequested;
            return Task.CompletedTask;
        }).Requires(load);
        var run = scenario.Start(gracePeriod: TimeSpan.Zero);
        scenario.Host.PumpUntilIdle();

        // The body's end is the post of its input. Opening the gate only queues the body's continuation, so the bodies are pumped
        // before the stop is asked for: the inbox then holds the body's end, and the stop behind it.
        gate.Open();
        scenario.Host.PumpBodies();
        run.Stop();
        scenario.Host.PumpUntilIdle();

        scenario.Events.ShouldBe(
        [
            "run started",
            "load: pending -> ready",
            "load: ready -> running",
            "load: running -> succeeded",
            "publish: pending -> ready",
            "publish: ready -> running",
            "stop requested by App",
            "grace period over",
            "publish: running -> cancelled",
            "run ended: stopped",
        ]);
        scenario.Result.Outcome.ShouldBe(RunOutcome.Stopped);
        scenario.Result.Snapshot[NodePath.Parse("load")].Status.ShouldBe(NodeStatus.Succeeded);
        scenario.Journal.Describe().ShouldBe("run started; load started; load succeeded; publish started; publish cancelled; run ended stopped");

        // What the script shows: the dependent was started and let go of in the same turn of the loop, so its body first ran
        // after the run had ended, with the token already signalled. Starting a body is an effect the host carries out later.
        publishSawTheTokenSignalled.ShouldBe(true);
    }

    [Test]
    public void Case1_one_task_stop_first_is_stopped_and_body_first_is_complete()
    {
        string[] StopFirst()
        {
            var scenario = new Scenario();
            var gate = new Gate();
            scenario.Task("load", async _ => await gate.Task);
            var run = scenario.Start(gracePeriod: TimeSpan.Zero);
            scenario.Host.PumpUntilIdle();
            run.Stop();
            gate.Open();
            scenario.Host.PumpUntilIdle();
            scenario.Result.Outcome.ShouldBe(RunOutcome.Stopped);
            return scenario.Events[3..];
        }

        string[] BodyFirst()
        {
            var scenario = new Scenario();
            var gate = new Gate();
            scenario.Task("load", async _ => await gate.Task);
            var run = scenario.Start(gracePeriod: TimeSpan.Zero);
            scenario.Host.PumpUntilIdle();
            gate.Open();
            scenario.Host.PumpBodies();
            run.Stop();
            scenario.Host.PumpUntilIdle();
            scenario.Result.Outcome.ShouldBe(RunOutcome.Complete);
            return scenario.Events[3..];
        }

        StopFirst().ShouldBe(["stop requested by App", "grace period over", "load: running -> cancelled", "run ended: stopped"]);

        // The stop arrives after the run has ended, and changes nothing.
        BodyFirst().ShouldBe(["load: running -> succeeded", "run ended: complete"]);
    }

    // ---- Case 2: a settle whose journal write fails while a dependent would otherwise start ----

    [Test]
    public void Case2_a_settle_whose_write_fails_is_published_readies_its_dependent_and_starts_nothing()
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
        scenario.Journal.FailsWhen = entries => entries.Any(entry => entry is TaskSettledEntry { Path: "load" });
        scenario.Start();
        scenario.Host.PumpUntilIdle();

        gate.Open();
        scenario.Host.PumpUntilIdle();

        scenario.Events.ShouldBe(
        [
            "run started",
            "load: pending -> ready",
            "load: ready -> running",
            "load: running -> succeeded",
            "publish: pending -> ready",
            "stop requested by Dagline",
            "run ended: unrecorded",
        ]);
        publishStarted.ShouldBeFalse();

        // RunAsync throws nothing: the result is there, with the fourth outcome and the failed write beside it.
        scenario.Run.Result.IsCompletedSuccessfully.ShouldBeTrue();
        scenario.Result.Outcome.ShouldBe(ProtoOutcome.Unrecorded);
        scenario.Result.JournalFailure.ShouldBeOfType<IOException>();
        scenario.Result.Snapshot[NodePath.Parse("load")].Status.ShouldBe(NodeStatus.Succeeded);
        scenario.Result.Snapshot[NodePath.Parse("publish")].Status.ShouldBe(NodeStatus.Ready);
        scenario.Result.Snapshot.Outcome.ShouldBe(ProtoOutcome.Unrecorded);
        scenario.Result.Snapshot.Stop!.By.ShouldBe(StopRequester.Dagline);

        // Nothing is written after the failed write, the end included, and the journal is let go of.
        scenario.Journal.Describe().ShouldBe("run started; load started");
        scenario.Journal.FailedWrites.ShouldBe(1);
        scenario.Journal.IsClosed.ShouldBeTrue();
        scenario.Run.TasksToken.IsCancellationRequested.ShouldBeTrue();
    }

    // ---- Case 3: the last settle's write fails; and only the end entry's write fails ----

    [Test]
    public void Case3_the_last_settles_write_fails_with_every_task_succeeded_so_the_run_is_unrecorded()
    {
        var scenario = new Scenario();
        scenario.Task("load", _ => Task.CompletedTask);
        scenario.Journal.FailsWhen = entries => entries.Any(entry => entry is TaskSettledEntry);
        scenario.Start();

        scenario.Host.PumpUntilIdle();

        scenario.Events.ShouldBe(
        [
            "run started",
            "load: pending -> ready",
            "load: ready -> running",
            "load: running -> succeeded",
            "stop requested by Dagline",
            "run ended: unrecorded",
        ]);
        scenario.Result.Outcome.ShouldBe(ProtoOutcome.Unrecorded);
        scenario.Result.Snapshot.Counts[NodeStatus.Succeeded].ShouldBe(1);
        scenario.Journal.Describe().ShouldBe("run started; load started");
    }

    [Test]
    public void Case3_only_the_end_entrys_write_fails_so_the_run_keeps_its_outcome()
    {
        var scenario = new Scenario();
        scenario.Task("load", _ => Task.CompletedTask);
        scenario.Journal.FailsWhen = entries => entries.Any(entry => entry is RunEndedEntry);
        scenario.Start();

        scenario.Host.PumpUntilIdle();

        scenario.Events.ShouldBe(
        [
            "run started",
            "load: pending -> ready",
            "load: ready -> running",
            "load: running -> succeeded",
            "run ended: complete",
        ]);
        scenario.Result.Outcome.ShouldBe(RunOutcome.Complete);
        scenario.Result.Snapshot.Stop.ShouldBeNull();
        scenario.Journal.Describe().ShouldBe("run started; load started; load succeeded");

        // The prototype still hands the app the failed write: the design does not say whether it should.
        scenario.Result.JournalFailure.ShouldBeOfType<IOException>();
    }

    // ---- Case 4: a checkpoint save whose write fails ----

    [Test]
    public void Case4_a_save_whose_write_fails_throws_to_the_task_and_the_run_ends_unrecorded()
    {
        var scenario = new Scenario();
        var gate = new Gate();
        Exception? thrownToTheTask = null;
        string? checkpointAfter = null;
        scenario.Task("load", async context =>
        {
            try
            {
                await context.SaveCheckpointAsync("job-17", CancellationToken.None);
            }
            catch (Exception thrown)
            {
                thrownToTheTask = thrown;
            }

            checkpointAfter = context.Checkpoint;
            await gate.Task;
        });
        scenario.Journal.FailsWhen = entries => entries.Any(entry => entry is CheckpointSavedEntry);
        scenario.Start();

        scenario.Host.PumpUntilIdle();

        thrownToTheTask.ShouldBeOfType<JournalWriteException>().InnerException.ShouldBeOfType<IOException>();
        checkpointAfter.ShouldBe("");
        scenario.Run.TasksToken.IsCancellationRequested.ShouldBeTrue();
        scenario.Events.ShouldBe(["run started", "load: pending -> ready", "load: ready -> running", "stop requested by Dagline"]);
        scenario.Run.Result.IsCompleted.ShouldBeFalse();

        gate.Open();
        scenario.Host.PumpUntilIdle();

        scenario.Events[4..].ShouldBe(["load: running -> succeeded", "run ended: unrecorded"]);
        scenario.Run.Result.IsCompletedSuccessfully.ShouldBeTrue();
        scenario.Result.Outcome.ShouldBe(ProtoOutcome.Unrecorded);
        scenario.Journal.Describe().ShouldBe("run started; load started");
    }

    // ---- Case 5: a checkpoint save around letting go ----

    [Test]
    public void Case5_a_save_before_the_grace_period_elapses_succeeds_and_one_after_throws()
    {
        var scenario = new Scenario();
        var first = new Gate();
        var second = new Gate();
        var savedDuringGrace = false;
        Exception? thrownAfterLetGo = null;
        string? checkpointAtTheEnd = null;
        scenario.Task("load", async context =>
        {
            // A body that ignores the stop.
            await first.Task;
            await context.SaveCheckpointAsync("during-grace", CancellationToken.None);
            savedDuringGrace = true;
            await second.Task;
            try
            {
                await context.SaveCheckpointAsync("after-let-go", CancellationToken.None);
            }
            catch (Exception thrown)
            {
                thrownAfterLetGo = thrown;
            }

            checkpointAtTheEnd = context.Checkpoint;
        });
        var run = scenario.Start(gracePeriod: _tenSeconds);
        scenario.Host.PumpUntilIdle();

        run.Stop();
        scenario.Host.PumpUntilIdle();
        scenario.Clock.Advance(TimeSpan.FromSeconds(9));
        first.Open();
        scenario.Host.PumpUntilIdle();

        savedDuringGrace.ShouldBeTrue();
        scenario.Journal.Describe().ShouldBe("run started; load started; load checkpoint 'during-grace'");
        scenario.Run.Result.IsCompleted.ShouldBeFalse();

        scenario.Clock.Advance(TimeSpan.FromSeconds(1));
        scenario.Host.PumpUntilIdle();

        scenario.Events.ShouldBe(
        [
            "run started",
            "load: pending -> ready",
            "load: ready -> running",
            "stop requested by App",
            "grace period over",
            "load: running -> cancelled",
            "run ended: stopped",
        ]);
        scenario.Result.Outcome.ShouldBe(RunOutcome.Stopped);

        // The run has returned and nothing reads its inbox any more. The save is still answered, on the thread that makes it.
        second.Open();
        scenario.Host.PumpUntilIdle();

        thrownAfterLetGo.ShouldBeOfType<InvalidOperationException>();
        checkpointAtTheEnd.ShouldBe("during-grace");
        scenario.Journal.Describe().ShouldBe(
            "run started; load started; load checkpoint 'during-grace'; load cancelled; run ended stopped");
    }

    [Test]
    public void Case5_a_save_after_letting_go_is_refused_by_the_core_while_the_loop_still_waits_for_reporters()
    {
        var scenario = new Scenario();
        var gate = new Gate();
        Exception? thrownAfterLetGo = null;
        scenario.Task("load", async context =>
        {
            await gate.Task;
            try
            {
                await context.SaveCheckpointAsync("after-let-go", CancellationToken.None);
            }
            catch (Exception thrown)
            {
                thrownAfterLetGo = thrown;
            }
        });
        var run = scenario.Start(gracePeriod: _tenSeconds);
        scenario.Host.PumpUntilIdle();
        run.Stop();
        scenario.Host.PumpLoop();
        scenario.Clock.Advance(_tenSeconds);

        // The reporters are not pumped, so the loop has ended the run and is waiting for them: its inbox is still read.
        scenario.Host.PumpLoop();
        scenario.Run.Result.IsCompleted.ShouldBeFalse();
        gate.Open();
        scenario.Host.PumpBodies();
        scenario.Host.PumpLoop();
        scenario.Host.PumpBodies();

        thrownAfterLetGo.ShouldBeOfType<InvalidOperationException>();
        scenario.Journal.Entries.OfType<CheckpointSavedEntry>().ShouldBeEmpty();
    }

    [Test]
    public void Case5_a_save_in_flight_when_the_grace_period_elapses_is_decided_by_the_order_of_the_inbox()
    {
        var scenario = new Scenario();
        var gate = new Gate();
        Task? save = null;
        scenario.Task("load", async context =>
        {
            await gate.Task;
            save = context.SaveCheckpointAsync("just-in-time", CancellationToken.None);
            await save;
        });
        var run = scenario.Start(gracePeriod: _tenSeconds);
        scenario.Host.PumpUntilIdle();
        run.Stop();
        scenario.Host.PumpUntilIdle();

        // The save is posted, then the grace period elapses, and only then does the loop run: it applies them in that order.
        gate.Open();
        scenario.Host.PumpBodies();
        scenario.Clock.Advance(_tenSeconds);
        scenario.Host.PumpUntilIdle();

        save!.IsCompletedSuccessfully.ShouldBeTrue();
        scenario.Journal.Describe().ShouldBe(
            "run started; load started; load checkpoint 'just-in-time'; load cancelled; run ended stopped");
    }
}
