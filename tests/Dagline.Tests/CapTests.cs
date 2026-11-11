using Dagline.TestSupport;

namespace Dagline.Tests;

public class CapTests
{
    private const int Repeats = 50;

    [Test]
    public async Task The_number_of_task_bodies_running_at_once_never_exceeds_the_cap()
    {
        const int cap = 3;

        for (var repeat = 0; repeat < Repeats; repeat++)
        {
            var bodies = new OverlappingBodies(expected: cap);
            var builder = new GraphBuilder();
            for (var shard = 1; shard <= 20; shard++)
            {
                builder.Task($"shard-{shard}", bodies.Body);
            }

            var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Cap = cap });

            result.Outcome.ShouldBe(RunOutcome.Complete);
            bodies.Highest.ShouldBe(cap);
        }
    }

    [Test]
    public async Task Whenever_a_task_settles_the_next_ready_task_in_declaration_order_starts()
    {
        var bodies = Enumerable.Range(0, 4).Select(_ => new PausedBody()).ToList();
        var builder = new GraphBuilder();
        builder.Task("shard-1", bodies[0].Body);
        builder.Task("shard-2", bodies[1].Body);
        builder.Task("shard-3", bodies[2].Body);
        builder.Task("shard-4", bodies[3].Body);
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Cap = 2, Reporters = [reporter] });
        await bodies[0].Started;
        await bodies[1].Started;
        bodies[1].Release();
        await bodies[2].Started;
        bodies[0].Release();
        await bodies[3].Started;
        bodies[2].Release();
        bodies[3].Release();
        var result = await run;

        result.Outcome.ShouldBe(RunOutcome.Complete);
        reporter.Lines.Take(11).ShouldBe(
        [
            "run started",
            "shard-1: pending -> ready",
            "shard-2: pending -> ready",
            "shard-3: pending -> ready",
            "shard-4: pending -> ready",
            "shard-1: ready -> running",
            "shard-2: ready -> running",
            "shard-2: running -> succeeded",
            "shard-3: ready -> running",
            "shard-1: running -> succeeded",
            "shard-4: ready -> running",
        ]);
    }

    [Test]
    public async Task With_a_cap_of_one_the_ready_tasks_run_one_at_a_time_in_declaration_order()
    {
        var builder = new GraphBuilder();
        builder.Task("shard-1", _ => Task.CompletedTask);
        builder.Task("shard-2", _ => Task.CompletedTask);
        builder.Task("shard-3", _ => Task.CompletedTask);
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(
            builder.Build(),
            new RunOptions { Journal = Journal.None, Cap = 1, Reporters = [reporter] });

        result.Outcome.ShouldBe(RunOutcome.Complete);
        reporter.Lines.ShouldBe(
        [
            "run started",
            "shard-1: pending -> ready",
            "shard-2: pending -> ready",
            "shard-3: pending -> ready",
            "shard-1: ready -> running",
            "shard-1: running -> succeeded",
            "shard-2: ready -> running",
            "shard-2: running -> succeeded",
            "shard-3: ready -> running",
            "shard-3: running -> succeeded",
            "run ended: complete",
        ]);
    }
}
