using Dagline.TestSupport;

namespace Dagline.Tests;

public class ReporterTests
{
    [Test]
    public async Task A_paused_reporter_does_not_delay_the_run_and_the_run_returns_once_it_has_handled_every_event()
    {
        var builder = new GraphBuilder();
        var prepare = builder.Task("prepare", _ => Task.CompletedTask);
        builder.Task("publish", _ => Task.CompletedTask).Requires(prepare);
        var paused = new PausedReporter();
        var watching = new RecordingReporter();

        var run = Runner.RunAsync(
            builder.Build(),
            new RunOptions { Journal = Journal.None, Reporters = [paused, watching] });
        await paused.Paused;
        await watching.Ended;
        var handedWhilePaused = paused.Lines;
        var returnedWhilePaused = run.IsCompleted;
        paused.Release();
        var result = await run;

        result.Outcome.ShouldBe(RunOutcome.Complete);
        watching.Lines.ShouldBe(
        [
            "run started",
            "prepare: pending -> ready",
            "prepare: ready -> running",
            "prepare: running -> succeeded",
            "publish: pending -> ready",
            "publish: ready -> running",
            "publish: running -> succeeded",
            "run ended: complete",
        ]);
        handedWhilePaused.ShouldBe(["run started"]);
        returnedWhilePaused.ShouldBeFalse();
        paused.Lines.ShouldBe(watching.Lines);
        result.ReporterErrors.ShouldBeEmpty();
    }

    [Test]
    public async Task A_reporter_that_throws_is_dropped_and_its_error_is_on_the_result()
    {
        var broken = new InvalidOperationException("the reporter is broken");
        var builder = new GraphBuilder();
        var prepare = builder.Task("prepare", _ => Task.CompletedTask);
        builder.Task("publish", _ => Task.CompletedTask).Requires(prepare);
        var failing = new FailingReporter("prepare: ready -> running", () => throw broken);
        var watching = new RecordingReporter();

        var result = await Runner.RunAsync(
            builder.Build(),
            new RunOptions { Journal = Journal.None, Reporters = [failing, watching] });

        result.Outcome.ShouldBe(RunOutcome.Complete);
        result.Snapshot.Counts[NodeStatus.Succeeded].ShouldBe(2);
        watching.Lines.ShouldBe(
        [
            "run started",
            "prepare: pending -> ready",
            "prepare: ready -> running",
            "prepare: running -> succeeded",
            "publish: pending -> ready",
            "publish: ready -> running",
            "publish: running -> succeeded",
            "run ended: complete",
        ]);
        failing.Lines.ShouldBe(["run started", "prepare: pending -> ready", "prepare: ready -> running"]);
        var error = result.ReporterErrors.ShouldHaveSingleItem();
        error.Reporter.ShouldBeSameAs(failing);
        error.Event.ShouldBeSameAs(watching.Events[2]);
        error.Exception.ShouldBeSameAs(broken);
    }

    [Test]
    public async Task A_reporter_whose_returned_task_faults_is_dropped_and_its_error_is_on_the_result()
    {
        var broken = new InvalidOperationException("the reporter is broken");
        var builder = new GraphBuilder();
        var prepare = builder.Task("prepare", _ => Task.CompletedTask);
        builder.Task("publish", _ => Task.CompletedTask).Requires(prepare);
        var failing = new FailingReporter("prepare: running -> succeeded", () => ValueTask.FromException(broken));
        var watching = new RecordingReporter();

        var result = await Runner.RunAsync(
            builder.Build(),
            new RunOptions { Journal = Journal.None, Reporters = [failing, watching] });

        result.Outcome.ShouldBe(RunOutcome.Complete);
        result.Snapshot.Counts[NodeStatus.Succeeded].ShouldBe(2);
        watching.Lines.ShouldBe(
        [
            "run started",
            "prepare: pending -> ready",
            "prepare: ready -> running",
            "prepare: running -> succeeded",
            "publish: pending -> ready",
            "publish: ready -> running",
            "publish: running -> succeeded",
            "run ended: complete",
        ]);
        failing.Lines.ShouldBe(
        [
            "run started",
            "prepare: pending -> ready",
            "prepare: ready -> running",
            "prepare: running -> succeeded",
        ]);
        var error = result.ReporterErrors.ShouldHaveSingleItem();
        error.Reporter.ShouldBeSameAs(failing);
        error.Event.ShouldBeSameAs(watching.Events[3]);
        error.Exception.ShouldBeSameAs(broken);
    }

