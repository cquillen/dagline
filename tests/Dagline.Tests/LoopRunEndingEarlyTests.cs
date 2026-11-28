using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace Dagline.Tests;

/// <summary>
/// Ending a run early on the new engine: a drain, a stop, the grace period and letting go (ADR 0014, ADR 0015). Each test is one
/// scripted run that makes each thing happen in the order it means, so none is repeated.
/// </summary>
public class LoopRunEndingEarlyTests
{
    private static readonly DateTimeOffset _runStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task A_stop_with_a_zero_grace_period_before_a_body_s_end_lets_go_of_the_task_and_its_end_changes_nothing()
    {
        var prepare = new PausedBody();
        var reporter = new RecordingReporter();
        var run = ScriptedDriver.Start(Chain(prepare), Options(reporter, TimeSpan.Zero));
        run.Pump();

        run.Stop();
        run.PumpLoop();
        prepare.Release();
        var result = await run.Finish();

        result.Outcome.ShouldBe(RunOutcome.Stopped);
        reporter.Lines.ShouldBe(
        [
            "run started",
            "prepare: pending -> ready",
            "prepare: ready -> running",
            "stop requested",
            "grace period over",
            "prepare: running -> cancelled",
            "run ended: stopped",
        ]);
        result.Snapshot.Stop!.By.ShouldBe(StopRequester.App);
        result.Snapshot[NodePath.Parse("load")].Status.ShouldBe(NodeStatus.Pending);
        (await prepare.Started).CancellationToken.IsCancellationRequested.ShouldBeTrue();
    }

    [Test]
    public async Task A_stop_with_a_zero_grace_period_after_a_body_s_end_applies_the_end_first_and_lets_go_of_the_dependent_it_started()
    {
        var prepare = new PausedBody();
        var reporter = new RecordingReporter();
        var run = ScriptedDriver.Start(Chain(prepare), Options(reporter, TimeSpan.Zero));
        run.Pump();

        prepare.Release();
        run.PumpBodies();
        run.Stop();
        var result = await run.Finish();

        result.Outcome.ShouldBe(RunOutcome.Stopped);
        reporter.Lines.ShouldBe(
        [
            "run started",
            "prepare: pending -> ready",
            "prepare: ready -> running",
            "prepare: running -> succeeded",
            "load: pending -> ready",
            "load: ready -> running",
            "stop requested",
            "grace period over",
            "load: running -> cancelled",
            "run ended: stopped",
        ]);
    }

    [Test]
    public async Task A_drain_before_a_body_s_end_starts_nothing_more_and_tells_no_task()
    {
        var prepare = new PausedBody();
        var reporter = new RecordingReporter();
        var run = ScriptedDriver.Start(Chain(prepare), Options(reporter, TimeSpan.Zero));
        run.Pump();

        run.Drain();
        run.PumpLoop();
        prepare.Release();
        var result = await run.Finish();

        result.Outcome.ShouldBe(RunOutcome.Stopped);
        reporter.Lines.ShouldBe(
        [
            "run started",
            "prepare: pending -> ready",
            "prepare: ready -> running",
            "drain requested",
            "prepare: running -> succeeded",
            "load: pending -> ready",
            "run ended: stopped",
        ]);
        result.Snapshot.Drain.ShouldNotBeNull();
        result.Snapshot.Stop.ShouldBeNull();
        (await prepare.Started).CancellationToken.IsCancellationRequested.ShouldBeFalse();
    }

    [Test]
    public async Task A_drain_after_a_body_s_end_lets_the_dependent_it_started_finish_and_a_run_in_which_all_succeed_is_complete()
    {
        var prepare = new PausedBody();
        var reporter = new RecordingReporter();
        var run = ScriptedDriver.Start(Chain(prepare), Options(reporter, TimeSpan.Zero));
        run.Pump();

        prepare.Release();
        run.PumpBodies();
        run.Drain();
        var result = await run.Finish();

        result.Outcome.ShouldBe(RunOutcome.Complete);
        reporter.Lines.ShouldBe(
        [
            "run started",
            "prepare: pending -> ready",
            "prepare: ready -> running",
            "prepare: running -> succeeded",
            "load: pending -> ready",
            "load: ready -> running",
            "drain requested",
            "load: running -> succeeded",
            "run ended: complete",
        ]);
    }

