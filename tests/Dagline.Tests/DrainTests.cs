using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace Dagline.Tests;

public class DrainTests
{
    private const int _repeats = 50;
    private static readonly DateTimeOffset _runStart = new(2026, 11, 11, 4, 12, 0, TimeSpan.Zero);

    [Test]
    public void A_new_signal_has_not_been_requested_and_a_request_is_kept()
    {
        var drain = new DrainSignal();

        drain.IsRequested.ShouldBeFalse();
        drain.Request();
        drain.Request();
        drain.IsRequested.ShouldBeTrue();
    }

    [Test]
    public async Task A_drain_lets_the_running_tasks_finish_unsignalled_and_starts_nothing_new()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var succeeds = new PausedBody();
            var fails = new PausedBody();
            var builder = new GraphBuilder();
            var succeedsTask = builder.Task("succeeds", succeeds.Body);
            var failsTask = builder.Task("fails", fails.Body);
            var queued = builder.Task("queued", _ => Task.CompletedTask);
            var then = builder.Task("then", _ => Task.CompletedTask).Requires(succeedsTask);
            var tidy = builder.Task("tidy", _ => Task.CompletedTask).After(failsTask);
            var drain = new DrainSignal();
            var reporter = new RecordingReporter();
            var watched = new WatchedReporter(reporter);

            var run = Runner.RunAsync(
                builder.Build(),
                new RunOptions { Journal = Journal.None, MaxRunningTasks = 2, Reporters = [watched] },
                drain);
            var succeedsContext = await succeeds.Started;
            var failsContext = await fails.Started;
            drain.Request();
            drain.Request();
            await watched.Handled("drain requested");
            succeeds.Release();
            fails.Throw(new InvalidOperationException("the store went away"));
            var result = await run;

