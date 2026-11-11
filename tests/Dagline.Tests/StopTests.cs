using Dagline.TestSupport;

namespace Dagline.Tests;

public class StopTests
{
    private const int _repeats = 50;

    [Test]
    public async Task A_signal_requested_before_the_run_starts_starts_nothing_and_the_run_is_stopped()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var builder = new GraphBuilder();
            var load = builder.Task("load", _ => Task.CompletedTask);
            var verify = builder.Task("verify", _ => Task.CompletedTask);
            var publish = builder.Task("publish", _ => Task.CompletedTask).Requires(load);
            var stop = new StopSignal();
            stop.Request();
            var reporter = new RecordingReporter();

            var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] }, stop);

            result.Outcome.ShouldBe(RunOutcome.Stopped);
            reporter.Lines.ShouldBe(
            [
                "run started",
                "load: pending -> ready",
                "verify: pending -> ready",
                "stop requested",
                "run ended: stopped",
            ]);
            result.Snapshot.NeverReached.Select(task => task.Path).ShouldBe([load.Path, verify.Path, publish.Path]);
            result.Snapshot.Cancelled.ShouldBeEmpty();
            result.Snapshot[publish.Path].Status.ShouldBe(NodeStatus.Pending);
        }
    }

    [Test]
    public async Task A_run_given_a_signal_that_is_never_requested_runs_to_its_end()
    {
        var builder = new GraphBuilder();
        var load = builder.Task("load", _ => Task.CompletedTask);
        builder.Task("publish", _ => Task.CompletedTask).Requires(load);
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(
            builder.Build(),
            new RunOptions { Journal = Journal.None, Reporters = [reporter] },
            new StopSignal());

        result.Outcome.ShouldBe(RunOutcome.Complete);
        reporter.Lines.ShouldNotContain("stop requested");
        result.Snapshot.Stop.ShouldBeNull();
        result.Snapshot.NeverReached.ShouldBeEmpty();
    }

    [Test]
    public async Task An_empty_graph_with_a_stop_is_complete()
    {
        var stop = new StopSignal();
        stop.Request();
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(new GraphBuilder().Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] }, stop);

        result.Outcome.ShouldBe(RunOutcome.Complete);
        reporter.Lines.ShouldBe(["run started", "stop requested", "run ended: complete"]);
    }

    [Test]
    public async Task After_a_stop_nothing_starts_and_running_tasks_see_their_token_signalled()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var honour = new StoppedBody();
            var finish = new StoppedBody(_ => { });
            var builder = new GraphBuilder();
            var honourTask = builder.Task("honour", honour.Body);
            var finishTask = builder.Task("finish", finish.Body);
            var queued = builder.Task("queued", _ => Task.CompletedTask);
            var then = builder.Task("then", _ => Task.CompletedTask).Requires(finishTask);
            var needs = builder.Task("needs", _ => Task.CompletedTask).Requires(honourTask);
            var tidy = builder.Task("tidy", _ => Task.CompletedTask).After(honourTask);
            var stop = new StopSignal();
            var reporter = new RecordingReporter();

            var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Cap = 2, Reporters = [reporter] }, stop);
            await honour.Started;
            await finish.Started;
            stop.Request();
            var result = await run;

            result.Outcome.ShouldBe(RunOutcome.Stopped);
            var lines = reporter.Lines.ToList();
            var stopAt = lines.IndexOf("stop requested");
            stopAt.ShouldBeGreaterThan(0);
            lines.Skip(stopAt).ShouldNotContain(line => line.EndsWith("ready -> running", StringComparison.Ordinal));
            lines.ShouldContain("honour: running -> cancelled");
            lines.ShouldContain("finish: running -> succeeded");
            lines.ShouldContain("then: pending -> ready");
            lines.ShouldContain("tidy: pending -> ready");
            lines[^1].ShouldBe("run ended: stopped");

            var snapshot = result.Snapshot;
            snapshot[honourTask.Path].Status.ShouldBe(NodeStatus.Cancelled);
            snapshot[finishTask.Path].Status.ShouldBe(NodeStatus.Succeeded);
            snapshot[queued.Path].Status.ShouldBe(NodeStatus.Ready);
            snapshot[then.Path].Status.ShouldBe(NodeStatus.Ready);
            snapshot[needs.Path].Status.ShouldBe(NodeStatus.Pending);
            snapshot[tidy.Path].Status.ShouldBe(NodeStatus.Ready);
            snapshot.Cancelled.Select(task => task.Path).ShouldBe([honourTask.Path]);
            snapshot.NeverReached.Select(task => task.Path).ShouldBe([queued.Path, then.Path, needs.Path, tidy.Path]);
            (await honour.Started).CancellationToken.IsCancellationRequested.ShouldBeTrue();
        }
    }

    [Test]
    public async Task A_body_that_ends_with_any_cancellation_exception_after_a_stop_is_cancelled()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var ownToken = new StoppedBody(_ => throw new OperationCanceledException(new CancellationToken(canceled: true)));
            var noToken = new StoppedBody(_ => throw new TaskCanceledException("gave up"));
            var builder = new GraphBuilder();
            var ownTokenTask = builder.Task("own-token", ownToken.Body);
            var noTokenTask = builder.Task("no-token", noToken.Body);
            var stop = new StopSignal();

            var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None }, stop);
            await ownToken.Started;
            await noToken.Started;
            stop.Request();
            var result = await run;

            result.Outcome.ShouldBe(RunOutcome.Stopped);
            result.Snapshot[ownTokenTask.Path].Status.ShouldBe(NodeStatus.Cancelled);
            result.Snapshot[ownTokenTask.Path].Failure.ShouldBeNull();
            result.Snapshot[noTokenTask.Path].Status.ShouldBe(NodeStatus.Cancelled);
        }
    }

    [Test]
    public async Task A_body_that_throws_anything_else_after_a_stop_is_failed_and_blocks_its_dependents()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var load = new StoppedBody(_ => throw new InvalidOperationException("the store went away"));
            var builder = new GraphBuilder();
            var loadTask = builder.Task("load", load.Body);
            var publish = builder.Task("publish", _ => Task.CompletedTask).Requires(loadTask);
            var stop = new StopSignal();
            var reporter = new RecordingReporter();

            var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] }, stop);
            await load.Started;
            stop.Request();
            var result = await run;

            result.Outcome.ShouldBe(RunOutcome.Stopped);
            reporter.Lines.ShouldBe(
            [
                "run started",
                "load: pending -> ready",
                "load: ready -> running",
                "stop requested",
                "load: running -> failed",
                "publish: pending -> blocked",
                "run ended: stopped",
            ]);
            result.Snapshot[loadTask.Path].Failure.ShouldBe(FailureKind.Unexpected);
            result.Snapshot[loadTask.Path].Message.ShouldBe("the store went away");
            result.Snapshot.NeverReached.ShouldBeEmpty();
        }
    }

    [Test]
    public async Task A_reported_failure_followed_by_a_cancellation_after_a_stop_is_a_reported_failure()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var load = new StoppedBody(context =>
            {
                _ = context.Fail("entity 7 has no owner");
                context.CancellationToken.ThrowIfCancellationRequested();
            });
            var builder = new GraphBuilder();
            var loadTask = builder.Task("load", load.Body);
            var stop = new StopSignal();

            var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None }, stop);
            await load.Started;
            stop.Request();
            var result = await run;

            var failed = result.Snapshot[loadTask.Path];
            failed.Status.ShouldBe(NodeStatus.Failed);
            failed.Failure.ShouldBe(FailureKind.Reported);
            failed.Message.ShouldBe("entity 7 has no owner");
        }
    }

    [Test]
    public async Task A_cancelled_task_keeps_its_message_detail_warnings_and_frozen_progress()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var load = new StoppedBody(context =>
            {
                context.Status("loading entities");
                context.Count(40, 100);
                context.Warn("entity 9 has no region");
                context.Message("loaded 40 of 100 entities");
                context.Detail("stopped at entity 41");
                context.CancellationToken.ThrowIfCancellationRequested();
            });
            var builder = new GraphBuilder();
            var loadTask = builder.Task("load", load.Body);
            var stop = new StopSignal();

            var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None }, stop);
            await load.Started;
            stop.Request();
            var result = await run;

            var cancelled = result.Snapshot[loadTask.Path];
            cancelled.Status.ShouldBe(NodeStatus.Cancelled);
            cancelled.StatusText.ShouldBe("loading entities");
            cancelled.Count.ShouldBe(40);
            cancelled.Total.ShouldBe(100);
            cancelled.Warnings.ShouldBe(["entity 9 has no region"]);
            cancelled.Message.ShouldBe("loaded 40 of 100 entities");
            cancelled.Detail.ShouldBe(["stopped at entity 41"]);
            cancelled.Settled.ShouldNotBeNull();
        }
    }

    [Test]
    public async Task A_settled_group_with_a_cancelled_task_is_cancelled_and_one_with_a_task_never_reached_does_not_settle()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var load = new StoppedBody();
            var builder = new GraphBuilder();
            var extract = builder.Group("extract");
            var loadTask = extract.Task("load", load.Body);
            extract.Task("verify", _ => Task.CompletedTask);
            var publish = builder.Group("publish");
            publish.Task("send", _ => Task.CompletedTask).Requires(loadTask);
            var stop = new StopSignal();
            var reporter = new RecordingReporter();
            var watched = new WatchedReporter(reporter);

            var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [watched] }, stop);
            await load.Started;
            await watched.Handled("extract/verify: running -> succeeded");
            stop.Request();
            var result = await run;

            reporter.Lines.ShouldContain("group extract settled: cancelled");
            reporter.Lines.ShouldNotContain(line => line.StartsWith("group publish", StringComparison.Ordinal));
            result.Snapshot[extract.Path].Status.ShouldBe(NodeStatus.Cancelled);
            result.Snapshot[publish.Path].Status.ShouldBe(NodeStatus.Pending);
        }
    }

    [Test]
    public async Task A_run_in_which_every_task_succeeds_despite_a_stop_is_complete()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var load = new StoppedBody(_ => { });
            var builder = new GraphBuilder();
            builder.Task("load", load.Body);
            var stop = new StopSignal();

            var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None }, stop);
            await load.Started;
            stop.Request();
            var result = await run;

            result.Outcome.ShouldBe(RunOutcome.Complete);
            result.Snapshot.Stop.ShouldNotBeNull();
        }
    }

    [Test]
    public async Task The_snapshot_carries_the_stop_from_the_stop_requested_event_on_and_never_reached_only_once_the_run_has_ended()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var load = new StoppedBody();
            var builder = new GraphBuilder();
            var loadTask = builder.Task("load", load.Body);
            var publish = builder.Task("publish", _ => Task.CompletedTask).Requires(loadTask);
            var stop = new StopSignal();
            var reporter = new RecordingReporter();

            var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] }, stop);
            await load.Started;
            stop.Request();
            var result = await run;

            var events = reporter.Events;
            var snapshots = reporter.Snapshots;
            var stopAt = events.ToList().FindIndex(runEvent => runEvent is StopRequested);
            var requested = (StopRequested)events[stopAt];
            snapshots.Take(stopAt).ShouldAllBe(snapshot => snapshot.Stop == null);
            snapshots.Skip(stopAt).ShouldAllBe(snapshot => snapshot.Stop != null && snapshot.Stop.RequestedAt == requested.At);
            snapshots.SkipLast(1).ShouldAllBe(snapshot => snapshot.NeverReached.Count == 0);
            result.Snapshot.NeverReached.Select(task => task.Path).ShouldBe([publish.Path]);
            result.Snapshot.Stop.ShouldBe(snapshots[stopAt].Stop);
        }
    }

    [Test]
    public async Task A_signal_passed_to_a_second_run_after_the_first_has_ended_stops_that_run_from_its_start()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var load = new StoppedBody();
            var builder = new GraphBuilder();
            builder.Task("load", load.Body);
            var graph = builder.Build();
            var stop = new StopSignal();

            var first = Runner.RunAsync(graph, new RunOptions { Journal = Journal.None }, stop);
            await load.Started;
            stop.Request();
            (await first).Outcome.ShouldBe(RunOutcome.Stopped);

            var reporter = new RecordingReporter();
            var second = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.None, Reporters = [reporter] }, stop);

            second.Outcome.ShouldBe(RunOutcome.Stopped);
            reporter.Lines.ShouldBe(["run started", "load: pending -> ready", "stop requested", "run ended: stopped"]);
        }
    }
}