    [Test]
    public async Task A_token_already_cancelled_at_the_start_gives_the_start_the_ready_tasks_the_stop_and_the_end_with_no_task_started()
    {
        var prepare = new PausedBody();
        var reporter = new RecordingReporter();
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();

        var result = await ScriptedDriver.Start(Chain(prepare), Options(reporter, TimeSpan.Zero), stop.Token).Finish();

        result.Outcome.ShouldBe(RunOutcome.Stopped);
        reporter.Lines.ShouldBe(["run started", "prepare: pending -> ready", "stop requested", "run ended: stopped"]);
        prepare.Started.IsCompleted.ShouldBeFalse();
        result.Snapshot.NeverReached.Select(task => task.Path.ToString()).ShouldBe(["prepare", "load"]);
    }

    [Test]
    public async Task A_stop_can_follow_a_drain_and_a_request_made_again_or_after_the_end_changes_nothing()
    {
        var prepare = new PausedBody();
        var reporter = new RecordingReporter();
        using var stop = new CancellationTokenSource();
        var run = ScriptedDriver.Start(Chain(prepare), Options(reporter, Timeout.InfiniteTimeSpan), stop.Token);
        run.Pump();

        run.Drain();
        run.Drain();
        run.Stop();
        await stop.CancelAsync();
        run.Drain();
        run.Stop();
        run.PumpLoop();
        prepare.Release();
        var result = await run.Finish();
        run.Drain();
        run.Stop();
        run.Pump();

        result.Outcome.ShouldBe(RunOutcome.Stopped);
        reporter.Lines.ShouldBe(
        [
            "run started",
            "prepare: pending -> ready",
            "prepare: ready -> running",
            "drain requested",
            "stop requested",
            "prepare: running -> succeeded",
            "load: pending -> ready",
            "run ended: stopped",
        ]);
        result.Snapshot.Drain.ShouldNotBeNull();
        result.Snapshot.Stop!.By.ShouldBe(StopRequester.App);
        run.Current.ShouldBeSameAs(result.Snapshot);
    }

    [Test]
    public async Task A_limited_grace_period_ends_the_wait_only_when_it_runs_out_on_the_run_s_clock()
    {
        var clock = new FakeTimeProvider(_runStart);
        var prepare = new PausedBody();
        var reporter = new RecordingReporter();
        using var stop = new CancellationTokenSource();
        var run = ScriptedDriver.Start(Chain(prepare), Options(reporter, TimeSpan.FromSeconds(30)) with { TimeProvider = clock }, stop.Token);
        run.Pump();

        await stop.CancelAsync();
        run.Pump();
        clock.Advance(TimeSpan.FromSeconds(29));
        run.Stop();
        run.Drain();
        run.Pump();
        var beforeTheEnd = run.Current;
        clock.Advance(TimeSpan.FromSeconds(1));
        var result = await run.Finish();

        beforeTheEnd[NodePath.Parse("prepare")].Status.ShouldBe(NodeStatus.Running);
        result.Outcome.ShouldBe(RunOutcome.Stopped);
        reporter.Lines.ShouldBe(
        [
            "run started",
            "prepare: pending -> ready",
            "prepare: ready -> running",
            "stop requested",
            "grace period over",
            "prepare: running -> cancelled",
            "run ended: stopped",
        ]);
        result.Snapshot.Stop!.Deadline.ShouldBe(_runStart.AddSeconds(30));
        result.Snapshot.Ended.ShouldBe(_runStart.AddSeconds(30));
        result.Snapshot[NodePath.Parse("prepare")].Message.ShouldBe("still running when the run stopped waiting");
    }

    [Test]
    public async Task An_unlimited_grace_period_waits_for_the_running_task_however_long_it_takes()
    {
        var clock = new FakeTimeProvider(_runStart);
        var prepare = new PausedBody();
        var reporter = new RecordingReporter();
        var run = ScriptedDriver.Start(Chain(prepare), Options(reporter, Timeout.InfiniteTimeSpan) with { TimeProvider = clock });
        run.Pump();

        run.Stop();
        run.Pump();
        clock.Advance(TimeSpan.FromDays(100));
        run.Pump();
        var beforeTheEnd = run.Current;
        prepare.Release();
        var result = await run.Finish();

        beforeTheEnd[NodePath.Parse("prepare")].Status.ShouldBe(NodeStatus.Running);
        result.Outcome.ShouldBe(RunOutcome.Stopped);
        reporter.Lines.ShouldBe(
        [
            "run started",
            "prepare: pending -> ready",
            "prepare: ready -> running",
            "stop requested",
            "prepare: running -> succeeded",
            "load: pending -> ready",
            "run ended: stopped",
        ]);
        result.Snapshot.Stop!.Deadline.ShouldBeNull();
    }

