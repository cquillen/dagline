namespace Dagline.Tests;

// Neither a key press nor a SIGTERM can be made in a test: both are checked by hand with the Basic sample.
public class ConsoleStopTests
{
    [Test]
    public void Hook_returns_a_drain_that_is_not_requested_and_a_token_that_is_not_cancelled()
    {
        using var console = ConsoleStop.Hook();

        console.Drain.IsRequested.ShouldBeFalse();
        console.Token.CanBeCanceled.ShouldBeTrue();
        console.Token.IsCancellationRequested.ShouldBeFalse();
    }

    [Test]
    public async Task Its_drain_requested_from_code_drains_a_run_as_any_drain_signal_does()
    {
        var builder = new GraphBuilder();
        builder.Task("work", _ => Task.CompletedTask);
        using var console = ConsoleStop.Hook();

        console.Drain.Request();
        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None }, console.Drain, console.Token);

        result.Outcome.ShouldBe(RunOutcome.Stopped);
        result.Snapshot.Drain.ShouldNotBeNull();
        result.Snapshot.Stop.ShouldBeNull();
        result.Snapshot.NeverReached.Count.ShouldBe(1);
    }

    [Test]
    public void Disposing_twice_is_harmless_and_leaves_the_drain_and_the_token_usable()
    {
        var console = ConsoleStop.Hook();

        console.Dispose();
        console.Dispose();
        console.Drain.Request();

        console.Drain.IsRequested.ShouldBeTrue();
        console.Token.IsCancellationRequested.ShouldBeFalse();
    }
}
