using Dagline.Testing;

namespace Dagline.Samples.Batch.Tests;

public sealed class BatchTasksTests
{
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(2);

    [Test]
    public async Task Validate_on_a_tenant_seeded_to_warn_succeeds_with_its_warning()
    {
        var store = new EntityStore(timeScale: 1);
        await using var harness = TaskHarness.For(context => BatchTasks.Validate("tenant-1-019", store, context));

        var outcome = await harness.RunAsync();

        outcome.Status.ShouldBe(NodeStatus.Succeeded);
        outcome.Warnings.ShouldBe(["some entities have no owner; they were kept as they are"]);
    }

    [Test]
    public async Task The_job_task_waits_for_its_job_and_succeeds()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            // One instant clock for both: the job's run time and the task's wait pass at once.
            var clock = new InstantClock();
            var scheduler = new FakeScheduler(directory.FullName, clock);
            await using var harness = TaskHarness.For(context => BatchTasks.Job("shard-1", scheduler, _interval, context), clock);

            var outcome = await harness.RunAsync();

            outcome.Status.ShouldBe(NodeStatus.Succeeded);
            outcome.Waited.ShouldBeTrue();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public async Task The_job_task_stopped_at_its_wait_re_attaches_in_the_next_run_without_a_second_trigger()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            // The scheduler keeps time of its own, as a real one keeps running while the app is down. The harness is
            // given no clock, so it stops the task as soon as the task starts to wait.
            var schedulerClock = new InstantClock();
            var scheduler = new FakeScheduler(directory.FullName, schedulerClock);
            await using var harness = TaskHarness.For(context => BatchTasks.Job("shard-1", scheduler, _interval, context));

            var stopped = await harness.RunUntilWaitingThenStopAsync();

            stopped.Status.ShouldBe(NodeStatus.Cancelled);
            stopped.Checkpoint.ShouldNotBeEmpty();
            stopped.StatusText.ShouldBe($"job {stopped.Checkpoint}");
            stopped.WaitingText.ShouldBe($"job {stopped.Checkpoint} to finish");
            scheduler.Triggers.ShouldBe(1);

            // A job takes at most 40 seconds. Waiting a minute on the scheduler's instant clock moves it past the end
            // of the job at once.
            await Task.Delay(TimeSpan.FromMinutes(1), schedulerClock);

            var continued = await harness.RunAsync();

            continued.Status.ShouldBe(NodeStatus.Succeeded);
            scheduler.Triggers.ShouldBe(1);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public async Task The_job_task_handed_a_job_in_its_checkpoint_waits_for_that_job_without_triggering_another()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var clock = new InstantClock();
            var scheduler = new FakeScheduler(directory.FullName, clock);
            var job = await scheduler.TriggerAsync("shard-1", CancellationToken.None);
            await using var harness = TaskHarness.For(
                context => BatchTasks.Job("shard-1", scheduler, _interval, context),
                clock,
                checkpoint: job);

            var outcome = await harness.RunAsync();

            outcome.Status.ShouldBe(NodeStatus.Succeeded);
            outcome.WaitingText.ShouldBe($"job {job} to finish");
            outcome.Checkpoint.ShouldBe("");
            scheduler.Triggers.ShouldBe(1);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