    [Test]
    public async Task A_task_started_and_let_go_of_in_one_turn_whose_body_first_runs_after_the_run_has_ended_changes_nothing()
    {
        CancellationToken? seen = null;
        var builder = new GraphBuilder();
        builder.Task("prepare", context =>
        {
            seen = context.CancellationToken;
            return Task.CompletedTask;
        });
        var reporter = new RecordingReporter();
        var run = ScriptedDriver.Start(builder.Build(), Options(reporter, TimeSpan.Zero));

        run.Stop();
        run.PumpLoop();
        run.PumpReporters();
        run.PumpLoop();
        var result = await run.Result;
        run.PumpBodies().ShouldBe(1);
        run.Pump();

        seen!.Value.IsCancellationRequested.ShouldBeTrue();
        result.Outcome.ShouldBe(RunOutcome.Stopped);
        reporter.Lines.ShouldBe(
        [
            "run started",
            "prepare: pending -> ready",
            "prepare: ready -> running",
            "stop requested",
            "grace period over",
            "prepare: running -> cancelled",
            "run ended: stopped",
        ]);
        run.Current.ShouldBeSameAs(result.Snapshot);
    }

    [Test]
    public async Task The_tasks_token_is_signalled_before_the_run_s_result_is_available()
    {
        var prepare = new PausedBody();
        var run = ScriptedDriver.Start(Chain(prepare), Options(new RecordingReporter(), TimeSpan.Zero));
        run.Pump();
        var token = (await prepare.Started).CancellationToken;
        bool? signalledAtTheResult = null;
        _ = run.Result.ContinueWith(
            _ => signalledAtTheResult = token.IsCancellationRequested,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        run.Stop();
        await run.Finish();

        signalledAtTheResult.ShouldBe(true);
    }

    [Test]
    public async Task A_drain_and_a_stop_on_the_thread_pool_end_the_run_stopped()
    {
        var prepare = new PausedBody();
        var reporter = new RecordingReporter();
        var watched = new WatchedReporter(reporter);
        using var stop = new CancellationTokenSource();
        var drain = new DrainSignal();

        var running = NewEngine.RunAsync(
            Chain(prepare),
            new RunOptions { Journal = Journal.None, Reporters = [watched], GracePeriod = Timeout.InfiniteTimeSpan },
            drain,
            stop.Token);
        var context = await prepare.Started;
        drain.Request();
        await watched.Handled("drain requested");
        await stop.CancelAsync();
        await watched.Handled("stop requested");
        prepare.Release();
        var result = await running;

        result.Outcome.ShouldBe(RunOutcome.Stopped);
        context.CancellationToken.IsCancellationRequested.ShouldBeTrue();
        reporter.Lines.TakeLast(3).ShouldBe(["prepare: running -> succeeded", "load: pending -> ready", "run ended: stopped"]);
    }

    [Test]
    public async Task A_drain_signal_and_a_token_both_asked_before_the_call_go_with_the_start_on_the_thread_pool_the_drain_first()
    {
        var prepare = new PausedBody();
        var reporter = new RecordingReporter();
        var drain = new DrainSignal();
        drain.Request();
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();

        var result = await NewEngine.RunAsync(Chain(prepare), Options(reporter, TimeSpan.Zero), drain, stop.Token);

        result.Outcome.ShouldBe(RunOutcome.Stopped);
        reporter.Lines.ShouldBe(["run started", "prepare: pending -> ready", "drain requested", "stop requested", "run ended: stopped"]);
        prepare.Started.IsCompleted.ShouldBeFalse();
    }

    private static Graph Chain(PausedBody prepare)
    {
        var builder = new GraphBuilder();
        var prepareTask = builder.Task("prepare", prepare.Body);
        builder.Task("load", _ => Task.CompletedTask).Requires(prepareTask);
        return builder.Build();
    }

    private static RunOptions Options(RecordingReporter reporter, TimeSpan gracePeriod)
        => new() { Journal = Journal.None, Reporters = [reporter], GracePeriod = gracePeriod };
}
