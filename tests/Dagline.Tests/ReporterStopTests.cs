using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace Dagline.Tests;

/// <summary>
/// The run's token and the wait for reporters at the end of a run: the token means a stop and nothing else, and only the
/// reporter wait ends the wait (ADR 0010, ADR 0014).
/// </summary>
public class ReporterStopTests
{
    private const int _repeats = 50;
    private static readonly DateTimeOffset _runStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan _reporterWait = TimeSpan.FromSeconds(30);

    [Test]
    public async Task With_no_cancellation_the_run_waits_for_a_slow_reporter_and_never_cancels_its_token()
    {
        var builder = new GraphBuilder();
        var prepare = builder.Task("prepare", _ => Task.CompletedTask);
        builder.Task("publish", _ => Task.CompletedTask).Requires(prepare);
        var slow = new HeldReporter("run started");
        var watching = new RecordingReporter();
        using var stop = new CancellationTokenSource();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [slow, watching] }, cancellationToken: stop.Token);
        await slow.Held;
        await watching.Ended;
        var returnedWhileHeld = run.IsCompleted;
        slow.Release();
        var result = await run;

        returnedWhileHeld.ShouldBeFalse();
        result.Outcome.ShouldBe(RunOutcome.Complete);
        result.ReporterErrors.ShouldBeEmpty();
        slow.Lines.ShouldBe(watching.Lines);
        slow.Tokens.ShouldAllBe(token => !token.IsCancellationRequested);
    }

    [Test]
    public async Task A_token_cancelled_during_the_wait_for_reporters_drops_no_reporter_and_the_reporter_wait_still_ends_the_wait()
    {
        var clock = new FakeTimeProvider(_runStart);
        var stuck = new PumpedHeldReporter("prepare: ready -> running");
        var watching = new RecordingReporter();
        using var stop = new CancellationTokenSource();
        var run = ScriptedDriver.Start(Chain(), Options(clock, stuck, watching), stop.Token);
        run.Pump();

        await stop.CancelAsync();
        run.Pump();
        var returnedOnTheCancellation = run.Result.IsCompleted;
        clock.Advance(_reporterWait);
        var result = await run.Finish();

        returnedOnTheCancellation.ShouldBeFalse();
        result.Outcome.ShouldBe(RunOutcome.Complete);
        result.Snapshot.Stop.ShouldBeNull();
        watching.Lines.ShouldNotContain("stop requested");
        var error = result.ReporterErrors.ShouldHaveSingleItem();
        error.Reporter.ShouldBeSameAs(stuck);
        error.Event.ShouldBeSameAs(watching.Events[2]);
        error.Exception.ShouldBeAssignableTo<OperationCanceledException>();
        stuck.Tokens[^1].IsCancellationRequested.ShouldBeTrue();
        stuck.Lines.ShouldBe(["run started", "prepare: pending -> ready", "prepare: ready -> running"]);
    }

    [Test]
    public async Task A_token_cancelled_before_every_task_has_ended_is_a_stop_and_drops_no_reporter()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var load = new StoppedBody();
            var builder = new GraphBuilder();
            builder.Task("load", load.Body);
            var stuck = new HeldReporter("run started");
            var watching = new RecordingReporter();
            using var stop = new CancellationTokenSource();

            var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [stuck, watching] }, cancellationToken: stop.Token);
            await stuck.Held;
            await load.Started;
            await stop.CancelAsync();
            await watching.Ended;
            var returnedWhileHeld = run.IsCompleted;
            stuck.Release();
            var result = await run;

            returnedWhileHeld.ShouldBeFalse();
            result.Outcome.ShouldBe(RunOutcome.Stopped);
            result.ReporterErrors.ShouldBeEmpty();
            stuck.Lines.ShouldBe(watching.Lines);
            stuck.Tokens.ShouldAllBe(token => !token.IsCancellationRequested);
        }
    }

    [Test]
    public async Task A_token_cancelled_as_the_last_task_ends_but_taken_after_its_end_is_no_stop_and_the_run_still_waits_for_its_reporter()
    {
        var clock = new FakeTimeProvider(_runStart);
        var prepare = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("prepare", prepare.Body);
        var slow = new PumpedHeldReporter("run ended: complete");
        using var stop = new CancellationTokenSource();
        var run = ScriptedDriver.Start(builder.Build(), Options(clock, slow), stop.Token);
        run.Pump();

        prepare.Release();
        run.PumpBodies();
        await stop.CancelAsync();
        run.Pump();
        var returnedWhileBusy = run.Result.IsCompleted;
        slow.Release();
        var result = await run.Finish();

        returnedWhileBusy.ShouldBeFalse();
        result.Outcome.ShouldBe(RunOutcome.Complete);
        result.Snapshot.Stop.ShouldBeNull();
        result.ReporterErrors.ShouldBeEmpty();
        slow.Lines.ShouldNotContain("stop requested");
        slow.Lines[^1].ShouldBe("run ended: complete");
        slow.Tokens.ShouldAllBe(token => !token.IsCancellationRequested);
    }

    private static Graph Chain()
    {
        var builder = new GraphBuilder();
        var prepare = builder.Task("prepare", _ => Task.CompletedTask);
        builder.Task("publish", _ => Task.CompletedTask).Requires(prepare);
        return builder.Build();
    }

    private static RunOptions Options(FakeTimeProvider clock, params IReporter[] reporters)
        => new() { Journal = Journal.None, Reporters = reporters, TimeProvider = clock, ReporterWait = _reporterWait };

    /// <summary>
    /// For a scripted run: keeps a line and the token for every event it is handed, and holds the call for the event whose line
    /// is <paramref name="holdsOn"/> open, ignoring its token as a stuck reporter would, until the test releases it. The held call
    /// resumes on the pump that delivers to it.
    /// </summary>
    private sealed class PumpedHeldReporter(string holdsOn) : IReporter
    {
        private readonly List<string> _lines = [];
        private readonly List<CancellationToken> _tokens = [];
        private readonly TaskCompletionSource _released = new();

        public IReadOnlyList<string> Lines => _lines;

        public IReadOnlyList<CancellationToken> Tokens => _tokens;

        public void Release() => _released.TrySetResult();

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
    /// Keeps a line and the token for every event it is handed, and holds the call for the event whose line is
    /// <paramref name="holdsOn"/> open until the test releases it, ignoring its token as a stuck reporter would.
    /// </summary>
    private sealed class HeldReporter(string holdsOn) : IReporter
    {
        private readonly Lock _gate = new();
        private readonly List<string> _lines = [];
        private readonly List<CancellationToken> _tokens = [];
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _returned = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Held => _held.Task;

        /// <summary>Completes once the held call has come back, after <see cref="Release"/>.</summary>
        public Task Returned => _returned.Task;

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

        public void Release() => _released.TrySetResult();

        public async ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken)
        {
            var line = EventLine.Describe(runEvent);
            lock (_gate)
            {
                _lines.Add(line);
                _tokens.Add(cancellationToken);
            }

            if (line != holdsOn)
            {
                return;
            }

            _held.TrySetResult();
            try
            {
                await _released.Task;
            }
            finally
            {
                _returned.TrySetResult();
            }
        }
    }
}
