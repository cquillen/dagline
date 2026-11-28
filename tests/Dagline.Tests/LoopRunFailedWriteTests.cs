using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace Dagline.Tests;

/// <summary>
/// A journal write that fails (ADR 0015): the core knows the write's result before it decides what follows it,
/// so nothing starts after a failed write, Dagline applies a stop unless one has been applied, and the run ends unrecorded, with
/// no end entry, so the journal lists it as died. Each test is one scripted run that fails one write through the journal write
/// hook, so none is repeated.
/// </summary>
/// <remarks>
/// Until the cutover (#153) the outcome unrecorded is held internally, on <see cref="RunResult.Unrecorded"/>, and the run's end
/// carries the <see cref="JournalWriteException"/> in place of a run outcome, until #153.
/// </remarks>
public class LoopRunFailedWriteTests
{
    private static readonly DateTimeOffset _runStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task A_settle_whose_write_fails_is_still_published_and_its_dependents_become_ready_but_none_starts()
    {
        using var temporary = new TemporaryDirectory();
        var builder = new GraphBuilder();
        var load = builder.Task("load", _ => Task.CompletedTask);
        var publish = builder.Task("publish", _ => Task.CompletedTask).Requires(load);
        var reporter = new RecordingReporter();
        var journal = JournalWriteFailure.SettleOf("load").In(Journal.New(temporary.Path));

        var result = await ScriptedDriver.Start(builder.Build(), Options(journal, reporter)).Finish();

        reporter.Lines.ShouldBe(
        [
            "run started",
            "load: pending -> ready",
            "load: ready -> running",
            "load: running -> succeeded",
            "publish: pending -> ready",
            "stop requested",
            "run ended: JournalWriteException",
        ]);
        reporter.Events.OfType<StopRequested>().Single().By.ShouldBe(StopRequester.Dagline);
        ShouldHaveEndedUnrecorded(result, reporter);
        result.Snapshot[publish.Path].Status.ShouldBe(NodeStatus.Ready);
        JournalEntries.Read(TheJournalIn(temporary)).ShouldBe(["header", "run started", "started load"]);
        await ShouldBeListedAsDiedAndFree(temporary);
    }

    [Test]
    public async Task A_start_entry_whose_write_fails_starts_nothing_and_the_task_gives_back_its_slots_and_stays_ready()
    {
        using var temporary = new TemporaryDirectory();
        var builder = new GraphBuilder();
        var load = builder.Task("load", _ => Task.CompletedTask);
        var output = builder.Group("output");
        var publish = output.Task("publish", _ => Task.CompletedTask).Requires(load).Limit("database");
        var reporter = new RecordingReporter();
        var journal = JournalWriteFailure.StartOf("output/publish").In(Journal.New(temporary.Path));
        var options = Options(journal, reporter) with { Limits = new Dictionary<string, int> { ["database"] = 1 } };

        var result = await ScriptedDriver.Start(builder.Build(), options).Finish();

        reporter.Lines.ShouldBe(
        [
            "run started",
            "load: pending -> ready",
            "load: ready -> running",
            "load: running -> succeeded",
            "output/publish: pending -> ready",
            "stop requested",
            "run ended: JournalWriteException",
        ]);
        ShouldHaveEndedUnrecorded(result, reporter);
        var task = result.Snapshot[publish.Path];
        (task.Status, task.Started).ShouldBe((NodeStatus.Ready, null));
        (result.Snapshot.RunningTasks.InUse, result.Snapshot.Limits["database"].InUse).ShouldBe((0, 0));
        var group = result.Snapshot[output.Path];
        (group.Status, group.Counts[NodeStatus.Ready], group.Counts[NodeStatus.Running]).ShouldBe((NodeStatus.Ready, 1, 0));
        (result.Snapshot.Counts[NodeStatus.Ready], result.Snapshot.Counts[NodeStatus.Running]).ShouldBe((1, 0));
        JournalEntries.Read(TheJournalIn(temporary)).ShouldBe(["header", "run started", "started load", "settled load"]);
        await ShouldBeListedAsDiedAndFree(temporary);
    }

