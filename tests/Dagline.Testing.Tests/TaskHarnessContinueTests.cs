using Dagline.TestSupport;

namespace Dagline.Testing.Tests;

public class TaskHarnessContinueTests
{
    private const int _repeats = 50;

    [Test]
    public async Task A_second_run_hands_the_task_the_checkpoint_it_saved_before_failing()
    {
        var seen = new List<string>();
        await using var harness = TaskHarness.For(async context =>
        {
            seen.Add(context.Checkpoint);
            await context.SaveCheckpointAsync("job-17");
            throw context.Fail("job 17 failed");
        });

        await harness.RunAsync();
        await harness.RunAsync();

        seen.ShouldBe(["", "job-17"]);
    }

    [Test]
    public async Task The_outcome_holds_the_checkpoint_the_task_saved_before_failing_and_the_next_run_sees_it_too()
    {
        await using var harness = TaskHarness.For(async context =>
        {
            await context.SaveCheckpointAsync(context.Checkpoint + "a");
            throw context.Fail("not yet");
        });

        var first = await harness.RunAsync();
        var second = await harness.RunAsync();

        first.Checkpoint.ShouldBe("a");
        second.Checkpoint.ShouldBe("aa");
    }

    [Test]
    public async Task A_task_that_saves_no_checkpoint_has_an_empty_one_in_its_outcome()
    {
        await using var harness = TaskHarness.For(context => throw context.Fail("no job"));

        var outcome = await harness.RunAsync();

        outcome.Checkpoint.ShouldBe("");
    }

    [Test]
    public async Task A_cleared_checkpoint_is_empty_in_the_outcome()
    {
        await using var harness = TaskHarness.For(
            async context =>
            {
                await context.ClearCheckpointAsync();
                throw context.Fail("the job was lost");
            },
            checkpoint: "job-17");

        var outcome = await harness.RunAsync();

        outcome.Checkpoint.ShouldBe("");
    }

    [Test]
    public async Task A_success_discards_the_checkpoint_so_the_outcome_and_a_further_run_have_none()
    {
        await using var harness = TaskHarness.For(context => context.SaveCheckpointAsync("job-17"));

        var first = await harness.RunAsync();
        var second = await harness.RunAsync();

        first.Status.ShouldBe(NodeStatus.Succeeded);
        first.Checkpoint.ShouldBe("");
        second.Checkpoint.ShouldBe("");
    }

    [Test]
    public async Task A_seeded_checkpoint_is_handed_to_the_task_in_the_first_run()
    {
        var seen = new List<string>();
        await using var harness = TaskHarness.For(
            context =>
            {
                seen.Add(context.Checkpoint);
                throw context.Fail("not yet");
            },
            checkpoint: "job-17");

        var first = await harness.RunAsync();
        var second = await harness.RunAsync();

        seen.ShouldBe(["job-17", "job-17"]);
        first.Checkpoint.ShouldBe("job-17");
        second.Checkpoint.ShouldBe("job-17");
    }

    [Test]
    public async Task A_seeded_checkpoint_replaced_in_the_first_run_is_not_planted_again()
    {
        var seen = new List<string>();
        await using var harness = TaskHarness.For(
            async context =>
            {
                seen.Add(context.Checkpoint);
                await context.SaveCheckpointAsync("job-18");
                throw context.Fail("not yet");
            },
            checkpoint: "job-17");

        await harness.RunAsync();
        await harness.RunAsync();

        seen.ShouldBe(["job-17", "job-18"]);
    }

    [Test]
    public async Task A_task_seeded_with_its_job_re_attaches_without_triggering_and_without_a_first_run()
    {
        var system = new FakeExternalSystem();
        await using var harness = TaskHarness.For(context => TriggerOrReattach(context, system), checkpoint: "job-1");

        var outcome = await harness.RunAsync();

        outcome.Status.ShouldBe(NodeStatus.Succeeded);
        outcome.Message.ShouldBe("job-1 finished");
        outcome.WaitingText.ShouldBe("job-1 to finish");
        system.Triggers.ShouldBe(0);
    }

    [Test]
    public async Task A_task_stopped_at_its_wait_comes_back_with_the_checkpoint_and_the_waiting_text_the_operator_saw()
    {
        var system = new FakeExternalSystem();
        await using var harness = TaskHarness.For(context => TriggerOrReattach(context, system));

        var stopped = await harness.RunUntilWaitingThenStopAsync();

        stopped.Status.ShouldBe(NodeStatus.Cancelled);
        stopped.Checkpoint.ShouldBe("job-1");
        stopped.WaitingText.ShouldBe("job-1 to finish");
    }

    [Test]
    public async Task After_a_success_a_further_run_does_not_call_the_body_and_returns_the_same_outcome_without_progress_or_waiting()
    {
        var calls = 0;
        await using var harness = TaskHarness.For(async context =>
        {
            calls++;
            await context.WaitUntilAsync(TimeSpan.FromMinutes(1), _ => Task.FromResult(true));
            context.AddWarning("the export was slow");
            context.AddDetail("17 rows", "3 files");
            context.SetMessage("export done");
            context.SetStatusText("17 rows written");
        });

        var first = await harness.RunAsync();
        var second = await harness.RunAsync();

        calls.ShouldBe(1);
        first.Waited.ShouldBeTrue();
        second.Status.ShouldBe(NodeStatus.Succeeded);
        second.Failure.ShouldBeNull();
        second.Message.ShouldBe("export done");
        second.Detail.ShouldBe(["17 rows", "3 files"]);
        second.Warnings.ShouldBe(["the export was slow"]);
        second.Waited.ShouldBeFalse();
        first.StatusText.ShouldBe("17 rows written");
        second.StatusText.ShouldBeNull();
        second.WaitingText.ShouldBeNull();
    }

    [Test]
    [NotInParallel(DiskHeavy.Key)]
    public async Task A_task_stopped_at_its_wait_re_attaches_in_the_next_run_and_the_external_system_sees_one_trigger()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var system = new FakeExternalSystem();
            await using var harness = TaskHarness.For(context => TriggerOrReattach(context, system));

            var stopped = await harness.RunUntilWaitingThenStopAsync();
            var continued = await harness.RunAsync();
            var third = await harness.RunAsync();

            stopped.Status.ShouldBe(NodeStatus.Cancelled);
            stopped.Waited.ShouldBeTrue();
            continued.Status.ShouldBe(NodeStatus.Succeeded);
            continued.Message.ShouldBe("job-1 finished");
            third.Status.ShouldBe(NodeStatus.Succeeded);
            system.Triggers.ShouldBe(1);
        }
    }

    private static async Task TriggerOrReattach(TaskContext context, FakeExternalSystem system)
    {
        var job = context.Checkpoint;
        if (job == "")
        {
            job = system.Trigger();
            await context.SaveCheckpointAsync(job);
        }

        await context.WaitUntilAsync(TimeSpan.FromMinutes(5), _ => Task.FromResult(system.IsFinished(job)), $"{job} to finish");
        context.SetMessage($"{job} finished");
    }

    private sealed class FakeExternalSystem
    {
        private int _checks;

        public int Triggers { get; private set; }

        public string Trigger()
        {
            Triggers++;
            return $"job-{Triggers}";
        }

        public bool IsFinished(string job) => job == "job-1" && ++_checks > 3;
    }
}
