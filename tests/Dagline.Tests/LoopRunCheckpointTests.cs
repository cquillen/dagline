using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace Dagline.Tests;

/// <summary>
/// A task's checkpoint (ADR 0015): a save is an input like any other, answered once its entry is written with
/// the run's durability. Each test is one scripted run that makes each thing happen in the order it means, so none is repeated.
/// </summary>
public class LoopRunCheckpointTests
{
    private static readonly DateTimeOffset _runStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan _gracePeriod = TimeSpan.FromSeconds(30);

    [Test]
    public async Task A_save_completes_only_once_the_loop_has_written_its_entry_flushed_to_the_disk_and_then_reads_back()
    {
        using var temporary = new TemporaryDirectory();
        var flushes = new JournalFlushes();
        var saver = new Saver("job-17");
        var run = ScriptedDriver.Start(saver.Graph, new RunOptions { Journal = flushes.In(Journal.New(temporary.Path)) });
        run.PumpLoop();
        run.PumpBodies();
        saver.Posted.IsCompleted.ShouldBeTrue();
        var save = await saver.Posted;
        var journal = Directory.GetFiles(temporary.Path, "*.journal").Single();
        var flushesBefore = flushes.Count;

        var completedBeforeTheLoop = save.IsCompleted;
        var entriesBeforeTheLoop = JournalEntries.Read(journal);
        run.PumpLoop();

        completedBeforeTheLoop.ShouldBeFalse();
        entriesBeforeTheLoop.ShouldNotContain("checkpoint saved export");
        save.IsCompletedSuccessfully.ShouldBeTrue();
        flushes.Count.ShouldBe(flushesBefore + 1);
        JournalEntries.Read(journal).ShouldContain("checkpoint saved export");
        var result = await run.Finish();
        result.Outcome.ShouldBe(RunOutcome.Complete);
        saver.ReadBack.ShouldBe("job-17");
    }

    [Test]
    public async Task A_save_on_a_journal_handed_to_the_operating_system_is_written_with_no_flush_before_it_completes()
    {
        using var temporary = new TemporaryDirectory();
        var flushes = new JournalFlushes();
        var export = new PausedBody();
        var run = ScriptedDriver.Start(OneTask(export), new RunOptions { Journal = flushes.In(QuickJournal.New(temporary.Path)) });
        run.Pump();
        var context = await export.Started;
        var flushesBefore = flushes.Count;

        var save = context.SaveCheckpointAsync("job-17");
        var completedBeforeTheLoop = save.IsCompleted;
        run.PumpLoop();

        completedBeforeTheLoop.ShouldBeFalse();
        save.IsCompletedSuccessfully.ShouldBeTrue();
        flushes.Count.ShouldBe(flushesBefore);
        JournalEntries.Read(Directory.GetFiles(temporary.Path, "*.journal").Single()).ShouldContain("checkpoint saved export");
        export.Release();
        (await run.Finish()).Outcome.ShouldBe(RunOutcome.Complete);
    }

    [Test]
    public async Task A_save_with_no_journal_is_an_input_too_and_the_checkpoint_reads_back_once_the_loop_has_answered_it()
    {
        var export = new PausedBody();
        var run = ScriptedDriver.Start(OneTask(export), new RunOptions { Journal = Journal.None });
        run.Pump();
        var context = await export.Started;

        var save = context.SaveCheckpointAsync("job-17");
        var completedBeforeTheLoop = save.IsCompleted;
        var readBeforeTheLoop = context.Checkpoint;
        run.PumpLoop();

        completedBeforeTheLoop.ShouldBeFalse();
        readBeforeTheLoop.ShouldBe("");
        save.IsCompletedSuccessfully.ShouldBeTrue();
        context.Checkpoint.ShouldBe("job-17");
        export.Release();
        (await run.Finish()).Outcome.ShouldBe(RunOutcome.Complete);
    }

