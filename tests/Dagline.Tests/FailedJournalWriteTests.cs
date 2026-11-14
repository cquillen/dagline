using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace Dagline.Tests;

public class FailedJournalWriteTests
{
    private const int _repeats = 50;
    private static readonly DateTimeOffset _runStart = new(2026, 11, 11, 4, 12, 0, TimeSpan.Zero);
    private static readonly TimeSpan _step = TimeSpan.FromSeconds(1);

    [Test]
    [NotInParallel(DiskHeavy.Key)]
    public async Task A_settle_that_cannot_be_written_stops_the_run_and_the_run_ends_with_an_error()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            using var temporary = new TemporaryDirectory();
            var clock = new FakeTimeProvider(_runStart);
            var fails = new PausedBody();
            var slow = new PausedBody();
            var builder = new GraphBuilder();
            var failsTask = builder.Task("fails", fails.Body);
            var slowTask = builder.Task("slow", slow.Body);
            var later = builder.Task("later", _ => Task.CompletedTask).After(slowTask);
            var reporter = new RecordingReporter();
            var watched = new WatchedReporter(reporter);
            var journal = JournalWriteFailure.SettleOf("fails").In(Journal.New(temporary.Path));

            var run = Runner.RunAsync(
                builder.Build(),
                new RunOptions { Journal = journal, TimeProvider = clock, Reporters = [watched] });
            await fails.Started;
            await slow.Started;
            fails.Release();
            await watched.Handled("stop requested");
            await clock.AdvanceUntilAsync(run, _step);
            var error = await Should.ThrowAsync<JournalWriteException>(run);

            error.InnerException.ShouldBeOfType<IOException>();
            reporter.Lines.Skip(5).ShouldBe(
            [
                "fails: running -> succeeded",
                "stop requested",
                "stopped waiting: grace period over",
                "slow: running -> cancelled",
                "later: pending -> ready",
                "run ended: JournalWriteException",
            ]);
            ShouldHaveStoppedAndWaitedOutTheGracePeriod(reporter, error);
            (await slow.Started).CancellationToken.IsCancellationRequested.ShouldBeTrue();
            error.Snapshot[failsTask.Path].Status.ShouldBe(NodeStatus.Succeeded);
            error.Snapshot[later.Path].Status.ShouldBe(NodeStatus.Ready);
            error.Snapshot.Outcome.ShouldBeNull();

