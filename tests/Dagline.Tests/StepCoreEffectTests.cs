using Dagline.TestSupport;

namespace Dagline.Tests;

/// <summary>
/// The ordered effects the step core gives for an input: what a whole run cannot show, that a record is the last effect
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
    public void A_failure_s_settle_records_the_tasks_it_blocks_in_its_write_and_what_follows_waits_for_its_result()
    {
        var builder = new GraphBuilder();
        var batch = builder.Group("batch");
        var prepare = batch.Task("prepare", _ => Task.CompletedTask);
        var load = builder.Task("load", _ => Task.CompletedTask).Requires(prepare);
        builder.Task("publish", _ => Task.CompletedTask).Requires(load);
        builder.Task("cleanup", _ => Task.CompletedTask).After(batch);
        var graph = builder.Build();
        var core = new StepCore(graph, Settings(graph), JournalFold.Of(new HeaderEntry(1, "journal", _now), []), new UnreadSource());
        core.Apply(Input.Start, _now);
        core.Apply(Input.Written(null), default);
        Described(core, core.Apply(Input.Written(null), default)).ShouldBe(["publish batch/prepare: ready -> running", "start batch/prepare"]);

        var failure = TaskOutcome.UnexpectedFailure(new InvalidOperationException("the store is offline"));
        Described(core, core.Apply(Input.BodyEnded(graph.TaskAt(1), failure), _now)).ShouldBe(
            ["record settled batch/prepare, settled load, settled publish, flushed"]);
        Described(core, core.Apply(Input.Written(null), default)).ShouldBe(
        [
            "publish batch/prepare: running -> failed",
            "publish load: pending -> blocked",
            "publish publish: pending -> blocked",
            "publish cleanup: pending -> ready",
            "publish group batch settled: failed",
            "record started cleanup, not flushed",
        ]);
    }

    [Test]
    public void A_continuing_run_records_its_set_asides_with_its_start_and_its_earlier_successes_get_no_event()
    {
        var builder = new GraphBuilder();
        builder.Task("prepare", _ => Task.CompletedTask);
        var check = builder.Task("check", _ => Task.CompletedTask);
        builder.Task("load", _ => Task.CompletedTask).After(check);
        var graph = builder.Build();
        var earlier = JournalFold.Of(
            new HeaderEntry(1, "journal", _now),
            [Succeeded("prepare"), Succeeded("load"), Succeeded("gone"), new TaskSettledEntry("lost", _now, NodeStatus.Failed, [], 0, [], 0)]);
        var core = new StepCore(graph, Settings(graph), earlier, new UnreadSource());

        Described(core, core.Apply(Input.Start, _now)).ShouldBe(
            ["record run started with 2 paths not in the graph, 1 of them unsucceeded, set aside load, flushed"]);
        Described(core, core.Apply(Input.Written(null), default)).ShouldBe(
        [
            "publish run started",
            "publish check: pending -> ready",
            "record started check, not flushed",
        ]);
        Described(core, core.Apply(Input.Written(null), default)).ShouldBe(["publish check: ready -> running", "start check"]);
        Described(core, core.Apply(Input.BodyEnded(graph.TaskAt(1), TaskOutcome.Succeeded), _now)).ShouldBe(["record settled check, flushed"]);
        Described(core, core.Apply(Input.Written(null), default)).ShouldBe(
        [
            "publish check: running -> succeeded",
            "publish load: pending -> ready",
            "record started load, not flushed",
        ]);
    }

    [Test]
    public void A_steps_events_and_entries_share_the_time_of_its_input_across_its_writes()
    {
        var (graph, prepare, _) = Chain();
        var core = new StepCore(graph, Settings(graph), JournalFold.Of(new HeaderEntry(1, "journal", _now), []), new UnreadSource());
        var later = _now.AddMinutes(5);
        var notRead = _now.AddDays(1);
        core.Apply(Input.Start, _now);
        core.Apply(Input.Written(null), notRead);
        core.Apply(Input.Written(null), notRead);

        List<DateTimeOffset> times = [];
        foreach (var input in new[] { Input.BodyEnded(prepare, TaskOutcome.Succeeded), Input.Written(null), Input.Written(null) })
        {
            foreach (var effect in core.Apply(input, input.Kind is InputKind.Written ? notRead : later))
            {
                if (effect.Kind is EffectKind.Publish)
                {
                    core.SnapshotAfter(effect.Change);
                    times.Add(effect.Event!.At);
                }
                else if (effect.Kind is EffectKind.Record)
                {
                    times.AddRange(effect.Entries!.Select(TimeOf));
                }
            }
        }

        // The settle, its event, the dependent made ready, its start's entry and its start's event.
        times.Count.ShouldBe(5);
        times.ShouldAllBe(time => time == later);
    }

    [Test]
    public void While_a_write_is_under_way_the_core_takes_no_input_but_its_result()
    {
        var (graph, prepare, _) = Chain();
        var core = new StepCore(graph, Settings(graph), JournalFold.Of(new HeaderEntry(1, "journal", _now), []), new UnreadSource());
        core.Apply(Input.Start, _now);

        Should.Throw<InvalidOperationException>(() => core.Apply(Input.BodyEnded(prepare, TaskOutcome.Succeeded), _now));
    }

    [Test]
    public void A_report_that_is_an_event_is_published_any_other_marks_the_current_snapshot_out_of_date_and_once_settled_none_gives_anything()
    {
        var (graph, prepare, _) = Chain();
        var core = new StepCore(graph, Settings(graph), journal: null, new UnreadSource());
        core.Apply(Input.Start, _now);

        Described(core, core.Apply(Input.WarningKept(prepare, "entity 9 has no region"), _now))
            .ShouldBe(["publish warning prepare: entity 9 has no region"]);
        Described(core, core.Apply(Input.MessageSet(prepare, "checked 40 entities"), _now)).ShouldBe(["mark the current snapshot out of date"]);
        Described(core, core.Apply(Input.WaitOpened(prepare, "job 17 to finish"), _now)).ShouldBe(["publish waiting prepare: job 17 to finish"]);
        Described(core, core.Apply(Input.WaitOpened(prepare, "job 18 to finish"), _now)).ShouldBe(["mark the current snapshot out of date"]);
        core.Apply(Input.BodyEnded(prepare, TaskOutcome.Succeeded), _now);

        core.Apply(Input.WarningKept(prepare, "a late warning"), _now).ShouldBeEmpty();
        core.Apply(Input.MessageSet(prepare, "a late message"), _now).ShouldBeEmpty();
        core.Apply(Input.DetailKept(prepare, ["a late line"]), _now).ShouldBeEmpty();
        core.Apply(Input.FailureReported(prepare, "a late failure", []), _now).ShouldBeEmpty();
        core.Apply(Input.WaitOpened(prepare, "a late wait"), _now).ShouldBeEmpty();
        core.Apply(Input.WaitClosed(prepare), _now).ShouldBeEmpty();
    }

    [Test]
    public void A_stop_signals_the_tasks_and_starts_the_grace_period_before_the_stop_is_published()
    {
        var (graph, _, _) = Chain();
        var core = new StepCore(graph, Settings(graph, TimeSpan.FromMinutes(1)), journal: null, new UnreadSource());
        Described(core, core.Apply(Input.Start, _now));

        Described(core, core.Apply(Input.Stop(StopRequester.App), _now)).ShouldBe(
        [
            "signal the tasks",
            "start the grace period",
            "publish stop requested",
        ]);
    }

    [Test]
    public void A_stop_with_a_zero_grace_period_publishes_the_grace_end_before_the_write_of_the_tasks_it_lets_go_of()
    {
        var (graph, _, _) = Chain();
        var core = new StepCore(graph, Settings(graph, TimeSpan.Zero), JournalFold.Of(new HeaderEntry(1, "journal", _now), []), new UnreadSource());
        core.Apply(Input.Start, _now);
        core.Apply(Input.Written(null), default);
        Described(core, core.Apply(Input.Written(null), default));

        Described(core, core.Apply(Input.Stop(StopRequester.App), _now)).ShouldBe(
        [
            "signal the tasks",
            "publish stop requested",
            "publish grace period over",
            "record settled prepare, flushed",
        ]);
        Described(core, core.Apply(Input.Written(null), default)).ShouldBe(
        [
            "publish prepare: running -> cancelled",
            "record run ended, flushed",
        ]);
        Described(core, core.Apply(Input.Written(null), default)).ShouldBe(
        [
            "close the journal",
            "publish run ended: stopped",
            "end the run: stopped",
        ]);
    }

    [Test]
    public void A_start_write_that_fails_starts_no_body_and_the_stop_in_its_place_writes_nothing_before_the_end()
    {
        var (graph, prepare, _) = Chain();
        var core = new StepCore(graph, Settings(graph), JournalFold.Of(new HeaderEntry(1, "journal", _now), []), new UnreadSource());
        core.Apply(Input.Start, _now);
        Described(core, core.Apply(Input.Written(null), default)).ShouldBe(
            ["publish run started", "publish prepare: pending -> ready", "record started prepare, not flushed"]);

        var effects = Described(core, core.Apply(Input.Written(new IOException("disk full")), default));

        effects.ShouldBe(
        [
            "signal the tasks",
            "publish stop requested",
            "close the journal",
            "publish run ended: JournalWriteException",
            "end the run: stopped",
        ]);
        var ended = core.TakeSnapshot();
        (ended[prepare.Path].Status, ended.RunningTasks.InUse, ended.Outcome).ShouldBe((NodeStatus.Ready, 0, null));
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

    private static RunSettings Settings(Graph graph, TimeSpan gracePeriod)
        => RunSettings.From(graph, new RunOptions { Journal = Journal.None, GracePeriod = gracePeriod });

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
                EffectKind.SignalTheTasks => "signal the tasks",
                EffectKind.StartGracePeriod => "start the grace period",
                EffectKind.EndRun => $"end the run: {effect.Outcome.ToString().ToLowerInvariant()}",
                EffectKind.MarkCurrentOutOfDate => "mark the current snapshot out of date",
                _ => effect.Kind.ToString(),
            });
        }

        return described;
    }

    private static string Described(JournalEntry entry) => entry switch
    {
        RunStartedEntry { PathsNotInGraph: 0 } => "run started",
        RunStartedEntry started
            => $"run started with {started.PathsNotInGraph} paths not in the graph, {started.PathsNotInGraphUnsucceeded} of them unsucceeded",
        SetAsideEntry setAside => $"set aside {setAside.Path}",
        TaskStartedEntry started => $"started {started.Path}",
        TaskSettledEntry settled => $"settled {settled.Path}",
        RunEndedEntry => "run ended",
        _ => entry.ToString(),
    };

    private static DateTimeOffset TimeOf(JournalEntry entry) => entry switch
    {
        TaskStartedEntry started => started.Time,
        TaskSettledEntry settled => settled.Time,
        _ => throw new InvalidOperationException($"No time is read from {entry}."),
    };

    private static TaskSettledEntry Succeeded(string path) => new(path, _now, NodeStatus.Succeeded, [], 0, [], 0);

    /// <summary>The run as reporters would read it, which no reporter does here: there is none.</summary>
    private sealed class UnreadSource : ISnapshotSource
    {
        public Snapshot Current => throw new InvalidOperationException("No reporter reads the run in these tests.");
    }
}