    [Test]
    public async Task A_save_applied_before_the_grace_period_runs_out_succeeds_and_is_handed_back_in_the_next_run()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new FakeTimeProvider(_runStart);
        var export = new PausedBody();
        var run = ScriptedDriver.Start(OneTask(export), StoppingOptions(Journal.New(temporary.Path), clock));
        run.Pump();
        var context = await export.Started;
        run.Stop();
        run.Pump();

        var save = context.SaveCheckpointAsync("job-17", CancellationToken.None);
        run.Pump();
        var madeBeforeTheGracePeriodRanOut = save.IsCompletedSuccessfully;
        clock.Advance(_gracePeriod);
        var first = await run.Finish();

        madeBeforeTheGracePeriodRanOut.ShouldBeTrue();
        first.Snapshot[NodePath.Parse("export")].Status.ShouldBe(NodeStatus.Cancelled);
        (await HandedBack(temporary, first)).ShouldBe("job-17");
    }

    [Test]
    public async Task A_save_posted_before_the_grace_period_runs_out_and_applied_first_succeeds_and_is_handed_back_in_the_next_run()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new FakeTimeProvider(_runStart);
        var export = new PausedBody();
        var run = ScriptedDriver.Start(OneTask(export), StoppingOptions(Journal.New(temporary.Path), clock));
        run.Pump();
        var context = await export.Started;
        run.Stop();
        run.Pump();

        var save = context.SaveCheckpointAsync("job-17", CancellationToken.None);
        clock.Advance(_gracePeriod);
        var inFlightWhenTheGracePeriodRanOut = !save.IsCompleted;
        var first = await run.Finish();

        inFlightWhenTheGracePeriodRanOut.ShouldBeTrue();
        save.IsCompletedSuccessfully.ShouldBeTrue();
        first.Snapshot[NodePath.Parse("export")].Status.ShouldBe(NodeStatus.Cancelled);
        (await HandedBack(temporary, first)).ShouldBe("job-17");
    }

    [Test]
    public async Task A_save_posted_after_the_grace_period_has_run_out_throws_and_is_not_handed_back()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new FakeTimeProvider(_runStart);
        var export = new PausedBody();
        var run = ScriptedDriver.Start(OneTask(export), StoppingOptions(Journal.New(temporary.Path), clock));
        run.Pump();
        var context = await export.Started;
        run.Stop();
        run.Pump();

        clock.Advance(_gracePeriod);
        var save = context.SaveCheckpointAsync("job-17", CancellationToken.None);
        var clearing = context.ClearCheckpointAsync(CancellationToken.None);
        var first = await run.Finish();

        await Should.ThrowAsync<InvalidOperationException>(save);
        await Should.ThrowAsync<InvalidOperationException>(clearing);
        context.Checkpoint.ShouldBe("");
        first.Snapshot[NodePath.Parse("export")].Status.ShouldBe(NodeStatus.Cancelled);
        (await HandedBack(temporary, first)).ShouldBe("");
    }

    [Test]
    public async Task A_save_made_while_the_run_waits_for_reporters_is_answered_and_one_made_after_it_has_returned_is_refused_at_the_call()
    {
        TaskContext? kept = null;
        var builder = new GraphBuilder();
        builder.Task("export", async context =>
        {
            kept = context;
            await context.SaveCheckpointAsync("job-17");
        });
        var run = ScriptedDriver.Start(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [new RecordingReporter()] });
        while (run.PumpLoop() + run.PumpBodies() > 0)
        {
        }

        var endedBeforeTheSave = run.Current.Ended is not null;
        var returnedBeforeTheSave = run.Result.IsCompleted;
        var whileWaiting = kept!.SaveCheckpointAsync("job-18");
        var answeredBeforeTheLoop = whileWaiting.IsCompleted;
        run.PumpLoop();
        var answeredByTheLoop = whileWaiting.IsFaulted;
        var returnedOnceAnswered = run.Result.IsCompleted;
        await run.Finish();
        var afterTheReturn = kept.SaveCheckpointAsync("job-19");
        var answeredAtTheCall = afterTheReturn.IsFaulted;

        endedBeforeTheSave.ShouldBeTrue();
        returnedBeforeTheSave.ShouldBeFalse();
        answeredBeforeTheLoop.ShouldBeFalse();
        answeredByTheLoop.ShouldBeTrue();
        returnedOnceAnswered.ShouldBeFalse();
        await Should.ThrowAsync<InvalidOperationException>(whileWaiting);
        answeredAtTheCall.ShouldBeTrue();
        run.Pump().ShouldBe(0);
        await Should.ThrowAsync<InvalidOperationException>(afterTheReturn);
        kept.Checkpoint.ShouldBe("job-17");
    }

    [Test]
    public async Task A_fault_in_the_loop_answers_the_save_it_has_taken_with_the_fault_and_refuses_the_saves_still_queued()
    {
        var clock = new BreakingClock { FailAtRead = 2 };
        var export = new PausedBody();
        var run = ScriptedDriver.Start(OneTask(export), new RunOptions { Journal = Journal.None, TimeProvider = clock });
        run.Pump();
        var context = await export.Started;

        var taken = context.SaveCheckpointAsync("job-17");
        var queued = context.SaveCheckpointAsync("job-18");
        run.Pump();

        (await Should.ThrowAsync<InvalidOperationException>(run.Result)).Message.ShouldBe(BreakingClock.Failure);
        (await Should.ThrowAsync<InvalidOperationException>(taken)).Message.ShouldBe(BreakingClock.Failure);
        (await Should.ThrowAsync<InvalidOperationException>(queued)).Message.ShouldContain("the run has ended");
    }

    private static Graph OneTask(PausedBody export)
    {
        var builder = new GraphBuilder();
        builder.Task("export", export.Body);
        return builder.Build();
    }

    private static RunOptions StoppingOptions(Journal journal, TimeProvider clock)
        => new() { Journal = journal, TimeProvider = clock, GracePeriod = _gracePeriod };

    /// <summary>The checkpoint that a run continuing the first run's journal hands back to the task.</summary>
    private static async Task<string> HandedBack(TemporaryDirectory temporary, RunResult first)
    {
        var handedBack = "";
        var builder = new GraphBuilder();
        builder.Task("export", context =>
        {
            handedBack = context.Checkpoint;
            return Task.CompletedTask;
        });
        var next = await ScriptedDriver.Start(
            builder.Build(),
            new RunOptions { Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id) }).Finish();
        next.Outcome.ShouldBe(RunOutcome.Complete);
        return handedBack;
    }

    /// <summary>A task whose body saves one checkpoint, awaits the save, then reads the checkpoint back.</summary>
    private sealed class Saver
    {
        private readonly TaskCompletionSource<Task> _posted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Saver(string text)
        {
            var builder = new GraphBuilder();
            builder.Task("export", async context =>
            {
                var save = context.SaveCheckpointAsync(text);
                _posted.TrySetResult(save);
                await save;
                ReadBack = context.Checkpoint;
            });
            Graph = builder.Build();
        }

        public Graph Graph { get; }

        /// <summary>The save the body made, once it has made it.</summary>
        public Task<Task> Posted => _posted.Task;

        /// <summary>The checkpoint as the body read it once its save had completed.</summary>
        public string? ReadBack { get; private set; }
    }

    /// <summary>A clock whose read at <see cref="FailAtRead"/> throws, as a fault in Dagline itself would.</summary>
    private sealed class BreakingClock : TimeProvider
    {
        public const string Failure = "The clock broke.";

        private int _reads;

        public int? FailAtRead { get; init; }

        public override DateTimeOffset GetUtcNow()
            => ++_reads == FailAtRead ? throw new InvalidOperationException(Failure) : _runStart.AddSeconds(_reads);
    }
}
