using Dagline.TestSupport;

namespace Dagline.Tests;

public class KilledProcessTests
{
    [Test]
    [NotInParallel(DiskHeavy.Key)]
    public async Task Every_cut_of_a_real_journal_continues_with_exactly_the_successes_and_checkpoints_whose_entries_were_whole()
    {
        using var temporary = new TemporaryDirectory();
        var work = new Work();
        var first = new RecordingReporter();
        var second = new RecordingReporter();

        var firstRun = await Runner.RunAsync(work.Build(Phase.First), Sequential(Journal.New(temporary.Path), first));
        var id = firstRun.Snapshot.Journal!.Id;
        var secondRun = await Runner.RunAsync(work.Build(Phase.Second), Sequential(Journal.Continue(temporary.Path, id), second));
        var journalFile = Directory.GetFiles(temporary.Path, "*.journal").Single();
        List<Line> lines = [new("header"), .. work.LinesOf(first, Phase.First), .. work.LinesOf(second, Phase.Second)];

        firstRun.Snapshot[work.Notify].BlockedBy.ShouldBe(work.Check);
        secondRun.Snapshot.SetAside.ShouldBe([work.Summary]);
        File.ReadAllLines(journalFile).Length.ShouldBe(lines.Count);
        var cuts = JournalCutter.Cut(journalFile);
        cuts.Count.ShouldBe((lines.Count * 2) - 1);
        foreach (var cut in cuts)
        {
            await ContinuesAsExpected(cut, lines.Take(cut.WholeLines), work);
        }
    }