            result.Outcome.ShouldBe(RunOutcome.Stopped);
            var lines = reporter.Lines.ToList();
            lines.Count(line => line == "drain requested").ShouldBe(1);
            lines.ShouldNotContain("stop requested");
            lines.Skip(lines.IndexOf("drain requested")).ShouldNotContain(line => line.EndsWith("ready -> running", StringComparison.Ordinal));
            succeedsContext.CancellationToken.IsCancellationRequested.ShouldBeFalse();
            failsContext.CancellationToken.IsCancellationRequested.ShouldBeFalse();
            result.Snapshot[succeedsTask.Path].Status.ShouldBe(NodeStatus.Succeeded);
            result.Snapshot[failsTask.Path].Status.ShouldBe(NodeStatus.Failed);
            result.Snapshot[then.Path].Status.ShouldBe(NodeStatus.Ready);
            result.Snapshot[tidy.Path].Status.ShouldBe(NodeStatus.Ready);
            result.Snapshot.NeverReached.Select(task => task.Path).ShouldBe([queued.Path, then.Path, tidy.Path]);
            result.Snapshot.Stop.ShouldBeNull();
        }
    }

    [Test]
    public async Task A_drain_in_which_every_running_task_succeeds_and_nothing_is_left_is_complete()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var load = new PausedBody();
            var builder = new GraphBuilder();
            builder.Task("load", load.Body);
            var drain = new DrainSignal();
            var watched = new WatchedReporter(new RecordingReporter());

            var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [watched] }, drain);
            await load.Started;
            drain.Request();
            await watched.Handled("drain requested");
            load.Release();
            var result = await run;

            result.Outcome.ShouldBe(RunOutcome.Complete);
            result.Snapshot.Drain.ShouldNotBeNull();
        }
    }

    [Test]
    public async Task A_cancellation_exception_from_a_task_during_a_drain_is_a_failure()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var load = new PausedBody();
            var builder = new GraphBuilder();
            var loadTask = builder.Task("load", load.Body);
            var drain = new DrainSignal();
            var watched = new WatchedReporter(new RecordingReporter());

            var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [watched] }, drain);
            await load.Started;
            drain.Request();
            await watched.Handled("drain requested");
            load.Throw(new OperationCanceledException("gave up"));
            var result = await run;

            result.Snapshot[loadTask.Path].Status.ShouldBe(NodeStatus.Failed);
        }
    }

    [Test]
    public async Task A_drained_run_on_a_journal_is_listed_stopped_and_a_continuing_run_keeps_its_successes_and_runs_the_rest()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            using var temporary = new TemporaryDirectory();
            var load = new PausedBody();
            var builder = new GraphBuilder();
            var loadTask = builder.Task("load", load.Body);
            var publish = builder.Task("publish", _ => Task.CompletedTask).Requires(loadTask);
            var graph = builder.Build();
            var drain = new DrainSignal();
            var watched = new WatchedReporter(new RecordingReporter());

            var run = Runner.RunAsync(graph, new RunOptions { Journal = QuickJournal.New(temporary.Path), Reporters = [watched] }, drain);
            await load.Started;
            drain.Request();
            await watched.Handled("drain requested");
            load.Release();
            var first = await run;
            var listed = (await Journals.ListAsync(temporary.Path)).Journals.ShouldHaveSingleItem();
            var next = await Runner.RunAsync(graph, new RunOptions { Journal = QuickJournal.Continue(temporary.Path, listed.Id) });

            first.Outcome.ShouldBe(RunOutcome.Stopped);
            first.Snapshot.NeverReached.Select(task => task.Path).ShouldBe([publish.Path]);
            listed.Ending.ShouldBe(JournalEnding.Stopped);
            listed.Counts[NodeStatus.Succeeded].ShouldBe(1);
            next.Outcome.ShouldBe(RunOutcome.Complete);
            next.Snapshot[loadTask.Path].IsEarlierSuccess.ShouldBeTrue();
            next.Snapshot[publish.Path].Status.ShouldBe(NodeStatus.Succeeded);
        }
    }

    [Test]
    public async Task A_drain_requested_before_the_call_starts_nothing_and_the_run_is_stopped()
    {
        var builder = new GraphBuilder();
        var load = builder.Task("load", _ => Task.CompletedTask);
        builder.Task("publish", _ => Task.CompletedTask).Requires(load);
        var drain = new DrainSignal();
        drain.Request();
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] }, drain);

        result.Outcome.ShouldBe(RunOutcome.Stopped);
        reporter.Lines.ShouldBe(["run started", "load: pending -> ready", "drain requested", "run ended: stopped"]);
        result.Snapshot.NeverReached.Count.ShouldBe(2);
    }

    [Test]
    public async Task A_signal_drained_in_one_run_drains_a_later_run_from_its_start()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var builder = new GraphBuilder();
            var load = new PausedBody();
            builder.Task("load", load.Body);
            var graph = builder.Build();
            var drain = new DrainSignal();
            var watched = new WatchedReporter(new RecordingReporter());

            var first = Runner.RunAsync(graph, new RunOptions { Journal = Journal.None, Reporters = [watched] }, drain);
            await load.Started;
            drain.Request();
            await watched.Handled("drain requested");
            load.Release();
            (await first).Outcome.ShouldBe(RunOutcome.Complete);

            var reporter = new RecordingReporter();
            var second = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.None, Reporters = [reporter] }, drain);

            second.Outcome.ShouldBe(RunOutcome.Stopped);
            reporter.Lines.ShouldBe(["run started", "load: pending -> ready", "drain requested", "run ended: stopped"]);
        }
    }

    [Test]
    public async Task The_snapshot_carries_the_drain_from_the_drain_requested_event_on()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var load = new PausedBody();
            var builder = new GraphBuilder();
            builder.Task("load", load.Body);
            var drain = new DrainSignal();
            var reporter = new RecordingReporter();
            var watched = new WatchedReporter(reporter);

            var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [watched] }, drain);
            await load.Started;
            drain.Request();
            await watched.Handled("drain requested");
            load.Release();
            var result = await run;

            var events = reporter.Events;
            var snapshots = reporter.Snapshots;
            var drainAt = events.ToList().FindIndex(runEvent => runEvent is DrainRequested);
            snapshots.Take(drainAt).ShouldAllBe(snapshot => snapshot.Drain == null);
            snapshots.Skip(drainAt).ShouldAllBe(snapshot => snapshot.Drain != null && snapshot.Drain.RequestedAt == events[drainAt].At);
            result.Snapshot.Drain.ShouldBe(snapshots[drainAt].Drain);
        }
    }

    [Test]
    public async Task A_stop_after_a_drain_is_published_after_it_and_signals_the_running_tasks()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var load = new StoppedBody();
            var builder = new GraphBuilder();
            var loadTask = builder.Task("load", load.Body);
            builder.Task("queued", _ => Task.CompletedTask);
            var drain = new DrainSignal();
            using var stop = new CancellationTokenSource();
            var reporter = new RecordingReporter();
            var watched = new WatchedReporter(reporter);

            var run = Runner.RunAsync(
                builder.Build(),
                new RunOptions { Journal = Journal.None, MaxRunningTasks = 1, Reporters = [watched] },
                drain,
                stop.Token);
            await load.Started;
            drain.Request();
            await watched.Handled("drain requested");
            await stop.CancelAsync();
            var result = await run;

            result.Outcome.ShouldBe(RunOutcome.Stopped);
            reporter.Lines.ShouldBe(
            [
                "run started",
                "load: pending -> ready",
                "queued: pending -> ready",
                "load: ready -> running",
                "drain requested",
                "stop requested",
                "load: running -> cancelled",
                "run ended: stopped",
            ]);
            result.Snapshot[loadTask.Path].Status.ShouldBe(NodeStatus.Cancelled);
            result.Snapshot.Drain.ShouldNotBeNull();
            result.Snapshot.Stop.ShouldNotBeNull();
        }
    }

    [Test]
    public async Task A_drain_requested_during_a_stop_changes_nothing()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var clock = new FakeTimeProvider(_runStart);
            var load = new PausedBody();
            var builder = new GraphBuilder();
            builder.Task("load", load.Body);
            var drain = new DrainSignal();
            using var stop = new CancellationTokenSource();
            var reporter = new RecordingReporter();
            var watched = new WatchedReporter(reporter);

            var run = Runner.RunAsync(
                builder.Build(),
                new RunOptions { Journal = Journal.None, TimeProvider = clock, Reporters = [watched] },
                drain,
                stop.Token);
            await load.Started;
            await stop.CancelAsync();
            await watched.Handled("stop requested");
            drain.Request();
            // The grace period's timer exists once the stop is heard. Moving the clock to its end in one step keeps it there while the
            // run lets go, so the grace end's time is the end of the grace period on either engine.
            clock.Advance(TimeSpan.FromSeconds(30));
            var result = await run;
            load.Release();

            reporter.Lines.ShouldNotContain("drain requested");
            reporter.Events.OfType<GraceEnded>().ShouldHaveSingleItem().At.ShouldBe(_runStart.AddSeconds(30));
            result.Snapshot.Drain.ShouldBeNull();
            result.Outcome.ShouldBe(RunOutcome.Stopped);
        }
    }

    [Test]
    public async Task A_drain_and_a_stop_both_made_before_the_call_are_both_published_the_drain_first()
    {
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask);
        var drain = new DrainSignal();
        drain.Request();
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] }, drain, stop.Token);

        result.Outcome.ShouldBe(RunOutcome.Stopped);
        reporter.Lines.ShouldBe(["run started", "load: pending -> ready", "drain requested", "stop requested", "run ended: stopped"]);
    }

    [Test]
    public async Task Invalid_options_still_throw_with_a_drain_and_a_stop_already_made()
    {
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask);
        var drain = new DrainSignal();
        drain.Request();
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();

        Should.Throw<RunConfigurationException>(
            () => { _ = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, MaxRunningTasks = 0 }, drain, stop.Token); });
    }
}
