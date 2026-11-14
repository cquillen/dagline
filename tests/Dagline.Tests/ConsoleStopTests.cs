namespace Dagline.Tests;

// Neither a key press nor a SIGTERM can be made in a test: both are checked by hand with the Basic sample.
public class ConsoleStopTests
{
    [Test]
    public void Hook_returns_a_signal_that_is_not_requested()
    {
        using var stop = ConsoleStop.Hook();

        stop.IsRequested.ShouldBeFalse();
        stop.Requests.ShouldBe(0);
    }

    [Test]
    public async Task Request_on_the_hooked_signal_stops_a_run_as_on_any_stop_signal()
    {
        var builder = new GraphBuilder();
        builder.Task("work", _ => Task.CompletedTask);
        using var stop = ConsoleStop.Hook();

        stop.Request();
        stop.Request();
        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None }, stop);

        stop.IsRequested.ShouldBeTrue();
        stop.Requests.ShouldBe(2);
        result.Outcome.ShouldBe(RunOutcome.Stopped);
        result.Snapshot.NeverReached.Count.ShouldBe(1);
    }

    [Test]
    public void Disposing_twice_is_harmless_and_leaves_the_signal_usable()
    {
        var stop = ConsoleStop.Hook();

        stop.Dispose();
        stop.Dispose();
        stop.Request();

        stop.Requests.ShouldBe(1);
    }
}
