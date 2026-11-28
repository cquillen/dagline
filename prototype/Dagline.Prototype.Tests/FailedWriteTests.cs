namespace Dagline.Prototype.Tests;

/// <summary>
/// The failed writes the five cases do not reach. For each, the rules of the design are silent or only imply an answer, and the
/// test pins what the prototype chose.
/// </summary>
public class FailedWriteTests
{
    [Test]
    public void A_start_entry_whose_write_fails_starts_nothing_and_gives_its_slot_back()
    {
        var scenario = new Scenario();
        var publishStarted = false;
        var load = scenario.Task("load", _ => Task.CompletedTask);
        scenario.Task("publish", _ =>
        {
            publishStarted = true;
            return Task.CompletedTask;
        }).Requires(load);
        scenario.Journal.FailsWhen = entries => entries.Any(entry => entry is TaskStartedEntry { Path: "publish" });
        scenario.Start();

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
        scenario.Result.Outcome.ShouldBe(ProtoOutcome.Unrecorded);
        scenario.Result.Snapshot[NodePath.Parse("publish")].Status.ShouldBe(NodeStatus.Ready);
        scenario.Result.Snapshot.RunningTasks.InUse.ShouldBe(0);

        // The settle before it landed, flushed, in a write of its own.
        scenario.Journal.Describe().ShouldBe("run started; load started; load succeeded");
    }

    [Test]
    public void The_runs_start_entry_whose_write_fails_starts_nothing()
    {
        var scenario = new Scenario();
        var started = false;
        scenario.Task("load", _ =>
        {
            started = true;
            return Task.CompletedTask;
        });
        scenario.Journal.FailsWhen = entries => entries.Any(entry => entry is RunStartedEntry);
        scenario.Start();

        scenario.Host.PumpUntilIdle();

        scenario.Events.ShouldBe(["run started", "load: pending -> ready", "stop requested by Dagline", "run ended: unrecorded"]);
        started.ShouldBeFalse();
        scenario.Result.Outcome.ShouldBe(ProtoOutcome.Unrecorded);
        scenario.Journal.Entries.ShouldBeEmpty();
        scenario.Journal.IsClosed.ShouldBeTrue();
    }

    [Test]
    public void A_save_made_after_an_earlier_write_failed_throws_the_earlier_failure_and_writes_nothing()
    {
        var scenario = new Scenario();
        Exception? first = null;
        Exception? second = null;
        scenario.Task("load", async context =>
        {
            try
            {
                await context.SaveCheckpointAsync("job-17", CancellationToken.None);
            }
            catch (Exception thrown)
            {
                first = thrown;
            }

            try
            {
                await context.SaveCheckpointAsync("job-18", CancellationToken.None);
            }
            catch (Exception thrown)
            {
                second = thrown;
            }
        });
        scenario.Journal.FailsWhen = entries => entries.Any(entry => entry is CheckpointSavedEntry { Text: "job-17" });
        scenario.Start();

        scenario.Host.PumpUntilIdle();

        first.ShouldBeOfType<JournalWriteException>();
        second.ShouldBeOfType<JournalWriteException>().InnerException.ShouldBe(first!.InnerException);
        scenario.Journal.FailedWrites.ShouldBe(1);
        scenario.Journal.Describe().ShouldBe("run started; load started");
        scenario.Result.Outcome.ShouldBe(ProtoOutcome.Unrecorded);
    }

    [Test]
    public void A_write_that_fails_after_the_apps_own_stop_publishes_no_second_stop()
    {
        var scenario = new Scenario();
        var gate = new Gate();
        scenario.Task("load", async _ => await gate.Task);
        scenario.Journal.FailsWhen = entries => entries.Any(entry => entry is TaskSettledEntry);
        var run = scenario.Start();
        scenario.Host.PumpUntilIdle();

        run.Stop();
        scenario.Host.PumpUntilIdle();
        gate.Open();
        scenario.Host.PumpUntilIdle();

        scenario.Events[3..].ShouldBe(["stop requested by App", "load: running -> succeeded", "run ended: unrecorded"]);

        // The snapshot says the app stopped the run, and the outcome says it is unrecorded: nothing says Dagline would have stopped it.
        scenario.Result.Snapshot.Stop!.By.ShouldBe(StopRequester.App);
        scenario.Result.Outcome.ShouldBe(ProtoOutcome.Unrecorded);
    }

    [Test]
    public async Task Pump_until_async_finishes_a_body_that_leaves_the_host_and_goes_on_pumping_from_another_thread()
    {
        var scenario = new Scenario();
        var calling = Environment.CurrentManagedThreadId;
        var afterTheWork = 0;
        scenario.Task("load", async _ =>
        {
            await Task.Run(() => Thread.Sleep(50));
            afterTheWork = Environment.CurrentManagedThreadId;
        });
        scenario.Start();

        var finished = await scenario.Host.PumpUntilAsync(() => scenario.Run.Result.IsCompleted, TimeSpan.FromSeconds(10));

        finished.ShouldBeTrue();
        scenario.Result.Outcome.ShouldBe(RunOutcome.Complete);

        // Written beside the probe matrix rather than asserted: the pump resumed on a thread-pool thread, which is usually, and
        // not necessarily, another thread than the one the test began on.
        File.WriteAllText(
            Path.Combine(AppContext.BaseDirectory, "probe-async.txt"),
            $"test began on thread {calling}; the body's continuation was pumped on thread {afterTheWork}; "
            + $"the test went on, after the await, on thread {Environment.CurrentManagedThreadId}");
    }
}
