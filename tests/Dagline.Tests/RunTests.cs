using Dagline.TestSupport;

namespace Dagline.Tests;

public class RunTests
{
    [Test]
    public async Task A_chain_of_tasks_runs_in_order_and_the_run_is_complete()
    {
        var builder = new GraphBuilder();
        var prepare = builder.Task("prepare", _ => Task.CompletedTask);
        var process = builder.Task("process", _ => Task.CompletedTask).Requires(prepare);
        builder.Task("publish", _ => Task.CompletedTask).Requires(process);
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(
            builder.Build(),
            new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        result.Outcome.ShouldBe(RunOutcome.Complete);
        reporter.Lines.ShouldBe(
        [
            "run started",
            "prepare: pending -> ready",
            "prepare: ready -> running",
            "prepare: running -> succeeded",
            "process: pending -> ready",
            "process: ready -> running",
            "process: running -> succeeded",
            "publish: pending -> ready",
            "publish: ready -> running",
            "publish: running -> succeeded",
            "run ended: complete",
        ]);
    }

    [Test]
    public async Task A_task_that_requires_several_tasks_becomes_ready_only_once_all_of_them_have_succeeded()
    {
        var builder = new GraphBuilder();
        var first = builder.Task("shard-1", _ => Task.CompletedTask);
        var second = builder.Task("shard-2", _ => Task.CompletedTask);
        builder.Task("merge", _ => Task.CompletedTask).Requires(first).Requires(second);
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(
            builder.Build(),
            new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        result.Outcome.ShouldBe(RunOutcome.Complete);
        var lines = reporter.Lines.ToList();
        var mergeReady = lines.IndexOf("merge: pending -> ready");
        mergeReady.ShouldBeGreaterThan(lines.IndexOf("shard-1: running -> succeeded"));
        mergeReady.ShouldBeGreaterThan(lines.IndexOf("shard-2: running -> succeeded"));
        lines.Skip(mergeReady).ShouldBe(
        [
            "merge: pending -> ready",
            "merge: ready -> running",
            "merge: running -> succeeded",
            "run ended: complete",
        ]);
    }

    [Test]
    public async Task Tasks_with_no_dependency_between_them_all_become_ready_then_start_in_declaration_order()
    {
        var builder = new GraphBuilder();
        builder.Task("shard-1", _ => Task.CompletedTask);
        builder.Task("shard-2", _ => Task.CompletedTask);
        builder.Task("shard-3", _ => Task.CompletedTask);
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(
            builder.Build(),
            new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        result.Outcome.ShouldBe(RunOutcome.Complete);
        reporter.Lines.Take(7).ShouldBe(
        [
            "run started",
            "shard-1: pending -> ready",
            "shard-2: pending -> ready",
            "shard-3: pending -> ready",
            "shard-1: ready -> running",
            "shard-2: ready -> running",
            "shard-3: ready -> running",
        ]);
        reporter.Lines.Skip(7).ShouldBe(
            [
                "shard-1: running -> succeeded",
                "shard-2: running -> succeeded",
                "shard-3: running -> succeeded",
                "run ended: complete",
            ],
            ignoreOrder: true);
        reporter.Lines[^1].ShouldBe("run ended: complete");
    }

    [Test]
    public async Task A_graph_with_no_tasks_is_complete_and_publishes_only_the_start_and_the_end()
    {
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(
            new GraphBuilder().Build(),
            new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        result.Outcome.ShouldBe(RunOutcome.Complete);
        reporter.Lines.ShouldBe(["run started", "run ended: complete"]);
    }

    [Test]
    public async Task One_built_graph_can_be_run_twice()
    {
        var bodiesRun = 0;
        var builder = new GraphBuilder();
        var prepare = builder.Task("prepare", _ => CountBody());
        builder.Task("publish", _ => CountBody()).Requires(prepare);
        var graph = builder.Build();
        var first = new RecordingReporter();
        var second = new RecordingReporter();

        var firstResult = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.None, Reporters = [first] });
        var secondResult = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.None, Reporters = [second] });

        firstResult.Outcome.ShouldBe(RunOutcome.Complete);
        secondResult.Outcome.ShouldBe(RunOutcome.Complete);
        bodiesRun.ShouldBe(4);
        second.Lines.ShouldBe(first.Lines);
        second.Lines.ShouldBe(
        [
            "run started",
            "prepare: pending -> ready",
            "prepare: ready -> running",
            "prepare: running -> succeeded",
            "publish: pending -> ready",
            "publish: ready -> running",
            "publish: running -> succeeded",
            "run ended: complete",
        ]);

        Task CountBody()
        {
            Interlocked.Increment(ref bodiesRun);
            return Task.CompletedTask;
        }
    }
}
