using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace Dagline.Tests;

/// <summary>
/// The end of a run on the new engine: the wait for reporters, bounded by the reporter wait on the run's clock, and dropping a
/// reporter (ADR 0010, ADR 0015). Each test is one scripted run that makes each thing happen in the order it means, so none is
/// repeated.
/// </summary>
public class LoopRunReporterWaitTests
{
    private static readonly DateTimeOffset _runStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan _reporterWait = TimeSpan.FromSeconds(30);

    [Test]
    public async Task A_reporter_still_busy_when_the_reporter_wait_runs_out_is_dropped_with_its_token_cancelled_and_its_queued_events_let_go()
    {
        var clock = new FakeTimeProvider(_runStart);
        var busy = new HeldReporter("prepare: ready -> running");
        var watching = new RecordingReporter();
        var run = ScriptedDriver.Start(Chain(), Options(clock, busy, watching));

        run.Pump();
        clock.Advance(_reporterWait - TimeSpan.FromTicks(1));
        run.Pump();
        var returnedBeforeTheWaitRanOut = run.Result.IsCompleted;
        clock.Advance(TimeSpan.FromTicks(1));
        var result = await run.Finish();

        returnedBeforeTheWaitRanOut.ShouldBeFalse();
        result.Outcome.ShouldBe(RunOutcome.Complete);
        var error = result.ReporterErrors.ShouldHaveSingleItem();
        error.Reporter.ShouldBeSameAs(busy);
        error.Event.ShouldBeSameAs(watching.Events[2]);
        error.Exception.ShouldBeAssignableTo<OperationCanceledException>();
        busy.Tokens[^1].IsCancellationRequested.ShouldBeTrue();
        busy.Lines.ShouldBe(["run started", "prepare: pending -> ready", "prepare: ready -> running"]);
        watching.Lines[^1].ShouldBe("run ended: complete");
    }

    [Test]
    public async Task A_dropped_reporter_whose_call_comes_back_later_throwing_is_handed_nothing_more_and_changes_nothing()
    {
        var clock = new FakeTimeProvider(_runStart);
        var busy = new HeldReporter("prepare: ready -> running");
        var run = ScriptedDriver.Start(Chain(), Options(clock, busy));
        run.Pump();
        clock.Advance(_reporterWait);
        var result = await run.Finish();

        busy.Throw(new InvalidOperationException("the reporter threw once its call came back"));
        run.Pump();

        result.ReporterErrors.ShouldHaveSingleItem().Exception.ShouldBeAssignableTo<OperationCanceledException>();
        busy.Lines.ShouldBe(["run started", "prepare: pending -> ready", "prepare: ready -> running"]);
    }

    [Test]
    public async Task The_reporter_wait_is_measured_from_the_moment_the_run_s_end_is_queued_and_not_from_the_run_s_start()
    {
        var clock = new FakeTimeProvider(_runStart);
        var prepare = new PausedBody();
        var busy = new HeldReporter("run ended: complete");
        var run = ScriptedDriver.Start(Chain(prepare), Options(clock, busy));
        run.Pump();

        clock.Advance(_reporterWait);
        prepare.Release();
        run.Pump();
        clock.Advance(_reporterWait - TimeSpan.FromTicks(1));
        run.Pump();
        var returnedBeforeTheWaitRanOut = run.Result.IsCompleted;
        clock.Advance(TimeSpan.FromTicks(1));
        var result = await run.Finish();

        returnedBeforeTheWaitRanOut.ShouldBeFalse();
        result.ReporterErrors.ShouldHaveSingleItem().Event.ShouldBeOfType<RunEnded>();
    }

    [Test]
    public async Task A_reporter_whose_call_never_returns_holds_the_result_no_longer_than_the_reporter_wait()
    {
        var clock = new FakeTimeProvider(_runStart);
        var stuck = new HeldReporter("run ended: complete");
        var watching = new RecordingReporter();
        var run = ScriptedDriver.Start(Chain(), Options(clock, stuck, watching));
        run.Pump();

        clock.Advance(_reporterWait);
        var result = await run.Finish();

        result.Outcome.ShouldBe(RunOutcome.Complete);
        var error = result.ReporterErrors.ShouldHaveSingleItem();
        error.Reporter.ShouldBeSameAs(stuck);
        error.Event.ShouldBeSameAs(watching.Events[^1]);
        stuck.Tokens[^1].IsCancellationRequested.ShouldBeTrue();
    }