    [Test]
    public async Task A_reporter_that_throws_on_the_last_event_is_on_the_result_and_the_run_still_returns()
    {
        var broken = new InvalidOperationException("the reporter is broken");
        var builder = new GraphBuilder();
        builder.Task("prepare", _ => Task.CompletedTask);
        var failing = new FailingReporter("run ended: complete", () => throw broken);
        var watching = new RecordingReporter();

        var result = await Runner.RunAsync(
            builder.Build(),
            new RunOptions { Journal = Journal.None, Reporters = [failing, watching] });

        result.Outcome.ShouldBe(RunOutcome.Complete);
        result.Snapshot.ShouldBeSameAs(watching.Snapshots[^1]);
        var error = result.ReporterErrors.ShouldHaveSingleItem();
        error.Reporter.ShouldBeSameAs(failing);
        error.Event.ShouldBeOfType<RunEnded>().ShouldBeSameAs(watching.Events[^1]);
        error.Exception.ShouldBeSameAs(broken);
    }

    [Test]
    public async Task Reporters_that_fail_are_on_the_result_in_the_order_the_reporters_were_given()
    {
        var builder = new GraphBuilder();
        builder.Task("prepare", _ => Task.CompletedTask);
        var failsLast = new FailingReporter("run ended: complete", () => throw new InvalidOperationException("late"));
        var watching = new RecordingReporter();
        var failsFirst = new FailingReporter("run started", () => throw new InvalidOperationException("early"));

        var result = await Runner.RunAsync(
            builder.Build(),
            new RunOptions { Journal = Journal.None, Reporters = [failsLast, watching, failsFirst] });

        result.Outcome.ShouldBe(RunOutcome.Complete);
        result.ReporterErrors.Select(error => error.Reporter).ShouldBe([failsLast, failsFirst]);
        result.ReporterErrors.Select(error => error.Exception.Message).ShouldBe(["late", "early"]);
        failsFirst.Lines.ShouldBe(["run started"]);
        watching.Lines.ShouldBe(failsLast.Lines);
    }

    [Test]
    public async Task Two_reporters_receive_the_same_events_in_the_same_order()
    {
        var builder = new GraphBuilder();
        var merge = builder.Task("merge", _ => Task.CompletedTask);
        for (var shard = 1; shard <= 20; shard++)
        {
            merge.Requires(builder.Task($"shard-{shard}", _ => Task.CompletedTask));
        }

        var first = new RecordingReporter();
        var second = new RecordingReporter();

        await Runner.RunAsync(
            builder.Build(),
            new RunOptions { Journal = Journal.None, Reporters = [first, second] });

        // The start and the end, and three changes of status for each of the 21 tasks.
        first.Events.Count.ShouldBe(65);
        second.Events.Count.ShouldBe(65);
        for (var position = 0; position < 65; position++)
        {
            second.Events[position].ShouldBeSameAs(first.Events[position]);
            second.Snapshots[position].ShouldBeSameAs(first.Snapshots[position]);
        }
    }