            JournalEntries.Read(TheJournalIn(temporary)).ShouldBe(["header", "run started", "started fails", "started slow"]);
            var listed = (await Journals.ListAsync(temporary.Path)).Journals.ShouldHaveSingleItem();
            listed.Ending.ShouldBe(JournalEnding.Died);
            (listed.Runs, listed.LatestRun!.Started, listed.LatestRun.Ended).ShouldBe((1, _runStart, null));
            slow.Release();
        }
    }

    [Test]
    [NotInParallel(DiskHeavy.Key)]
    public async Task A_checkpoint_save_that_cannot_be_written_throws_to_the_task_stops_the_run_and_the_run_ends_with_an_error()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            using var temporary = new TemporaryDirectory();
            var clock = new FakeTimeProvider(_runStart);
            var slow = new PausedBody();
            var saveThrew = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var builder = new GraphBuilder();
            var saves = builder.Task("saves", async context =>
            {
                await slow.Started;
                try
                {
                    await context.SaveCheckpointAsync("job-17");
                    saveThrew.SetResult(null);
                }
                catch (Exception thrown)
                {
                    saveThrew.SetResult(thrown);
                    throw;
                }
            });
            var slowTask = builder.Task("slow", slow.Body);
            var later = builder.Task("later", _ => Task.CompletedTask).After(slowTask);
            var graph = builder.Build();
            var reporter = new RecordingReporter();
            var watched = new WatchedReporter(reporter);
            var journal = JournalWriteFailure.CheckpointSaveOf("saves").In(Journal.New(temporary.Path));

            var run = Runner.RunAsync(graph, new RunOptions { Journal = journal, TimeProvider = clock, Reporters = [watched] });
            var thrownToTheTask = (await saveThrew.Task).ShouldBeOfType<JournalWriteException>();
            await watched.Handled("saves: running -> failed");
            await clock.AdvanceUntilAsync(run, _step);
            var error = await Should.ThrowAsync<JournalWriteException>(run);

            thrownToTheTask.InnerException.ShouldBeOfType<IOException>();
            error.InnerException.ShouldBe(thrownToTheTask.InnerException);
            reporter.Lines.Skip(5).ShouldBe(
            [
                "stop requested",
                "saves: running -> failed",
                "stopped waiting: grace period over",
                "slow: running -> cancelled",
                "later: pending -> ready",
                "run ended: JournalWriteException",
            ]);
            ShouldHaveStoppedAndWaitedOutTheGracePeriod(reporter, error);
            (await slow.Started).CancellationToken.IsCancellationRequested.ShouldBeTrue();
            error.Snapshot[saves.Path].Status.ShouldBe(NodeStatus.Failed);
            error.Snapshot[slowTask.Path].Status.ShouldBe(NodeStatus.Cancelled);
            error.Snapshot[later.Path].Status.ShouldBe(NodeStatus.Ready);
            error.Snapshot.Outcome.ShouldBeNull();

            JournalEntries.Read(TheJournalIn(temporary)).ShouldBe(["header", "run started", "started saves", "started slow"]);
            var listed = (await Journals.ListAsync(temporary.Path)).Journals.ShouldHaveSingleItem();
            listed.Ending.ShouldBe(JournalEnding.Died);
            (listed.Runs, listed.LatestRun!.Started, listed.LatestRun.Ended).ShouldBe((1, _runStart, null));
            slow.Release();
        }
    }

    [Test]
    public async Task A_run_end_that_cannot_be_written_leaves_the_run_its_outcome_and_the_journal_shows_the_run_as_having_died()
    {
        using var temporary = new TemporaryDirectory();
        var builder = new GraphBuilder();
        var first = builder.Task("first", _ => Task.CompletedTask);
        builder.Task("second", _ => Task.CompletedTask).After(first);
        var reporter = new RecordingReporter();
        var journal = JournalWriteFailure.EndOfRun().In(Journal.New(temporary.Path));

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = journal, Reporters = [reporter] });

        result.Outcome.ShouldBe(RunOutcome.Complete);
        var ended = reporter.Events.OfType<RunEnded>().Single();
        (ended.Outcome, ended.Error).ShouldBe((RunOutcome.Complete, null));
        reporter.Events.OfType<StopRequested>().ShouldBeEmpty();
        var listed = (await Journals.ListAsync(temporary.Path)).Journals.ShouldHaveSingleItem();
        listed.Ending.ShouldBe(JournalEnding.Died);
        (listed.Runs, listed.LatestRun!.Ended).ShouldBe((1, null));
        (listed.Counts.Settled, listed.Counts[NodeStatus.Succeeded]).ShouldBe((2, 2));
        JournalEntries.Read(TheJournalIn(temporary)).ShouldBe(
            ["header", "run started", "started first", "settled first", "started second", "settled second"]);
    }

    /// <summary>
    /// The stop a failed journal write asks for: Dagline asks at the start of the run with the default grace period, waits it out,
    /// and the run ends with <paramref name="error"/> in place of a run outcome.
    /// </summary>
    private static void ShouldHaveStoppedAndWaitedOutTheGracePeriod(RecordingReporter reporter, JournalWriteException error)
    {
        var requested = reporter.Events.OfType<StopRequested>().Single();
        (requested.At, requested.By, requested.GracePeriod).ShouldBe((_runStart, StopRequester.Dagline, TimeSpan.FromSeconds(30)));
        var graceEnded = reporter.Events.OfType<GraceEnded>().Single();
        (graceEnded.At, graceEnded.Reason).ShouldBe((_runStart.AddSeconds(30), GraceEndReason.GracePeriodOver));
        var ended = reporter.Events.OfType<RunEnded>().Single();
        (ended.Outcome, ended.Error).ShouldBe((null, error));
    }

    private static string TheJournalIn(TemporaryDirectory temporary) => Directory.GetFiles(temporary.Path, "*.journal").Single();
}
