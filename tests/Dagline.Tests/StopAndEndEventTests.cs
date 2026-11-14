using Dagline.TestSupport;

namespace Dagline.Tests;

public class StopAndEndEventTests
{
    [Test]
    public async Task A_stop_requested_through_the_apps_signal_says_the_app_asked()
    {
        var reporter = new RecordingReporter();
        var stop = new StopSignal();
        stop.Request();
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask);

        await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] }, stop);

        reporter.Events.OfType<StopRequested>().ShouldHaveSingleItem().By.ShouldBe(StopRequester.App);
    }

    [Test]
    public async Task An_ordinary_run_ends_with_its_run_outcome_and_no_error()
    {
        var reporter = new RecordingReporter();
        var builder = new GraphBuilder();
        builder.Task("load", _ => throw new InvalidOperationException("load failed"));

        await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        var ended = reporter.Events[^1].ShouldBeOfType<RunEnded>();
        ended.Outcome.ShouldBe(RunOutcome.Incomplete);
        ended.Error.ShouldBeNull();
    }
}