    [Test]
    public async Task A_reporter_is_never_called_for_an_event_while_it_is_still_handling_one()
    {
        var builder = new GraphBuilder();
        for (var shard = 1; shard <= 20; shard++)
        {
            builder.Task($"shard-{shard}", _ => Task.CompletedTask);
        }

        var reporter = new OverlapCountingReporter();

        await Runner.RunAsync(
            builder.Build(),
            new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        // The start and the end, and three changes of status for each of the 20 tasks.
        reporter.Calls.ShouldBe(62);
        reporter.MostCallsAtOnce.ShouldBe(1);
    }

    [Test]
    public async Task The_snapshot_handed_with_an_event_includes_that_event_and_none_after_it_however_far_behind_the_reporter_is()
    {
        var builder = new GraphBuilder();
        var prepare = builder.Task("prepare", _ => Task.CompletedTask);
        builder.Task("publish", _ => Task.CompletedTask).Requires(prepare);
        var behind = new PausedReporter();
        var watching = new RecordingReporter();

        var run = Runner.RunAsync(
            builder.Build(),
            new RunOptions { Journal = Journal.None, Reporters = [behind, watching] });
        await behind.Paused;
        await watching.Ended;
        behind.Release();
        await run;

        behind.Lines.Zip(behind.Snapshots, (line, snapshot) => $"{line} | {Describe(snapshot)}").ShouldBe(
        [
            "run started | prepare pending, publish pending, not ended",
            "prepare: pending -> ready | prepare ready, publish pending, not ended",
            "prepare: ready -> running | prepare running, publish pending, not ended",
            "prepare: running -> succeeded | prepare succeeded, publish pending, not ended",
            "publish: pending -> ready | prepare succeeded, publish ready, not ended",
            "publish: ready -> running | prepare succeeded, publish running, not ended",
            "publish: running -> succeeded | prepare succeeded, publish succeeded, not ended",
            "run ended: complete | prepare succeeded, publish succeeded, complete",
        ]);

        static string Describe(Snapshot snapshot)
        {
            var nodes = snapshot.Nodes.Select(node => $"{node.Path} {node.Status}");
            var outcome = snapshot.Outcome?.ToString() ?? "not ended";
            return string.Join(", ", [.. nodes, outcome]).ToLowerInvariant();
        }
    }

    [Test]
    public async Task The_snapshot_source_may_be_ahead_of_the_events_a_reporter_has_been_handed()
    {
        var builder = new GraphBuilder();
        var prepare = builder.Task("prepare", _ => Task.CompletedTask);
        builder.Task("publish", _ => Task.CompletedTask).Requires(prepare);
        var behind = new PausedReporter();
        var watching = new RecordingReporter();

        var run = Runner.RunAsync(
            builder.Build(),
            new RunOptions { Journal = Journal.None, Reporters = [behind, watching] });
        await behind.Paused;
        await watching.Ended;
        var handed = behind.Events;
        var handedWithIt = behind.Snapshots;
        var current = handed[0].ShouldBeOfType<RunStarted>().Source.Current;
        behind.Release();
        var result = await run;

        handed.Count.ShouldBe(1);
        handedWithIt[0].Counts[NodeStatus.Pending].ShouldBe(2);
        handedWithIt[0].Outcome.ShouldBeNull();
        current.Counts[NodeStatus.Succeeded].ShouldBe(2);
        current.Outcome.ShouldBe(RunOutcome.Complete);
        current.ShouldBeSameAs(result.Snapshot);
    }

    [Test]
    public async Task The_events_still_queued_for_a_reporter_when_it_is_dropped_are_let_go()
    {
        var builder = new GraphBuilder();
        var prepare = builder.Task("prepare", _ => Task.CompletedTask);
        builder.Task("publish", _ => Task.CompletedTask).Requires(prepare);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var behind = new FailingReporter("run started", async () =>
        {
            await released.Task;
            throw new InvalidOperationException("the reporter is broken");
        });
        var watching = new WeaklyWatchingReporter();

        var run = Runner.RunAsync(
            builder.Build(),
            new RunOptions { Journal = Journal.None, Reporters = [behind, watching] });
        await watching.Ended;
        released.SetResult();
        var result = await run;

        // The thread that handed over the last event may still be leaving that call with the event on its stack,
        // so one collection can come too early. Events the queue still held would survive every one of them.
        for (var collection = 0; collection < 100 && watching.StillHeld > 0; collection++)
        {
            await Task.Yield();
            GC.Collect();
        }

        behind.Lines.ShouldBe(["run started"]);
        result.ReporterErrors.ShouldHaveSingleItem().Reporter.ShouldBeSameAs(behind);
        watching.Watched.ShouldBe(7);
        watching.StillHeld.ShouldBe(0);
        watching.Source.Current.ShouldBeSameAs(result.Snapshot);
    }

    [Test]
    public async Task A_run_with_no_reporters_works()
    {
        var builder = new GraphBuilder();
        var prepare = builder.Task("prepare", _ => Task.CompletedTask);
        builder.Task("publish", _ => Task.CompletedTask).Requires(prepare);

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });

