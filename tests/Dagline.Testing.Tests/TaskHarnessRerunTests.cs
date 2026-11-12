namespace Dagline.Testing.Tests;

public class TaskHarnessRerunTests
{
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
    public async Task After_a_success_a_further_run_does_not_call_the_body_and_returns_the_same_outcome()
    {
        var calls = 0;
        await using var harness = TaskHarness.For(async context =>
        {
            calls++;
            await context.WaitUntil(TimeSpan.FromMinutes(1), _ => Task.FromResult(true));
            context.Warn("the export was slow");
            context.Detail("17 rows", "3 files");
            context.Message("export done");
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
    }

    [Test]
    public async Task A_task_stopped_at_its_wait_re_attaches_in_the_next_run_and_the_external_system_sees_one_trigger()
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

    private static async Task TriggerOrReattach(TaskContext context, FakeExternalSystem system)
    {
        var job = context.Checkpoint;
        if (job == "")
        {
            job = system.Trigger();
            await context.SaveCheckpointAsync(job);
        }

        await context.WaitUntil(TimeSpan.FromMinutes(5), _ => Task.FromResult(system.IsFinished(job)), $"{job} to finish");
        context.Message($"{job} finished");
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
