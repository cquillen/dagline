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

    [Test]
    public void A_run_ended_event_carries_exactly_one_of_a_run_outcome_and_an_error()
    {
        var at = new DateTimeOffset(2026, 11, 11, 5, 0, 0, TimeSpan.Zero);

        Should.Throw<ArgumentException>(() => new RunEnded(at, null, null));
        Should.Throw<ArgumentException>(() => new RunEnded(at, RunOutcome.Complete, new IOException("disk full")));
        new RunEnded(at, null, new IOException("disk full")).Outcome.ShouldBeNull();
    }

    [Test]
    public void A_run_ended_event_cannot_be_given_a_second_one_by_an_initializer_or_a_with_expression()
    {
        var at = new DateTimeOffset(2026, 11, 11, 5, 0, 0, TimeSpan.Zero);

        // Set only by the constructor, so that neither member can be set apart from the other.
        typeof(RunEnded).GetProperty(nameof(RunEnded.Outcome))!.SetMethod.ShouldBeNull();
        typeof(RunEnded).GetProperty(nameof(RunEnded.Error))!.SetMethod.ShouldBeNull();
        var copied = new RunEnded(at, RunOutcome.Complete) with { At = at.AddSeconds(1) };
        copied.Outcome.ShouldBe(RunOutcome.Complete);
        copied.Error.ShouldBeNull();
    }
}
