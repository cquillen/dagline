using Dagline.TestSupport;

namespace Dagline.Tests;

public class LimitTests
{
    [Test]
    public async Task A_ready_task_whose_limit_is_full_is_passed_over_and_a_task_behind_it_starts()
    {
        var firstLoad = new PausedBody();
        var secondLoad = new PausedBody();
        var publish = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("load-1", firstLoad.Body).Limit("database");
        var held = builder.Task("load-2", secondLoad.Body).Limit("database");
        builder.Task("publish", publish.Body);
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(
            builder.Build(),
            new RunOptions
            {
                Journal = Journal.None,
                Limits = new Dictionary<string, int> { ["database"] = 1 },
                Reporters = [reporter],
            });
        var source = await reporter.Source;
        await firstLoad.Started;
        await publish.Started;
        var limitFull = source.Current;
        firstLoad.Release();
        await secondLoad.Started;
        secondLoad.Release();
        publish.Release();
        var result = await run;

        result.Outcome.ShouldBe(RunOutcome.Complete);
        limitFull[held.Path].Status.ShouldBe(NodeStatus.Ready);
        reporter.Lines.Take(8).ShouldBe(
        [
            "run started",
            "load-1: pending -> ready",
            "load-2: pending -> ready",
            "publish: pending -> ready",
            "load-1: ready -> running",
            "publish: ready -> running",
            "load-1: running -> succeeded",
            "load-2: ready -> running",
        ]);
    }
}
