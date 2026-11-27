using Dagline.TestSupport;

namespace Dagline.Tests;

// The presses are applied through the test hook, below the console's handlers: reading the real cancel key event and SIGTERM is
// checked by hand with the Basic sample.
public class ConsoleStopTests
{
    [Test]
    public void The_first_ctrl_c_requests_the_drain_and_leaves_the_token_alone()
    {
        using var console = ConsolePresses.Unhooked();

        console.PressCtrlC().ShouldBeTrue();

        console.Drain.IsRequested.ShouldBeTrue();
        console.Token.IsCancellationRequested.ShouldBeFalse();
    }

    [Test]
    public void The_second_ctrl_c_cancels_the_token()
    {
        using var console = ConsolePresses.Unhooked();

        console.PressCtrlC();
        console.PressCtrlC().ShouldBeTrue();

        console.Token.IsCancellationRequested.ShouldBeTrue();
    }

    [Test]
    public void A_sigterm_cancels_the_token_with_no_drain_first()
    {
        using var console = ConsolePresses.Unhooked();

        console.SendSigterm().ShouldBeTrue();

        console.Token.IsCancellationRequested.ShouldBeTrue();
        console.Drain.IsRequested.ShouldBeFalse();
    }

    [Test]
    public void A_sigterm_after_a_drain_cancels_the_token()
    {
        using var console = ConsolePresses.Unhooked();

        console.PressCtrlC();
        console.SendSigterm().ShouldBeTrue();

        console.Token.IsCancellationRequested.ShouldBeTrue();
    }

    [Test]
    public void Once_the_token_is_cancelled_a_ctrl_c_or_a_sigterm_requests_nothing_and_is_not_swallowed()
    {
        using var console = ConsolePresses.Unhooked();
        console.SendSigterm();

        console.PressCtrlC().ShouldBeFalse();
        console.SendSigterm().ShouldBeFalse();
        console.PressCtrlC().ShouldBeFalse();

        console.Drain.IsRequested.ShouldBeFalse();
    }

    [Test]
    public void A_drain_the_app_requests_does_not_make_the_next_ctrl_c_a_stop()
    {
        using var console = ConsolePresses.Unhooked();

        console.Drain.Request();
        console.PressCtrlC().ShouldBeTrue();

        console.Token.IsCancellationRequested.ShouldBeFalse();
    }

    [Test]
    public void Once_disposed_a_ctrl_c_or_a_sigterm_requests_nothing_and_is_not_swallowed()
    {
        var console = ConsolePresses.Unhooked();
        console.Dispose();

        console.PressCtrlC().ShouldBeFalse();
        console.SendSigterm().ShouldBeFalse();

        console.Drain.IsRequested.ShouldBeFalse();
        console.Token.IsCancellationRequested.ShouldBeFalse();
    }

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
