using Dagline.TestSupport;

namespace Dagline.Tests;

public class JournalDurabilityTests
{
    [Test]
    public async Task By_default_a_journal_is_flushed_to_the_disk_and_handed_to_the_operating_system_it_is_never_flushed()
    {
        using var flushed = new TemporaryDirectory();
        using var handed = new TemporaryDirectory();
        var byDefault = new JournalFlushes();
        var handedOnly = new JournalFlushes();

        await RunTwiceAsync(
            byDefault.In(Journal.New(flushed.Path)),
            id => byDefault.In(Journal.Continue(flushed.Path, id)));
        var second = await RunTwiceAsync(
            handedOnly.In(Journal.New(handed.Path, durability: JournalDurability.HandedToOperatingSystem)),
            id => handedOnly.In(Journal.Continue(handed.Path, id, JournalDurability.HandedToOperatingSystem)));

        // The header, the first run's start, its save, its settles and end, then the second run's start with its set-aside, its
        // settles and end: none of these is flushed when the journal is handed to the operating system only.
        byDefault.Count.ShouldBeGreaterThanOrEqualTo(8);
        handedOnly.Count.ShouldBe(0);
        second.Outcome.ShouldBe(RunOutcome.Complete);
        second.Snapshot.SetAside.Select(task => task.ToString()).ShouldBe(["summary"]);
    }

    [Test]
    public async Task Handed_to_the_operating_system_every_entry_is_in_the_file_at_the_moment_it_would_have_been_flushed()
    {
        using var temporary = new TemporaryDirectory();
        var journal = Journal.New(temporary.Path, durability: JournalDurability.HandedToOperatingSystem);
        IReadOnlyList<string> whenSummaryStarted = [];
        IReadOnlyList<string> afterSave = [];
        var builder = new GraphBuilder();
        var export = builder.Task("export", async context =>
        {
            await context.SaveCheckpointAsync("job-17");
            afterSave = JournalEntries.Read(JournalFileIn(temporary.Path));
        });
        builder.Task("summary", _ =>
        {
            whenSummaryStarted = JournalEntries.Read(JournalFileIn(temporary.Path));
            return Task.CompletedTask;
        }).Requires(export);

        await Runner.RunAsync(builder.Build(), new RunOptions { Journal = journal, MaxRunningTasks = 1 });

        afterSave.ShouldBe(["header", "run started", "started export", "checkpoint saved export"]);
        whenSummaryStarted.ShouldBe(["header", "run started", "started export", "checkpoint saved export", "settled export", "started summary"]);
        JournalEntries.Read(JournalFileIn(temporary.Path)).ShouldBe(
            ["header", "run started", "started export", "checkpoint saved export", "settled export", "started summary", "settled summary", "run ended"]);
    }

    [Test]
    public async Task A_run_that_continues_a_journal_chooses_its_durability_afresh()
    {
        using var temporary = new TemporaryDirectory();
        var flushes = new JournalFlushes();
        var builder = new GraphBuilder();
        var load = builder.Task("load", _ => Task.CompletedTask);
        var graph = builder.Build();

        var first = await Runner.RunAsync(
            graph,
            new RunOptions { Journal = flushes.In(Journal.New(temporary.Path, durability: JournalDurability.HandedToOperatingSystem)) });
        var flushedByTheFirst = flushes.Count;
        var next = await Runner.RunAsync(
            graph,
            new RunOptions { Journal = flushes.In(Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id)) });

        flushedByTheFirst.ShouldBe(0);
        flushes.Count.ShouldBeGreaterThan(0);
        next.Snapshot[load.Path].IsEarlierSuccess.ShouldBeTrue();
    }

    [Test]
    public void A_durability_that_is_not_one_of_the_choices_is_refused()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Journal.New("journals", durability: (JournalDurability)2));
        Should.Throw<ArgumentOutOfRangeException>(() => Journal.Continue("journals", "20261111T041200Z-a3f9c2", (JournalDurability)(-1)));
    }

    /// <summary>
    /// A first run whose <c>export</c> saves a checkpoint and fails, so that <c>summary</c>, after it, succeeds; then a run that
    /// continues it, sets <c>summary</c> aside, and runs both.
    /// </summary>
    private static async Task<RunResult> RunTwiceAsync(Journal journal, Func<string, Journal> continuing)
    {
        var firstRun = true;
        var builder = new GraphBuilder();
        var export = builder.Task("export", async context =>
        {
            if (firstRun)
            {
                await context.SaveCheckpointAsync("job-17");
                throw new InvalidOperationException("The export failed.");
            }
        });
        builder.Task("summary", _ => Task.CompletedTask).After(export);
        var graph = builder.Build();

        var first = await Runner.RunAsync(graph, new RunOptions { Journal = journal, MaxRunningTasks = 1 });
        firstRun = false;
        return await Runner.RunAsync(graph, new RunOptions { Journal = continuing(first.Snapshot.Journal!.Id), MaxRunningTasks = 1 });
    }

    private static string JournalFileIn(string directory) => Directory.GetFiles(directory, "*.journal").Single();
}