    [Test]
    public async Task The_run_s_start_entry_failing_publishes_the_run_s_start_and_the_ready_events_and_starts_nothing()
    {
        using var temporary = new TemporaryDirectory();
        var builder = new GraphBuilder();
        var load = builder.Task("load", _ => Task.CompletedTask);
        var store = builder.Task("store", _ => Task.CompletedTask);
        builder.Task("publish", _ => Task.CompletedTask).Requires(load);
        var reporter = new RecordingReporter();
        var journal = JournalWriteFailure.RunStart().In(Journal.New(temporary.Path));

        var result = await ScriptedDriver.Start(builder.Build(), Options(journal, reporter)).Finish();

        reporter.Lines.ShouldBe(
        [
            "run started",
            "load: pending -> ready",
            "store: pending -> ready",
            "stop requested",
            "run ended: JournalWriteException",
        ]);
        reporter.Events.OfType<StopRequested>().Single().By.ShouldBe(StopRequester.Dagline);
        ShouldHaveEndedUnrecorded(result, reporter);
        (result.Snapshot[load.Path].Status, result.Snapshot[store.Path].Status).ShouldBe((NodeStatus.Ready, NodeStatus.Ready));
        JournalEntries.Read(TheJournalIn(temporary)).ShouldBe(["header"]);
    }