    [Test]
    public async Task A_journal_cut_before_its_run_ended_entry_is_listed_as_died()
    {
        using var temporary = new TemporaryDirectory();
        using var copies = new TemporaryDirectory();
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask);
        await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.New(temporary.Path) });
        var cuts = JournalCutter.Cut(Directory.GetFiles(temporary.Path, "*.journal").Single());

        cuts[^2].WriteTo(copies.Path);
        var summary = (await Journals.ListAsync(copies.Path)).ShouldHaveSingleItem();

        summary.Ending.ShouldBe(JournalEnding.Died);
        summary.LatestRun!.Ended.ShouldBeNull();
        summary.IsFinished.ShouldBeFalse();
    }

    [Test]
    public async Task A_new_journals_header_and_first_run_are_on_disk_before_any_task_starts()
    {
        using var temporary = new TemporaryDirectory();
        using var copies = new TemporaryDirectory();
        var firstRun = true;
        var builder = new GraphBuilder();
        builder.Task("load", _ =>
        {
            if (firstRun)
            {
                copies.CopyIn(Directory.GetFiles(temporary.Path, "*.journal").Single());
            }

            return Task.CompletedTask;
        });
        var graph = builder.Build();

        var first = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path, "all shards") });
        firstRun = false;
        var fromTheCopy = await Runner.RunAsync(
            graph,
            new RunOptions { Journal = Journal.Continue(copies.Path, first.Snapshot.Journal!.Id) });

        (fromTheCopy.Snapshot.Journal!.Id, fromTheCopy.Snapshot.Journal.Description, fromTheCopy.Snapshot.Journal.Run)
            .ShouldBe((first.Snapshot.Journal.Id, "all shards", 2));
    }

    private static async Task ContinuesAsExpected(JournalCut cut, IEnumerable<Line> wholeLines, Work work)
    {
        using var copy = new TemporaryDirectory();
        var id = Path.GetFileNameWithoutExtension(cut.WriteTo(copy.Path));
        var (successes, checkpoints) = Fold(wholeLines);
        if (!successes.Contains(work.Check.ToString()))
        {
            successes.Remove(work.Summary.ToString());
        }

        var reporter = new RecordingReporter();
        work.CheckpointsSeen.Clear();
        var continued = await Runner.RunAsync(work.Build(Phase.Continuing), Sequential(Journal.Continue(copy.Path, id), reporter));

        var because = $"after a cut at {cut}";
        continued.Outcome.ShouldBe(RunOutcome.Complete, because);
        continued.Snapshot.Nodes.Where(node => node.IsEarlierSuccess).Select(node => node.Path.ToString())
            .ShouldBe(successes, ignoreOrder: true, because);
        var started = reporter.Events.OfType<TaskStatusChanged>().Where(changed => changed.To is NodeStatus.Running)
            .Select(changed => changed.Node.Path.ToString());
        started.ShouldBe(work.Paths.Except(successes), ignoreOrder: true, because);
        foreach (var (path, seen) in work.CheckpointsSeen)
        {
            seen.ShouldBe(checkpoints.GetValueOrDefault(path, ""), $"{because}, the checkpoint of {path}");
        }

        var again = new RecordingReporter();
        var thirdRun = await Runner.RunAsync(work.Build(Phase.Continuing), Sequential(Journal.Continue(copy.Path, id), again));
        thirdRun.Outcome.ShouldBe(RunOutcome.Complete, because);
        again.Events.Count.ShouldBe(2, because);
    }

    // What a run that continues a journal keeps from its whole lines, by the rules of the spec: a success stands until a later settle
    // or set-aside, and a checkpoint until a later clearing or success.
    private static (HashSet<string> Successes, Dictionary<string, string> Checkpoints) Fold(IEnumerable<Line> wholeLines)
    {
        HashSet<string> successes = [];
        Dictionary<string, string> checkpoints = [];
        foreach (var line in wholeLines)
        {
            switch (line.Kind)
            {
                case "settled" when line.Status is NodeStatus.Succeeded:
                    successes.Add(line.Path!);
                    checkpoints.Remove(line.Path!);
                    break;
                case "settled" or "set aside":
                    successes.Remove(line.Path!);
                    break;
                case "saved":
                    checkpoints[line.Path!] = line.Text!;
                    break;
                case "cleared":
                    checkpoints.Remove(line.Path!);
                    break;
            }
        }

        return (successes, checkpoints);
    }

    // One task at a time, so that the order of the journal's lines follows the order of the run's events.
    private static RunOptions Sequential(Journal journal, IReporter reporter) => new() { Journal = journal, MaxRunningTasks = 1, Reporters = [reporter] };

    private enum Phase
    {
        First,
        Second,
        Continuing,
    }

    // What one line of the journal records, as the test knows it from the run's events and its task bodies, never from the file.
    private sealed record Line(string Kind, string? Path = null, NodeStatus? Status = null, string? Text = null);

    /// <summary>
    /// A graph whose first run has a failure that blocks a cascade, a saved and a cleared checkpoint and an <em>after</em> task that
    /// succeeds; whose second run sets that task aside; and whose continuing runs record the checkpoint each task is handed.
    /// </summary>
    private sealed class Work
    {
        private readonly Dictionary<(Phase, string), List<Line>> _bodyLines = [];

        public NodePath Check { get; } = NodePath.Parse("check");

        public NodePath Notify { get; } = NodePath.Parse("notify");

        public NodePath Summary { get; } = NodePath.Parse("summary");

        public IReadOnlyList<string> Paths { get; } = ["fetch", "check", "publish", "notify", "summary", "retry"];

        public Dictionary<string, string> CheckpointsSeen { get; } = [];

        public Graph Build(Phase phase)
        {
            var builder = new GraphBuilder();
            builder.Task("fetch", Body(phase, "fetch", async context => await Save(phase, context, "fetch-1")));
            var check = builder.Task("check", Body(phase, "check", async context =>
            {
                await Save(phase, context, $"check-{phase}");
                if (phase is Phase.First)
                {
                    throw context.Fail("check failed");
                }
            }));
            var publish = builder.Task("publish", Body(phase, "publish", _ => Task.CompletedTask)).Requires(check);
            builder.Task("notify", Body(phase, "notify", _ => Task.CompletedTask)).Requires(publish);
            builder.Task("summary", Body(phase, "summary", _ => Task.CompletedTask)).After(check);
            builder.Task("retry", Body(phase, "retry", async context =>
            {
                await Save(phase, context, $"retry-{phase}");
                if (phase is Phase.First)
                {
                    await Clear(phase, context);
                    throw context.Fail("retry failed");
                }
            }));

            return builder.Build();
        }

        /// <summary>The lines a run of the given phase wrote, in order, from the events it published and what its bodies did.</summary>
        public IEnumerable<Line> LinesOf(RecordingReporter reporter, Phase phase)
        {
            for (var i = 0; i < reporter.Events.Count; i++)
            {
                switch (reporter.Events[i])
                {
                    case RunStarted:
                        yield return new Line("run started");
                        foreach (var path in reporter.Snapshots[i].SetAside)
                        {
                            yield return new Line("set aside", path.ToString());
                        }

                        break;
                    case TaskStatusChanged { To: NodeStatus.Running } started:
                        yield return new Line("started", started.Node.Path.ToString());
                        foreach (var line in _bodyLines.GetValueOrDefault((phase, started.Node.Path.ToString()), []))
                        {
                            yield return line;
                        }

                        break;
                    case TaskStatusChanged { To: NodeStatus.Succeeded or NodeStatus.Failed or NodeStatus.Blocked or NodeStatus.Cancelled } settled:
                        yield return new Line("settled", settled.Node.Path.ToString(), settled.To);
                        break;
                    case RunEnded:
                        yield return new Line("run ended");
                        break;
                }
            }
        }

        private TaskBody Body(Phase phase, string path, TaskBody body)
        {
            if (phase is not Phase.Continuing)
            {
                return body;
            }

            return context =>
            {
                CheckpointsSeen[path] = context.Checkpoint;
                return Task.CompletedTask;
            };
        }

        private async Task Save(Phase phase, TaskContext context, string text)
        {
            await context.SaveCheckpointAsync(text);
            LinesFor(phase, context).Add(new Line("saved", context.Path.ToString(), Text: text));
        }

        private async Task Clear(Phase phase, TaskContext context)
        {
            await context.ClearCheckpointAsync();
            LinesFor(phase, context).Add(new Line("cleared", context.Path.ToString()));
        }

        private List<Line> LinesFor(Phase phase, TaskContext context)
        {
            var key = (phase, context.Path.ToString());
            return _bodyLines.TryGetValue(key, out var lines) ? lines : _bodyLines[key] = [];
        }
    }
}