    [Test]
    public async Task A_reporter_still_to_be_handed_its_events_when_the_wait_runs_out_is_dropped_naming_the_next_event()
    {
        var clock = new FakeTimeProvider(_runStart);
        var unpumped = new RecordingReporter();
        var run = ScriptedDriver.Start(Chain(), Options(clock, unpumped));
        while (run.PumpLoop() + run.PumpBodies() > 0)
        {
        }

        clock.Advance(_reporterWait);
        run.PumpLoop();
        var result = await run.Result;

        var error = result.ReporterErrors.ShouldHaveSingleItem();
        error.Reporter.ShouldBeSameAs(unpumped);
        error.Event.ShouldBeOfType<RunStarted>();
        run.PumpReporters();
        unpumped.Events.ShouldBeEmpty();
    }

    [Test]
    public async Task A_reporter_that_finishes_after_the_wait_ran_out_but_before_the_loop_read_it_is_not_dropped()
    {
        var clock = new FakeTimeProvider(_runStart);
        var slow = new HeldReporter("run ended: complete");
        var run = ScriptedDriver.Start(Chain(), Options(clock, slow));
        run.Pump();

        clock.Advance(_reporterWait);
        slow.Release();
        run.PumpReporters();
        var result = await run.Finish();

        result.ReporterErrors.ShouldBeEmpty();
        slow.Tokens.ShouldAllBe(token => !token.IsCancellationRequested);
    }

    [Test]
    public async Task A_reporter_that_throws_is_dropped_at_once_with_its_token_cancelled_and_the_run_carries_on_and_returns_without_waiting_the_reporter_wait()
    {
        var clock = new FakeTimeProvider(_runStart);
        var failure = new InvalidOperationException("the reporter cannot write");
        var throwing = new HeldReporter("prepare: ready -> running");
        var watching = new RecordingReporter();
        var run = ScriptedDriver.Start(Chain(), Options(clock, throwing, watching));
        run.Pump();

        throwing.Throw(failure);
        var result = await run.Finish();

        result.Outcome.ShouldBe(RunOutcome.Complete);
        var error = result.ReporterErrors.ShouldHaveSingleItem();
        error.Reporter.ShouldBeSameAs(throwing);
        error.Event.ShouldBeSameAs(watching.Events[2]);
        error.Exception.ShouldBeSameAs(failure);
        throwing.Tokens[^1].IsCancellationRequested.ShouldBeTrue();
        throwing.Lines.ShouldBe(["run started", "prepare: pending -> ready", "prepare: ready -> running"]);
        watching.Lines[^1].ShouldBe("run ended: complete");
    }

    [Test]
    public async Task A_busy_reporter_that_returns_within_the_reporter_wait_is_handed_every_event_and_never_has_its_token_cancelled()
    {
        var clock = new FakeTimeProvider(_runStart);
        var busy = new HeldReporter("prepare: ready -> running");
        var watching = new RecordingReporter();
        var run = ScriptedDriver.Start(Chain(), Options(clock, busy, watching));
        run.Pump();

        clock.Advance(_reporterWait - TimeSpan.FromTicks(1));
        busy.Release();
        var result = await run.Finish();

        result.ReporterErrors.ShouldBeEmpty();
        busy.Lines.ShouldBe(watching.Lines);
        busy.Tokens.ShouldAllBe(token => !token.IsCancellationRequested);
    }

    [Test]
    public async Task A_reporter_that_had_handled_every_event_never_has_its_token_cancelled_when_another_is_dropped()
    {
        var clock = new FakeTimeProvider(_runStart);
        var stuck = new HeldReporter("run ended: complete");
        var finished = new HeldReporter("no event has this line");
        var run = ScriptedDriver.Start(Chain(), Options(clock, finished, stuck));
        run.Pump();

        clock.Advance(_reporterWait);
        var result = await run.Finish();

        result.ReporterErrors.ShouldHaveSingleItem().Reporter.ShouldBeSameAs(stuck);
        finished.Tokens.ShouldNotBeEmpty();
        finished.Tokens.ShouldAllBe(token => !token.IsCancellationRequested);
    }