    [Test]
    public async Task A_checkpoint_save_whose_write_fails_throws_to_the_task_and_is_answered_before_the_stop_is_applied()
    {
        using var temporary = new TemporaryDirectory();
        ScriptedDriver? run = null;
        bool? stoppedWhenAnswered = null;
        Exception? thrownToTheTask = null;
        string? readBack = null;
        var builder = new GraphBuilder();
        var saves = builder.Task("saves", async context =>
        {
            var save = context.SaveCheckpointAsync("job-17", CancellationToken.None);
            _ = save.ContinueWith(
                _ => stoppedWhenAnswered = run!.Current.Stop is not null,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            try
            {
                await save;
            }
            catch (Exception thrown)
            {
                thrownToTheTask = thrown;
                readBack = context.Checkpoint;
                throw;
            }
        });
        var reporter = new RecordingReporter();
        var journal = JournalWriteFailure.CheckpointSaveOf("saves").In(Journal.New(temporary.Path));

        run = ScriptedDriver.Start(builder.Build(), Options(journal, reporter, TimeSpan.FromSeconds(30)));
        var result = await run.Finish();

        stoppedWhenAnswered.ShouldBe(false);
        var thrown = thrownToTheTask.ShouldBeOfType<JournalWriteException>();
        thrown.InnerException.ShouldBeOfType<IOException>();
        readBack.ShouldBe("");
        reporter.Lines.ShouldBe(
        [
            "run started",
            "saves: pending -> ready",
            "saves: ready -> running",
            "stop requested",
            "saves: running -> failed",
            "run ended: JournalWriteException",
        ]);
        reporter.Events.OfType<StopRequested>().Single().By.ShouldBe(StopRequester.Dagline);
        ShouldHaveEndedUnrecorded(result, reporter);
        result.JournalError.ShouldBe(thrown.InnerException);
        result.Snapshot[saves.Path].Status.ShouldBe(NodeStatus.Failed);
        JournalEntries.Read(TheJournalIn(temporary)).ShouldBe(["header", "run started", "started saves"]);
        await ShouldBeListedAsDiedAndFree(temporary);
    }

    [Test]
    public async Task A_save_made_after_an_earlier_write_failed_is_refused_with_that_failure_and_nothing_more_is_written()
    {
        using var temporary = new TemporaryDirectory();
        var fails = new PausedBody();
        var saver = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("fails", fails.Body);
        builder.Task("saver", saver.Body);
        var reporter = new RecordingReporter();
        var journal = JournalWriteFailure.SettleOf("fails").In(Journal.New(temporary.Path));
        var run = ScriptedDriver.Start(builder.Build(), Options(journal, reporter, TimeSpan.FromSeconds(30)));
        run.Pump();
        var context = await saver.Started;
        fails.Release();
        run.Pump();

        var save = context.SaveCheckpointAsync("job-18", CancellationToken.None);
        run.Pump();
        var refused = await Should.ThrowAsync<JournalWriteException>(save);
        saver.Release();
        var result = await run.Finish();

        refused.InnerException.ShouldBe(result.JournalError);
        result.JournalError.ShouldBeOfType<IOException>();
        context.Checkpoint.ShouldBe("");
        ShouldHaveEndedUnrecorded(result, reporter);
        JournalEntries.Read(TheJournalIn(temporary)).ShouldBe(["header", "run started", "started fails", "started saver"]);
    }

    [Test]
    public async Task The_run_s_end_entry_alone_failing_keeps_the_outcome_the_run_reached_and_the_error_is_kept_for_the_result()
    {
        using var temporary = new TemporaryDirectory();
        var builder = new GraphBuilder();
        var load = builder.Task("load", _ => Task.CompletedTask);
        builder.Task("publish", _ => Task.CompletedTask).Requires(load);
        var reporter = new RecordingReporter();
        var journal = JournalWriteFailure.EndOfRun().In(Journal.New(temporary.Path));

        var result = await ScriptedDriver.Start(builder.Build(), Options(journal, reporter)).Finish();

        result.Outcome.ShouldBe(RunOutcome.Complete);
        result.Unrecorded.ShouldBeNull();
        result.JournalError.ShouldBeOfType<IOException>();
        result.Snapshot.Outcome.ShouldBe(RunOutcome.Complete);
        var ended = reporter.Events.OfType<RunEnded>().Single();
        (ended.Outcome, ended.Error).ShouldBe((RunOutcome.Complete, null));
        reporter.Events.OfType<StopRequested>().ShouldBeEmpty();
        JournalEntries.Read(TheJournalIn(temporary)).ShouldBe(
            ["header", "run started", "started load", "settled load", "started publish", "settled publish"]);
        await ShouldBeListedAsDiedAndFree(temporary);
    }

    [Test]
    public async Task A_write_that_fails_after_the_app_s_own_stop_publishes_no_second_stop_and_the_snapshot_still_says_the_app_asked()
    {
        using var temporary = new TemporaryDirectory();
        var load = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("load", load.Body);
        var reporter = new RecordingReporter();
        var journal = JournalWriteFailure.SettleOf("load").In(Journal.New(temporary.Path));
        var run = ScriptedDriver.Start(builder.Build(), Options(journal, reporter, TimeSpan.FromSeconds(30)));
        run.Pump();
        run.Stop();
        run.Pump();

        load.Release();
        var result = await run.Finish();

        reporter.Lines.ShouldBe(
        [
            "run started",
            "load: pending -> ready",
            "load: ready -> running",
            "stop requested",
            "load: running -> succeeded",
            "run ended: JournalWriteException",
        ]);
        reporter.Events.OfType<StopRequested>().Single().By.ShouldBe(StopRequester.App);
        result.Snapshot.Stop!.By.ShouldBe(StopRequester.App);
        ShouldHaveEndedUnrecorded(result, reporter);
        JournalEntries.Read(TheJournalIn(temporary)).ShouldBe(["header", "run started", "started load"]);
        await ShouldBeListedAsDiedAndFree(temporary);
    }

    private static RunOptions Options(Journal journal, RecordingReporter reporter, TimeSpan? gracePeriod = null)
        => new()
        {
            Journal = journal,
            Reporters = [reporter],
            TimeProvider = new FakeTimeProvider(_runStart),
            GracePeriod = gracePeriod ?? TimeSpan.Zero,
        };

    /// <summary>
    /// The run ended unrecorded: its result holds the file system's failure, its end carries the same exception in place of a run
    /// outcome, and its final snapshot has an end and no run outcome.
    /// </summary>
    private static void ShouldHaveEndedUnrecorded(RunResult result, RecordingReporter reporter)
    {
        var unrecorded = result.Unrecorded.ShouldNotBeNull();
        unrecorded.InnerException.ShouldBeOfType<IOException>();
        result.JournalError.ShouldBe(unrecorded.InnerException);
        var ended = reporter.Events.OfType<RunEnded>().Single();
        (ended.Outcome, ended.Error).ShouldBe((null, unrecorded));
        unrecorded.Snapshot.ShouldBe(result.Snapshot);
        result.Snapshot.Ended.ShouldNotBeNull();
        result.Snapshot.Outcome.ShouldBeNull();
    }

    /// <summary>The journal holds no end, so it is listed as died, and the run has let go of it, so it is not in use.</summary>
    private static async Task ShouldBeListedAsDiedAndFree(TemporaryDirectory temporary)
    {
        var listed = (await Journals.ListAsync(temporary.Path)).Journals.ShouldHaveSingleItem();
        listed.Ending.ShouldBe(JournalEnding.Died);
        listed.LatestRun!.Ended.ShouldBeNull();
    }

    private static string TheJournalIn(TemporaryDirectory temporary) => Directory.GetFiles(temporary.Path, "*.journal").Single();
}
