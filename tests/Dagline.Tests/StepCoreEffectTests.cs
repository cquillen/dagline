using Dagline.TestSupport;

namespace Dagline.Tests;

/// <summary>
/// The ordered effects the new engine's step core gives for an input: what a whole run cannot show, that a record is the last effect
/// of its list and that what follows a write is decided only once its result is given. The core is driven directly, with no host,
/// no clock and no thread, through the core's opening to this project.
/// </summary>
public class StepCoreEffectTests
{
    private static readonly DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public void With_no_journal_the_start_and_a_body_s_end_each_publish_then_start_bodies_in_one_list()
    {
        var (graph, prepare, _) = Chain();
        var core = new StepCore(graph, Settings(graph), journal: null, new UnreadSource());

        Described(core, core.Apply(Input.Start, _now)).ShouldBe(
        [
            "publish run started",
            "publish prepare: pending -> ready",
            "publish prepare: ready -> running",
            "start prepare",
        ]);
        Described(core, core.Apply(Input.BodyEnded(prepare, TaskOutcome.Succeeded), _now)).ShouldBe(
        [
            "publish prepare: running -> succeeded",
            "publish load: pending -> ready",
            "publish load: ready -> running",
            "start load",
        ]);
    }

    [Test]
    public void On_a_journal_each_write_is_the_last_effect_and_what_follows_it_waits_for_its_result()
    {
        var (graph, prepare, load) = Chain();
        var core = new StepCore(graph, Settings(graph), JournalFold.Of(new HeaderEntry(1, "journal", _now), []), new UnreadSource());

        Described(core, core.Apply(Input.Start, _now)).ShouldBe(["record run started, flushed"]);
        Described(core, core.Apply(Input.Written(null), default)).ShouldBe(
        [
            "publish run started",
            "publish prepare: pending -> ready",
            "record started prepare, not flushed",
        ]);
        Described(core, core.Apply(Input.Written(null), default)).ShouldBe(
        [
            "publish prepare: ready -> running",
            "start prepare",
        ]);
        Described(core, core.Apply(Input.BodyEnded(prepare, TaskOutcome.Succeeded), _now)).ShouldBe(["record settled prepare, flushed"]);
        Described(core, core.Apply(Input.Written(null), default)).ShouldBe(
        [
            "publish prepare: running -> succeeded",
            "publish load: pending -> ready",
            "record started load, not flushed",
        ]);
        Described(core, core.Apply(Input.Written(null), default)).ShouldBe(["publish load: ready -> running", "start load"]);
        Described(core, core.Apply(Input.BodyEnded(load, TaskOutcome.Succeeded), _now)).ShouldBe(["record settled load, flushed"]);
        Described(core, core.Apply(Input.Written(null), default)).ShouldBe(
        [
            "publish load: running -> succeeded",
            "record run ended, flushed",
        ]);
        Described(core, core.Apply(Input.Written(null), default)).ShouldBe(
        [
            "close the journal",
            "publish run ended: complete",
            "end the run: complete",
        ]);
    }

    [Test]
    public void While_a_write_is_under_way_the_core_takes_no_input_but_its_result()
    {
        var (graph, prepare, _) = Chain();
        var core = new StepCore(graph, Settings(graph), JournalFold.Of(new HeaderEntry(1, "journal", _now), []), new UnreadSource());
        core.Apply(Input.Start, _now);

        Should.Throw<InvalidOperationException>(() => core.Apply(Input.BodyEnded(prepare, TaskOutcome.Succeeded), _now));
    }

    private static (Graph Graph, BuiltTask Prepare, BuiltTask Load) Chain()
    {
        var builder = new GraphBuilder();
        var prepare = builder.Task("prepare", _ => Task.CompletedTask);
        builder.Task("load", _ => Task.CompletedTask).Requires(prepare);
        var graph = builder.Build();
        return (graph, graph.Tasks[0], graph.Tasks[1]);
    }

    private static RunSettings Settings(Graph graph) => RunSettings.From(graph, new RunOptions { Journal = Journal.None });

    /// <summary>
    /// Each effect in words. The snapshot of each event is built as the loop builds it, in order, since each is built on the one
    /// before.
    /// </summary>
    private static List<string> Described(StepCore core, IReadOnlyList<Effect> effects)
    {
        List<string> described = [];
        foreach (var effect in effects)
        {
            if (effect.Kind is EffectKind.Publish)
            {
                core.SnapshotAfter(effect.Change);
            }

            described.Add(effect.Kind switch
            {
                EffectKind.Record => $"record {string.Join(", ", effect.Entries!.Select(Described))}, "
                    + (effect.Flush is JournalFlush.ToDisk ? "flushed" : "not flushed"),
                EffectKind.Publish => $"publish {EventLine.Describe(effect.Event!)}",
                EffectKind.StartBody => $"start {effect.Task!.Path}",
                EffectKind.CloseJournal => "close the journal",
                EffectKind.EndRun => $"end the run: {effect.Outcome.ToString().ToLowerInvariant()}",
                _ => effect.Kind.ToString(),
            });
        }

        return described;
    }

    private static string Described(JournalEntry entry) => entry switch
    {
        RunStartedEntry => "run started",
        TaskStartedEntry started => $"started {started.Path}",
        TaskSettledEntry settled => $"settled {settled.Path}",
        RunEndedEntry => "run ended",
        _ => entry.ToString(),
    };

    /// <summary>The run as reporters would read it, which no reporter does here: there is none.</summary>
    private sealed class UnreadSource : ISnapshotSource
    {
        public Snapshot Current => throw new InvalidOperationException("No reporter reads the run in these tests.");
    }
}
