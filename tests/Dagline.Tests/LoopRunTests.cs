using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace Dagline.Tests;

/// <summary>
/// A chain of tasks run by the one loop that owns a run (ADR 0015): scripted on the test's own thread, and on the thread pool, each
/// giving the same events, snapshots and journal for the same graph.
/// </summary>
public class LoopRunTests
{
    private static readonly string[] _chainLines =
    [
        "run started",
        "prepare: pending -> ready",
        "prepare: ready -> running",
        "prepare: running -> succeeded",
        "load: pending -> ready",
        "load: ready -> running",
        "load: running -> succeeded",
        "publish: pending -> ready",
        "publish: ready -> running",
        "publish: running -> succeeded",
        "run ended: complete",
    ];

    [Test]
    public async Task A_scripted_chain_runs_to_complete_on_the_test_thread_with_each_event_in_order()
    {
        var reporter = new RecordingReporter();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var run = ScriptedDriver.Start(Chain(), new RunOptions { Journal = Journal.None, Reporters = [reporter], TimeProvider = clock });

        var result = await run.Finish();

        result.Outcome.ShouldBe(RunOutcome.Complete);
        result.ReporterErrors.ShouldBeEmpty();
        reporter.Lines.ShouldBe(_chainLines);
        result.Snapshot.Outcome.ShouldBe(RunOutcome.Complete);
        result.Snapshot.Counts[NodeStatus.Succeeded].ShouldBe(3);
        result.Snapshot.Nodes.ShouldAllBe(task => task.Status == NodeStatus.Succeeded);
        reporter.Snapshots[^1].ShouldBeSameAs(result.Snapshot);
        run.Current.ShouldBeSameAs(result.Snapshot);
    }

    [Test]
    public async Task A_body_s_end_is_posted_to_the_inbox_and_applied_only_when_the_loop_is_pumped()
    {
        var prepare = new PausedBody();
        var builder = new GraphBuilder();
        var prepareTask = builder.Task("prepare", prepare.Body);
        builder.Task("load", _ => Task.CompletedTask).Requires(prepareTask);
        var reporter = new RecordingReporter();
        var run = ScriptedDriver.Start(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        run.Pump();
        run.Current[prepareTask.Path].Status.ShouldBe(NodeStatus.Running);
        prepare.Release();
        run.PumpBodies().ShouldBe(1);
        var beforeTheLoop = run.Current;

        beforeTheLoop[prepareTask.Path].Status.ShouldBe(NodeStatus.Running);
        run.PumpLoop().ShouldBe(1);
        run.Current[prepareTask.Path].Status.ShouldBe(NodeStatus.Succeeded);
        (await run.Finish()).Outcome.ShouldBe(RunOutcome.Complete);
    }

    [Test]
    public async Task The_loop_reads_the_clock_once_for_each_input_it_takes()
    {
        var reporter = new RecordingReporter();
        var clock = new CountingClock();
        var run = ScriptedDriver.Start(Chain(), new RunOptions { Journal = Journal.None, Reporters = [reporter], TimeProvider = clock });

        await run.Finish();

        // The start, three bodies' ends, and the reporters being done.
        clock.Reads.ShouldBe(5);
        var times = reporter.Events.Select(runEvent => runEvent.At - CountingClock.First).ToList();
        times.ShouldBe(
        [
            Seconds(0), Seconds(0), Seconds(0),
            Seconds(1), Seconds(1), Seconds(1),
            Seconds(2), Seconds(2), Seconds(2),
            Seconds(3), Seconds(3),
        ]);
    }

    [Test]
    public async Task The_same_chain_runs_to_complete_on_the_thread_pool()
    {
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(Chain(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        result.Outcome.ShouldBe(RunOutcome.Complete);
        reporter.Lines.ShouldBe(_chainLines);
        reporter.Snapshots[^1].ShouldBeSameAs(result.Snapshot);
    }

    [Test]
    public async Task The_events_and_the_snapshot_of_each_are_the_same_scripted_and_on_the_thread_pool()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var scripted = new RecordingReporter();
        var onThePool = new RecordingReporter();
        var graph = Chain();

        await ScriptedDriver.Start(graph, new RunOptions { Journal = Journal.None, Reporters = [scripted], TimeProvider = clock }).Finish();
        await Runner.RunAsync(graph, new RunOptions { Journal = Journal.None, Reporters = [onThePool], TimeProvider = clock });

        scripted.Lines.ShouldBe(_chainLines);
        Described(onThePool).ShouldBe(Described(scripted));
    }

    [Test]
    public async Task A_chain_on_a_new_journal_records_the_same_scripted_and_on_the_thread_pool()
    {
        using var onThePoolDirectory = new TemporaryDirectory();
        using var scriptedDirectory = new TemporaryDirectory();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var onThePool = new RecordingReporter();
        var scripted = new RecordingReporter();
        var graph = Chain();

        await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(onThePoolDirectory.Path), Reporters = [onThePool], TimeProvider = clock });
        var result = await ScriptedDriver
            .Start(graph, new RunOptions { Journal = Journal.New(scriptedDirectory.Path), Reporters = [scripted], TimeProvider = clock })
            .Finish();

        result.Outcome.ShouldBe(RunOutcome.Complete);
        Described(scripted).ShouldBe(Described(onThePool));
        LinesAfterTheHeader(scriptedDirectory).ShouldBe(LinesAfterTheHeader(onThePoolDirectory));
        JournalEntries.Read(TheJournalIn(scriptedDirectory)).ShouldBe(
            ["header", "run started", "started prepare", "settled prepare", "started load", "settled load", "started publish", "settled publish", "run ended"]);
    }

