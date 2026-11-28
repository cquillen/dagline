using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace Dagline.Tests;

/// <summary>
/// A journal write that fails, on the new engine (ADR 0015): the core knows the write's result before it decides what follows it,
/// so nothing starts after a failed write, Dagline applies a stop unless one has been applied, and the run ends unrecorded, with
/// no end entry, so the journal lists it as died. Each test is one scripted run that fails one write through the journal write
/// hook, so none is repeated.
/// </summary>
/// <remarks>
/// Until the cutover (#153) the outcome unrecorded is held internally, on <see cref="RunResult.Unrecorded"/>, and the run's end
/// carries the <see cref="JournalWriteException"/> in place of a run outcome, as on the existing engine.
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