        result.Outcome.ShouldBe(RunOutcome.Complete);
        result.Snapshot.Counts[NodeStatus.Succeeded].ShouldBe(2);
        result.ReporterErrors.ShouldBeEmpty();
    }

    /// <summary>
    /// Counts the calls made to it and the most that were ever open at once.
    /// Each call stays open across a hop to another thread, so a call made before the last one returned would overlap it.
    /// </summary>
    private sealed class OverlapCountingReporter : IReporter
    {
        private int _calls;
        private int _open;
        private int _mostCallsAtOnce;

        public int Calls => Volatile.Read(ref _calls);

        public int MostCallsAtOnce => Volatile.Read(ref _mostCallsAtOnce);

        public async ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var open = Interlocked.Increment(ref _open);
            int most;
            while (open > (most = Volatile.Read(ref _mostCallsAtOnce)))
            {
                Interlocked.CompareExchange(ref _mostCallsAtOnce, open, most);
            }

            await Task.Yield();
            Interlocked.Decrement(ref _open);
        }
    }

    /// <summary>
    /// Keeps a line for every event it is handed, and fails on the event whose line is <paramref name="failsOn"/>
    /// in the way <paramref name="fail"/> does: by throwing, or by returning a faulted <see cref="ValueTask"/>.
    /// </summary>
    private sealed class FailingReporter(string failsOn, Func<ValueTask> fail) : IReporter
    {
        private readonly Lock _gate = new();
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_gate)
                {
                    return [.. _lines];
                }
            }
        }

        public ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken)
        {
            var line = EventLine.Describe(runEvent);
            lock (_gate)
            {
                _lines.Add(line);
            }

            return line == failsOn ? fail() : ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Keeps the snapshot source of the run, as a reporter that shows progress does, and so keeps the run itself reachable.
    /// It keeps no event after <see cref="RunStarted"/>: it only watches whether something else still holds each one.
    /// </summary>
    private sealed class WeaklyWatchingReporter : IReporter
    {
        private readonly Lock _gate = new();
        private readonly List<WeakReference<RunEvent>> _watched = [];
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ISnapshotSource? _source;

        public Task Ended => _ended.Task;

        public ISnapshotSource Source => _source ?? throw new InvalidOperationException("The run has not started.");

        public int Watched
        {
            get
            {
                lock (_gate)
                {
                    return _watched.Count;
                }
            }
        }

        /// <summary>How many of the events watched are still reachable from somewhere. Collect garbage before asking.</summary>
        public int StillHeld
        {
            get
            {
                lock (_gate)
                {
                    return _watched.Count(watched => watched.TryGetTarget(out _));
                }
            }
        }

        public ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken)
        {
            if (runEvent is RunStarted started)
            {
                _source = started.Source;
                return ValueTask.CompletedTask;
            }

            lock (_gate)
            {
                _watched.Add(new WeakReference<RunEvent>(runEvent));
            }

            if (runEvent is RunEnded)
            {
                _ended.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }
    }
}