    [Test]
    public async Task A_journal_is_continued_entry_for_entry_the_same_scripted_and_on_the_thread_pool()
    {
        using var first = new TemporaryDirectory();
        using var onThePoolDirectory = new TemporaryDirectory();
        using var scriptedDirectory = new TemporaryDirectory();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var checkFails = true;
        var before = new GraphBuilder();
        before.Task("prepare", _ => Task.CompletedTask);
        var check = before.Task("check", _ => checkFails ? throw new InvalidOperationException("bad data") : Task.CompletedTask);
        before.Task("load", _ => Task.CompletedTask).After(check);
        before.Task("gone", _ => Task.CompletedTask);
        var id = (await Runner.RunAsync(before.Build(), new RunOptions { Journal = Journal.New(first.Path), TimeProvider = clock }))
            .Snapshot.Journal!.Id;
        onThePoolDirectory.CopyIn(TheJournalIn(first));
        scriptedDirectory.CopyIn(TheJournalIn(first));
        checkFails = false;
        clock.Advance(Seconds(60));

        // The same graph less one task: prepare is an earlier success, load is set aside and gone is not in the graph.
        var after = new GraphBuilder();
        after.Task("prepare", _ => Task.CompletedTask);
        var checkAgain = after.Task("check", _ => Task.CompletedTask);
        after.Task("load", _ => Task.CompletedTask).After(checkAgain);
        var graph = after.Build();
        var onThePool = new RecordingReporter();
        var scripted = new RecordingReporter();
        await Runner.RunAsync(
            graph,
            new RunOptions { Journal = Journal.Continue(onThePoolDirectory.Path, id), Reporters = [onThePool], TimeProvider = clock });
        var result = await ScriptedDriver
            .Start(graph, new RunOptions { Journal = Journal.Continue(scriptedDirectory.Path, id), Reporters = [scripted], TimeProvider = clock })
            .Finish();

        result.Outcome.ShouldBe(RunOutcome.Complete);
        result.Snapshot.Counts.EarlierSuccesses.ShouldBe(1);
        result.Snapshot.SetAside.Select(path => path.ToString()).ShouldBe(["load"]);
        result.Snapshot.JournalPathsNotInGraph.ShouldBe(1);
        Described(scripted).ShouldBe(Described(onThePool));
        LinesAfterTheHeader(scriptedDirectory).ShouldBe(LinesAfterTheHeader(onThePoolDirectory));
        JournalEntries.Read(TheJournalIn(scriptedDirectory)).Skip(11).ShouldBe(
            ["run started", "set aside load", "started check", "settled check", "started load", "settled load", "run ended"]);
    }

