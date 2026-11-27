using Dagline.TestSupport;

namespace Dagline.Tests;

public class ReporterStopTests
{
    private const int _repeats = 50;

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
    public async Task A_token_cancelled_during_the_wait_for_reporters_drops_the_busy_reporter_and_the_run_returns()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var builder = new GraphBuilder();
            var prepare = builder.Task("prepare", _ => Task.CompletedTask);
            builder.Task("publish", _ => Task.CompletedTask).Requires(prepare);
            var stuck = new HeldReporter("prepare: ready -> running");
            var watching = new RecordingReporter();
            using var stop = new CancellationTokenSource();

            var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [stuck, watching] }, cancellationToken: stop.Token);
            await stuck.Held;
            await watching.Ended;
            await stop.CancelAsync();
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
    public async Task A_reporter_that_had_handled_every_event_is_not_affected_by_the_cancellation()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var builder = new GraphBuilder();
            builder.Task("prepare", _ => Task.CompletedTask);
            var stuck = new HeldReporter("run ended: complete");
            var done = new WatchedReporter(new RecordingReporter());
            using var stop = new CancellationTokenSource();

            var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [done, stuck] }, cancellationToken: stop.Token);
            await stuck.Held;
            await done.Handled("run ended: complete");
            await stop.CancelAsync();
            var result = await run;
            stuck.Release();
            await stuck.Returned;

            result.Outcome.ShouldBe(RunOutcome.Complete);
            result.ReporterErrors.ShouldHaveSingleItem().Reporter.ShouldBeSameAs(stuck);
        }
    }

    [Test]
    public async Task The_cancellation_never_cancels_the_token_of_a_reporter_that_had_handled_every_event()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var builder = new GraphBuilder();
            builder.Task("prepare", _ => Task.CompletedTask);
            var stuck = new HeldReporter("run ended: complete");
            var finished = new HeldReporter(holdsOn: "no event has this line");
            var done = new WatchedReporter(finished);
            using var stop = new CancellationTokenSource();

            var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [done, stuck] }, cancellationToken: stop.Token);
            await stuck.Held;
            await done.Handled("run ended: complete");
            await stop.CancelAsync();
            await run;
            stuck.Release();
            await stuck.Returned;

            finished.Tokens.ShouldNotBeEmpty();
            finished.Tokens.ShouldAllBe(token => !token.IsCancellationRequested);
        }
    }

    [Test]
    public async Task A_cancellation_ends_the_wait_for_a_reporter_that_blocks_its_thread_inside_a_call()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var builder = new GraphBuilder();
            builder.Task("prepare", _ => Task.CompletedTask);
            using var blocking = new BlockingReporter("prepare: ready -> running");
            var watching = new RecordingReporter();
            using var stop = new CancellationTokenSource();

            var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [blocking, watching] }, cancellationToken: stop.Token);
            await blocking.Blocked;
            await watching.Ended;
            await stop.CancelAsync();
            var result = await run.WaitAsync(TimeSpan.FromSeconds(30));
            var blockedToken = blocking.Tokens[^1];
            blocking.Release();
            await blocking.Returned;

            var error = result.ReporterErrors.ShouldHaveSingleItem();
            error.Reporter.ShouldBeSameAs(blocking);
            error.Event.ShouldBeSameAs(watching.Events[2]);
            error.Exception.ShouldBeAssignableTo<OperationCanceledException>();
            blockedToken.IsCancellationRequested.ShouldBeTrue();
            blocking.Lines.ShouldBe(["run started", "prepare: pending -> ready", "prepare: ready -> running"]);
        }
    }

    [Test]
    public async Task A_token_cancelled_as_the_run_ends_is_a_stop_or_ends_the_wait_for_reporters()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var prepare = new PausedBody();
            var builder = new GraphBuilder();
            builder.Task("prepare", prepare.Body);
            var stuck = new HeldReporter("run ended: complete");
            using var stop = new CancellationTokenSource();

            var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [stuck] }, cancellationToken: stop.Token);
            await prepare.Started;
            var cancel = Task.Run(stop.Cancel);
            prepare.Release();
            await cancel;
            await Task.WhenAny(run, stuck.Held);
            var applied = stuck.Held.IsCompleted && stuck.Lines.Contains("stop requested");
            if (applied)
            {
                // The run applied the stop, so nothing drops the reporter: the run waits for it.
                stuck.Release();
            }

            var result = await run;
            stuck.Release();
            if (stuck.Held.IsCompleted)
            {
                // The cancellation can drop the reporter before it is handed the event it holds on, and then it holds nothing.
                await stuck.Returned;
            }

            result.Outcome.ShouldBe(RunOutcome.Complete);
            if (applied)
            {
                result.ReporterErrors.ShouldBeEmpty();
            }
            else
            {
                result.ReporterErrors.ShouldHaveSingleItem().Reporter.ShouldBeSameAs(stuck);
            }
        }
    }

    /// <summary>
    /// Keeps a line and the token for every event it is handed, and blocks its thread inside the call for the event whose
    /// line is <paramref name="blocksOn"/>, before it returns a task at all, until the test releases it.
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

        /// <summary>Completes once the blocked call has come back, after <see cref="Release"/>.</summary>
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
