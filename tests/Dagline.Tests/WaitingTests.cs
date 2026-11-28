using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace Dagline.Tests;

public class WaitingTests
{
    private static readonly DateTimeOffset _runStart = new(2026, 11, 11, 4, 12, 0, TimeSpan.Zero);

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
        (waiting.Limits["external"].Size, waiting.Limits["external"].InUse).ShouldBe((1, 1));
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

    private static (DateTimeOffset At, NodePath Path, bool Waiting, string? Text, DateTimeOffset Since) WaitFacts(WaitingChanged wait)
        => (wait.At, wait.Path, wait.Waiting, wait.Text, wait.Since);
}