    [Test]
    public async Task The_compatibility_journal_is_continued_the_same_scripted_and_on_the_thread_pool()
    {
        // The journal JournalCompatibilityTests holds, continued with bodies that read no checkpoint: handing one back is #150's.
        const string id = "20261111T041200Z-fa4fa4";
        using var onThePoolDirectory = new TemporaryDirectory();
        using var scriptedDirectory = new TemporaryDirectory();
        var compatibilityJournal = Path.Combine(AppContext.BaseDirectory, "CompatibilityJournals", $"{id}.journal");
        onThePoolDirectory.CopyIn(compatibilityJournal);
        scriptedDirectory.CopyIn(compatibilityJournal);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero));
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1");
        var load = shard.Task("load", _ => throw new InvalidOperationException("an earlier success runs again"));
        var export = shard.Task("export", _ => Task.CompletedTask).Requires(load);
        builder.Task("report", _ => Task.CompletedTask).Requires(export);
        var graph = builder.Build();
        var onThePool = new RecordingReporter();
        var scripted = new RecordingReporter();

        await Runner.RunAsync(
            graph,
            new RunOptions { Journal = Journal.Continue(onThePoolDirectory.Path, id), Reporters = [onThePool], TimeProvider = clock });
        var result = await ScriptedDriver
            .Start(graph, new RunOptions { Journal = Journal.Continue(scriptedDirectory.Path, id), Reporters = [scripted], TimeProvider = clock })
            .Finish();

        result.Outcome.ShouldBe(RunOutcome.Complete);
        result.Snapshot[load.Path].IsEarlierSuccess.ShouldBeTrue();
        result.Snapshot[load.Path].Message.ShouldBe("loaded 3 files");
        result.Snapshot.Journal!.Run.ShouldBe(2);
        Described(scripted).ShouldBe(Described(onThePool));
        LinesAfterTheHeader(scriptedDirectory).ShouldBe(LinesAfterTheHeader(onThePoolDirectory));
    }

    [Test]
    public async Task A_reporter_that_hears_the_run_ended_can_continue_the_journal_at_once()
    {
        using var directory = new TemporaryDirectory();
        var continuer = new ContinuingReporter(directory.Path);
        var driver = ScriptedDriver.Start(Chain(), new RunOptions { Journal = Journal.New(directory.Path), Reporters = [continuer] });

        var result = await driver.Finish();

        continuer.ContinuedRun.ShouldBe(2);
        result.Outcome.ShouldBe(RunOutcome.Complete);
    }

    [Test]
    public async Task A_fault_inside_the_loop_faults_the_result_and_closes_the_inbox()
    {
        var clock = new CountingClock { FailAtRead = 2 };
        var run = ScriptedDriver.Start(Chain(), new RunOptions { Journal = Journal.None, TimeProvider = clock });

        run.Pump();

        var fault = await Should.ThrowAsync<InvalidOperationException>(run.Result);
        fault.Message.ShouldBe(CountingClock.Failure);
        run.InboxIsClosed.ShouldBeTrue();
    }

    [Test]
    public async Task A_fault_inside_the_loop_lets_go_of_the_journal_so_that_a_later_run_can_continue_it()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new CountingClock { FailAtRead = 3 };
        var run = ScriptedDriver.Start(Chain(), new RunOptions { Journal = Journal.New(temporary.Path), TimeProvider = clock });

        run.Pump();

        await Should.ThrowAsync<InvalidOperationException>(run.Result);
        var listed = (await Journals.ListAsync(temporary.Path)).Journals.ShouldHaveSingleItem();
        listed.Ending.ShouldBe(JournalEnding.Died);
    }

    private static Graph Chain()
    {
        var builder = new GraphBuilder();
        var prepare = builder.Task("prepare", _ => Task.CompletedTask);
        var load = builder.Task("load", async _ => await Task.Yield()).Requires(prepare);
        builder.Task("publish", _ => Task.CompletedTask).Requires(load);
        return builder.Build();
    }

    /// <summary>Each event with its time and everything its snapshot shows of the run and of each task, as one line.</summary>
    private static List<string> Described(RecordingReporter reporter)
        => [.. reporter.Events.Zip(reporter.Snapshots, (runEvent, snapshot) => $"{EventLine.Describe(runEvent)} at {runEvent.At:O} | {Described(snapshot)}")];

    private static string Described(Snapshot snapshot)
    {
        var tasks = snapshot.Nodes.Select(task => $"{task.Path}={task.Status} {task.Started:O}-{task.Settled:O} {Described(task.Counts)}");
        return $"{snapshot.Started:O} {snapshot.Ended:O} {snapshot.Outcome} {Described(snapshot.Counts)} running {snapshot.RunningTasks} "
            + $"on a journal {snapshot.Journal is not null} | {string.Join("; ", tasks)}";
    }

    private static string Described(TaskCounts counts)
        => $"{string.Join(",", counts.ByStatus().Select(count => $"{count.Key}:{count.Value}"))} waiting:{counts.Waiting}";

    private static string TheJournalIn(TemporaryDirectory directory) => Directory.GetFiles(directory.Path, "*.journal").Single();

    // The header holds the journal's id, which differs from one journal to the next.
    private static string[] LinesAfterTheHeader(TemporaryDirectory directory) => [.. File.ReadAllLines(TheJournalIn(directory)).Skip(1)];

    private static TimeSpan Seconds(int seconds) => TimeSpan.FromSeconds(seconds);

    /// <summary>A reporter that, on hearing the run ended, continues the run's journal with a run of its own.</summary>
    private sealed class ContinuingReporter(string directory) : IReporter
    {
        public int? ContinuedRun { get; private set; }

        public async ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken)
        {
            if (runEvent is RunEnded)
            {
                var continued = await ScriptedDriver
                    .Start(Chain(), new RunOptions { Journal = Journal.Continue(directory, snapshot.Journal!.Id) }, cancellationToken)
                    .Finish();
                ContinuedRun = continued.Snapshot.Journal!.Run;
            }
        }
    }

    /// <summary>A clock that moves on one second at each read and counts the reads; it can be told to throw at one of them.</summary>
    private sealed class CountingClock : TimeProvider
    {
        public const string Failure = "The clock broke.";

        public static readonly DateTimeOffset First = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public int Reads { get; private set; }

        public int? FailAtRead { get; init; }

        public override DateTimeOffset GetUtcNow()
        {
            Reads++;
            return Reads == FailAtRead ? throw new InvalidOperationException(Failure) : First.AddSeconds(Reads - 1);
        }
    }
}
