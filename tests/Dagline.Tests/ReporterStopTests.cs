using Dagline.TestSupport;

namespace Dagline.Tests;

public class ReporterStopTests
{
    private const int _repeats = 50;

    [Test]
    public async Task With_no_stop_request_the_run_waits_for_a_slow_reporter_and_never_cancels_its_token()
    {
        var builder = new GraphBuilder();
        var prepare = builder.Task("prepare", _ => Task.CompletedTask);
        builder.Task("publish", _ => Task.CompletedTask).Requires(prepare);
        var slow = new HeldReporter("run started");
        var watching = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [slow, watching] }, new StopSignal());
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
    public async Task A_first_request_during_the_wait_for_reporters_drops_the_busy_reporter_and_the_run_returns()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var builder = new GraphBuilder();
            var prepare = builder.Task("prepare", _ => Task.CompletedTask);
            builder.Task("publish", _ => Task.CompletedTask).Requires(prepare);
            var stuck = new HeldReporter("prepare: ready -> running");
            var watching = new RecordingReporter();
            var stop = new StopSignal();

            var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [stuck, watching] }, stop);
            await stuck.Held;
            await watching.Ended;
            stop.Request();
            var result = await run;
            var heldToken = stuck.Tokens[^1];
            stuck.Release();
            await stuck.Returned;

            result.Outcome.ShouldBe(RunOutcome.Complete);
            result.Snapshot.Stop.ShouldBeNull();
            watching.Lines.ShouldNotContain("stop requested");
            var error = result.ReporterErrors.ShouldHaveSingleItem();
            error.Reporter.ShouldBeSameAs(stuck);
            error.Event.ShouldBeSameAs(watching.Events[2]);
            error.Exception.ShouldBeAssignableTo<OperationCanceledException>();
            heldToken.IsCancellationRequested.ShouldBeTrue();
            stuck.Lines.ShouldBe(["run started", "prepare: pending -> ready", "prepare: ready -> running"]);
        }
    }

    [Test]
    public async Task A_later_request_during_the_wait_for_reporters_ends_it_once_the_run_has_been_stopped()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var load = new StoppedBody();
            var builder = new GraphBuilder();
            builder.Task("load", load.Body);
            var stuck = new HeldReporter("run started");
            var watching = new RecordingReporter();
            var stop = new StopSignal();

            var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [stuck, watching] }, stop);
            await stuck.Held;
            await load.Started;
            stop.Request();
            await watching.Ended;
            stop.Request();
            var result = await run;
            var heldToken = stuck.Tokens[^1];
            stuck.Release();
            await stuck.Returned;

            result.Outcome.ShouldBe(RunOutcome.Stopped);
            var error = result.ReporterErrors.ShouldHaveSingleItem();
            error.Reporter.ShouldBeSameAs(stuck);
            error.Event.ShouldBeSameAs(watching.Events[0]);
            error.Exception.ShouldBeAssignableTo<OperationCanceledException>();
            heldToken.IsCancellationRequested.ShouldBeTrue();
            stuck.Lines.ShouldBe(["run started"]);
        }
    }

    [Test]
    public async Task A_reporter_that_had_handled_every_event_is_not_affected_by_the_request()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var builder = new GraphBuilder();
            builder.Task("prepare", _ => Task.CompletedTask);
            var stuck = new HeldReporter("run ended: complete");
            var done = new WatchedReporter(new RecordingReporter());
            var stop = new StopSignal();

            var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [done, stuck] }, stop);
            await stuck.Held;
            await done.Handled("run ended: complete");
            stop.Request();
            var result = await run;
            stuck.Release();
            await stuck.Returned;

            result.Outcome.ShouldBe(RunOutcome.Complete);
            result.ReporterErrors.ShouldHaveSingleItem().Reporter.ShouldBeSameAs(stuck);
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
