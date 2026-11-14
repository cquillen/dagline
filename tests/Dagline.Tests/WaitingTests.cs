using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace Dagline.Tests;

public class WaitingTests
{
    private static readonly DateTimeOffset _runStart = new(2026, 11, 11, 4, 12, 0, TimeSpan.Zero);

    [Test]
    public async Task Opening_a_scope_begins_the_wait_and_closing_it_ends_the_wait_with_the_same_text_and_start_time()
    {
        var clock = new FakeTimeProvider(_runStart);
        var trigger = new PausedBody();
        var poll = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("job", async context =>
        {
            await trigger.Body(context);
            using (context.BeginWaiting("job 17 to finish"))
            {
                await poll.Body(context);
            }
        });
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, TimeProvider = clock, Reporters = [reporter] });
        await trigger.Started;
        clock.Advance(TimeSpan.FromSeconds(2));
        trigger.Release();
        await poll.Started;
        clock.Advance(TimeSpan.FromSeconds(98));
        poll.Release();
        await run;

        var waitBegan = _runStart.AddSeconds(2);
        var waitEnded = _runStart.AddSeconds(100);
        reporter.Lines.ShouldBe(
        [
            "run started",
            "job: pending -> ready",
            "job: ready -> running",
            "waiting job: job 17 to finish",
            "waited job",
            "job: running -> succeeded",
            "run ended: complete",
        ]);
        var waits = reporter.Events.OfType<WaitingChanged>().ToList();
        waits[0].ShouldBe(new WaitingChanged(waitBegan, NodePath.Parse("job"), Waiting: true, "job 17 to finish", Since: waitBegan));
        waits[1].ShouldBe(new WaitingChanged(waitEnded, NodePath.Parse("job"), Waiting: false, "job 17 to finish", Since: waitBegan));
    }

    [Test]
    public async Task The_node_shows_the_start_time_and_text_while_the_scope_is_open_and_neither_once_it_has_closed()
    {
        var clock = new FakeTimeProvider(_runStart);
        var poll = new PausedBody();
        var tidy = new PausedBody();
        var builder = new GraphBuilder();
        var jobTask = builder.Task("job", async context =>
        {
            using (context.BeginWaiting("job 17 to finish"))
            {
                await poll.Body(context);
            }

            await tidy.Body(context);
        });
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, TimeProvider = clock, Reporters = [reporter] });
        var source = await reporter.Source;
        await poll.Started;
        var waiting = source.Current[jobTask.Path];
        clock.Advance(TimeSpan.FromSeconds(30));
        poll.Release();
        await tidy.Started;
        var working = source.Current[jobTask.Path];
        tidy.Release();
        await run;

        waiting.WaitingSince.ShouldBe(_runStart);
        waiting.WaitingText.ShouldBe("job 17 to finish");
        working.WaitingSince.ShouldBeNull();
        working.WaitingText.ShouldBeNull();
        var began = reporter.Events.OfType<WaitingChanged>().First();
        reporter.Snapshots[reporter.Events.ToList().IndexOf(began)][jobTask.Path].WaitingSince.ShouldBe(_runStart);
    }

    [Test]
    public async Task Overlapping_scopes_make_one_wait_with_the_first_scopes_text_and_time_that_ends_when_the_last_scope_closes()
    {
        var clock = new FakeTimeProvider(_runStart);
        var firstOpen = new PausedBody();
        var secondOpen = new PausedBody();
        var onlySecondOpen = new PausedBody();
        var builder = new GraphBuilder();
        var jobTask = builder.Task("job", async context =>
        {
            var first = context.BeginWaiting("job 17 to finish");
            await firstOpen.Body(context);
            using (context.BeginWaiting("job 18 to finish"))
            {
                await secondOpen.Body(context);
                first.Dispose();
                await onlySecondOpen.Body(context);
            }
        });
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, TimeProvider = clock, Reporters = [reporter] });
        var source = await reporter.Source;
        await firstOpen.Started;
        clock.Advance(TimeSpan.FromSeconds(10));
        firstOpen.Release();
        await secondOpen.Started;
        clock.Advance(TimeSpan.FromSeconds(10));
        secondOpen.Release();
        await onlySecondOpen.Started;
        var afterTheFirstClosed = source.Current[jobTask.Path];
        clock.Advance(TimeSpan.FromSeconds(10));
        onlySecondOpen.Release();
        await run;

        reporter.Lines.ShouldBe(
        [
            "run started",
            "job: pending -> ready",
            "job: ready -> running",
            "waiting job: job 17 to finish",
            "waited job",
            "job: running -> succeeded",
            "run ended: complete",
        ]);
        reporter.Events.OfType<WaitingChanged>().Last()
            .ShouldBe(new WaitingChanged(_runStart.AddSeconds(30), jobTask.Path, Waiting: false, "job 17 to finish", Since: _runStart));
        afterTheFirstClosed.WaitingSince.ShouldBe(_runStart);
        afterTheFirstClosed.WaitingText.ShouldBe("job 17 to finish");
    }

    [Test]
    public async Task Two_waits_one_after_the_other_give_two_pairs_of_events()
    {
        var builder = new GraphBuilder();
        builder.Task("job", context =>
        {
            using (context.BeginWaiting("job 17 to finish"))
            {
            }

            using (context.BeginWaiting())
            {
            }

            return Task.CompletedTask;
        });
        var reporter = new RecordingReporter();

        await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        reporter.Lines.ShouldBe(
        [
            "run started",
            "job: pending -> ready",
            "job: ready -> running",
            "waiting job: job 17 to finish",
            "waited job",
            "waiting job",
            "waited job",
            "job: running -> succeeded",
            "run ended: complete",
        ]);
    }

    [Test]
    public async Task Disposing_a_scope_twice_closes_it_once()
    {
        var stillOpen = new PausedBody();
        var builder = new GraphBuilder();
        var jobTask = builder.Task("job", async context =>
        {
            using (context.BeginWaiting("job 17 to finish"))
            {
                var inner = context.BeginWaiting("job 18 to finish");
                inner.Dispose();
                inner.Dispose();
                await stillOpen.Body(context);
            }
        });
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        var source = await reporter.Source;
        await stillOpen.Started;
        var waiting = source.Current[jobTask.Path];
        stillOpen.Release();
        await run;

        waiting.WaitingText.ShouldBe("job 17 to finish");
        reporter.Lines.Count(line => line.StartsWith("waiting", StringComparison.Ordinal)).ShouldBe(1);
        reporter.Lines.Count(line => line.StartsWith("waited", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Test]
    public async Task A_task_that_settles_with_a_scope_open_ends_its_wait_immediately_before_its_status_change()
    {
        IDisposable? leftOpen = null;
        var builder = new GraphBuilder();
        var jobTask = builder.Task("job", context =>
        {
            leftOpen = context.BeginWaiting("job 17 to finish");
            throw new InvalidOperationException("job 17 was rejected");
        });
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        leftOpen!.Dispose();

        reporter.Lines.ShouldBe(
        [
            "run started",
            "job: pending -> ready",
            "job: ready -> running",
            "waiting job: job 17 to finish",
            "waited job",
            "job: running -> failed",
            "run ended: incomplete",
        ]);
        var settledAt = reporter.Events.OfType<TaskStatusChanged>().Single(changed => changed.To is NodeStatus.Failed);
        reporter.Events.OfType<WaitingChanged>().Last().At.ShouldBe(settledAt.At);
        settledAt.Node.WaitingSince.ShouldBeNull();
        foreach (var snapshot in reporter.Snapshots.Where(snapshot => snapshot[jobTask.Path].Status is NodeStatus.Failed))
        {
            snapshot[jobTask.Path].WaitingSince.ShouldBeNull();
            snapshot[jobTask.Path].WaitingText.ShouldBeNull();
        }

        result.Snapshot[jobTask.Path].WaitingSince.ShouldBeNull();
    }

    [Test]
    public async Task A_waiting_task_is_running_and_its_group_and_the_run_are_what_they_would_be_without_the_marker()
    {
        var poll = new PausedBody();
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1");
        var jobTask = shard.Task("job", async context =>
        {
            using (context.BeginWaiting("job 17 to finish"))
            {
                await poll.Body(context);
            }
        });
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        var source = await reporter.Source;
        await poll.Started;
        var waiting = source.Current;
        poll.Release();
        var result = await run;

        waiting[jobTask.Path].WaitingSince.ShouldNotBeNull();
        waiting[jobTask.Path].Status.ShouldBe(NodeStatus.Running);
        waiting[shard.Path].Status.ShouldBe(NodeStatus.Running);
        waiting[shard.Path].Counts[NodeStatus.Running].ShouldBe(1);
        waiting.Counts[NodeStatus.Running].ShouldBe(1);
        waiting.RunningTasks.InUse.ShouldBe(1);
        result.Outcome.ShouldBe(RunOutcome.Complete);
        result.Snapshot[shard.Path].Status.ShouldBe(NodeStatus.Succeeded);
    }

    [Test]
    public async Task A_waiting_task_keeps_its_slots_so_a_second_task_that_holds_the_limit_stays_ready()
    {
        var poll = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("job", async context =>
        {
            using (context.BeginWaiting("job 17 to finish"))
            {
                await poll.Body(context);
            }
        }).Limit("external");
        var otherTask = builder.Task("other", _ => Task.CompletedTask).Limit("external");
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(
            builder.Build(),
            new RunOptions { Journal = Journal.None, Reporters = [reporter], Limits = new Dictionary<string, int> { ["external"] = 1 } });
        var source = await reporter.Source;
        await poll.Started;
        var waiting = source.Current;
        poll.Release();
        await run;

        waiting[otherTask.Path].Status.ShouldBe(NodeStatus.Ready);
        waiting.Limits["external"].ShouldBe(new LimitUse(Size: 1, InUse: 1));
        reporter.Lines.ShouldBe(
        [
            "run started",
            "job: pending -> ready",
            "other: pending -> ready",
            "job: ready -> running",
            "waiting job: job 17 to finish",
            "waited job",
            "job: running -> succeeded",
            "other: ready -> running",
            "other: running -> succeeded",
            "run ended: complete",
        ]);
    }

    [Test]
    public async Task Progress_set_while_waiting_is_in_the_snapshot()
    {
        var poll = new PausedBody();
        var builder = new GraphBuilder();
        var jobTask = builder.Task("job", async context =>
        {
            using (context.BeginWaiting("job 17 to finish"))
            {
                context.SetStatusText("job 17 is 40% done");
                context.SetCount(40, 100);
                await poll.Body(context);
            }
        });
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        var source = await reporter.Source;
        await poll.Started;
        var waiting = source.Current[jobTask.Path];
        poll.Release();
        await run;

        waiting.WaitingText.ShouldBe("job 17 to finish");
        waiting.StatusText.ShouldBe("job 17 is 40% done");
        waiting.Count.ShouldBe(40);
        waiting.Total.ShouldBe(100);
    }

    [Test]
    public async Task A_scope_opened_after_the_task_has_settled_does_nothing()
    {
        TaskContext? kept = null;
        var builder = new GraphBuilder();
        var jobTask = builder.Task("job", context =>
        {
            kept = context;
            return Task.CompletedTask;
        });
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        var late = kept!.BeginWaiting("job 17 to finish");
        late.Dispose();
        late.Dispose();

        late.ShouldNotBeNull();
        reporter.Events.OfType<WaitingChanged>().ShouldBeEmpty();
        result.Snapshot[jobTask.Path].WaitingSince.ShouldBeNull();
        (await reporter.Source).Current[jobTask.Path].WaitingSince.ShouldBeNull();
    }
}