    [Test]
    public async Task Cancelling_the_run_s_token_after_the_run_has_ended_changes_nothing()
    {
        var clock = new FakeTimeProvider(_runStart);
        var busy = new HeldReporter("run ended: complete");
        using var stop = new CancellationTokenSource();
        var run = ScriptedDriver.Start(Chain(), Options(clock, busy), stop.Token);
        run.Pump();

        await stop.CancelAsync();
        run.Pump();
        var returnedOnTheCancellation = run.Result.IsCompleted;
        busy.Release();
        var result = await run.Finish();

        returnedOnTheCancellation.ShouldBeFalse();
        result.Outcome.ShouldBe(RunOutcome.Complete);
        result.Snapshot.Stop.ShouldBeNull();
        result.ReporterErrors.ShouldBeEmpty();
        busy.Lines.ShouldNotContain("stop requested");
        busy.Tokens.ShouldAllBe(token => !token.IsCancellationRequested);
    }

    [Test]
    public async Task An_unlimited_reporter_wait_waits_for_as_long_as_the_reporters_take()
    {
        var clock = new FakeTimeProvider(_runStart);
        var slow = new HeldReporter("run ended: complete");
        var run = ScriptedDriver.Start(Chain(), Options(clock, slow).WithReporterWait(Timeout.InfiniteTimeSpan));
        run.Pump();

        clock.Advance(TimeSpan.FromDays(400));
        run.Pump();
        var returnedWhileBusy = run.Result.IsCompleted;
        slow.Release();
        var result = await run.Finish();

        returnedWhileBusy.ShouldBeFalse();
        result.ReporterErrors.ShouldBeEmpty();
        slow.Tokens.ShouldAllBe(token => !token.IsCancellationRequested);
    }

    [Test]
    public async Task A_reporter_never_reads_a_current_snapshot_older_than_an_event_it_holds_and_every_reporter_receives_the_same_events()
    {
        var first = new PausedBody();
        var second = new PausedBody();
        var builder = new GraphBuilder();
        var firstTask = builder.Task("first", first.Body);
        var secondTask = builder.Task("second", second.Body).Requires(firstTask);
        builder.Task("third", _ => Task.CompletedTask).Requires(secondTask);
        var checking = new CurrentCheckingReporter();
        var watching = new RecordingReporter();
        var run = ScriptedDriver.Start(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [checking, watching] });

        run.PumpLoop();
        run.PumpReporters();
        run.PumpBodies();
        run.PumpReporters();
        first.Release();
        run.PumpBodies();
        run.PumpLoop();
        run.PumpReporters();
        run.PumpBodies();
        second.Release();
        run.PumpBodies();
        run.PumpReporters();
        run.PumpLoop();
        run.PumpReporters();
        await run.Finish();

