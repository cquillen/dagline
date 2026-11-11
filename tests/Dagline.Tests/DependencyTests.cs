using Dagline.TestSupport;

namespace Dagline.Tests;

public class DependencyTests
{
    [Test]
    public async Task An_after_task_runs_once_its_upstream_has_settled()
    {
        var load = new PausedBody();
        var builder = new GraphBuilder();
        var loadTask = builder.Task("load", load.Body);
        var report = builder.Task("report", _ => Task.CompletedTask).After(loadTask);
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        var source = await reporter.Source;
        await load.Started;
        var midRun = source.Current;
        load.Release();
        var result = await run;

        midRun[report.Path].Status.ShouldBe(NodeStatus.Pending);
        result.Outcome.ShouldBe(RunOutcome.Complete);
        reporter.Lines.ShouldBe(
        [
            "run started",
            "load: pending -> ready",
            "load: ready -> running",
            "load: running -> succeeded",
            "report: pending -> ready",
            "report: ready -> running",
            "report: running -> succeeded",
            "run ended: complete",
        ]);
        result.Snapshot[report.Path].Dependencies.ShouldBe([new Dependency(loadTask.Path, DependencyKind.After)]);
    }
}