        checking.Lines.ShouldBe(watching.Lines);
        checking.Lines[^1].ShouldBe("run ended: complete");
        checking.OlderCurrents.ShouldBeEmpty();
    }

    [Test]
    public async Task A_reporter_that_blocks_its_thread_inside_a_call_does_not_hold_the_result_past_the_reporter_wait()
    {
        var clock = new FakeTimeProvider(_runStart);
        var builder = new GraphBuilder();
        builder.Task("prepare", _ => Task.CompletedTask);
        using var blocking = new BlockingReporter("prepare: ready -> running");
        var watching = new RecordingReporter();
        var run = Runner.RunAsync(builder.Build(), Options(clock, blocking, watching));
        await blocking.Blocked;
        await watching.Ended;

        await clock.AdvanceUntilAsync(run, _reporterWait);
        var result = await run;
        var blockedToken = blocking.Tokens[^1];
        blocking.Release();
        await blocking.Returned;

        var error = result.ReporterErrors.ShouldHaveSingleItem();
        error.Reporter.ShouldBeSameAs(blocking);
        error.Event.ShouldBeSameAs(watching.Events[2]);
        error.Exception.ShouldBeAssignableTo<OperationCanceledException>();
        blockedToken.IsCancellationRequested.ShouldBeTrue();
    }

    private static Graph Chain(PausedBody? prepare = null)
    {
        var builder = new GraphBuilder();
        var prepareTask = builder.Task("prepare", prepare is null ? _ => Task.CompletedTask : prepare.Body);
        builder.Task("load", _ => Task.CompletedTask).Requires(prepareTask);
        return builder.Build();
    }

    private static RunOptions Options(FakeTimeProvider clock, params IReporter[] reporters)
        => new RunOptions { Journal = Journal.None, Reporters = reporters, TimeProvider = clock }.WithReporterWait(_reporterWait);

    /// <summary>
    /// Keeps a line and the token for every event it is handed, and holds the call for the event whose line is
    /// <paramref name="holdsOn"/> open, ignoring its token as a stuck reporter would, until the test releases it or makes it throw.
    /// </summary>
    private sealed class HeldReporter(string holdsOn) : IReporter
    {
        private readonly List<string> _lines = [];
        private readonly List<CancellationToken> _tokens = [];
        private readonly TaskCompletionSource _released = new();

        public IReadOnlyList<string> Lines => _lines;

        public IReadOnlyList<CancellationToken> Tokens => _tokens;

        public void Release() => _released.TrySetResult();

        public void Throw(Exception exception) => _released.TrySetException(exception);

        public async ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken)
        {
            var line = EventLine.Describe(runEvent);
            _lines.Add(line);
            _tokens.Add(cancellationToken);
            if (line == holdsOn)
            {
                await _released.Task;
            }
        }
    }

    /// <summary>
    /// Reads the current snapshot from the run's source in every call, and keeps each event whose current snapshot was further
    /// behind than the snapshot handed with it, measured as how far the tasks have gone: ready, running, then settled.
    /// </summary>
    private sealed class CurrentCheckingReporter : IReporter
    {
        private readonly List<string> _lines = [];
        private readonly List<string> _olderCurrents = [];
        private ISnapshotSource? _source;

        public IReadOnlyList<string> Lines => _lines;

        public IReadOnlyList<string> OlderCurrents => _olderCurrents;

        public ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken)
        {
            if (runEvent is RunStarted started)
            {
                _source = started.Source;
            }

            var line = EventLine.Describe(runEvent);
            _lines.Add(line);
            var current = _source!.Current;
            if (HowFar(current) < HowFar(snapshot) || (snapshot.Ended is not null && current.Ended is null))
            {
                _olderCurrents.Add(line);
            }

            return ValueTask.CompletedTask;
        }

        private static int HowFar(Snapshot snapshot)
            => snapshot.Nodes.Sum(node => node.Status switch
            {
                NodeStatus.Pending => 0,
                NodeStatus.Ready => 1,
                NodeStatus.Running => 2,
                _ => 3,
            });
    }

    /// <summary>
    /// Keeps a line and the token for every event it is handed, and blocks its thread inside the call for the event whose line is
    /// <paramref name="blocksOn"/>, before it returns a task at all, until the test releases it.
    /// </summary>
    private sealed class BlockingReporter(string blocksOn) : IReporter, IDisposable
    {
        private readonly Lock _gate = new();
        private readonly List<string> _lines = [];
        private readonly List<CancellationToken> _tokens = [];
        private readonly ManualResetEventSlim _released = new();
        private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _returned = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Blocked => _blocked.Task;

        public Task Returned => _returned.Task;

        public IReadOnlyList<CancellationToken> Tokens
        {
            get
            {
                lock (_gate)
                {
                    return [.. _tokens];
                }
            }
        }

        public void Release() => _released.Set();

        public ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken)
        {
            var line = EventLine.Describe(runEvent);
            lock (_gate)
            {
                _lines.Add(line);
                _tokens.Add(cancellationToken);
            }

            if (line == blocksOn)
            {
                _blocked.TrySetResult();
                // Ignores its token, as a reporter stuck in blocking code would.
                _released.Wait(CancellationToken.None);
                _returned.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }

        public void Dispose() => _released.Dispose();
    }
}
