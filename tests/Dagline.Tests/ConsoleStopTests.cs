using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace Dagline.Tests;

// The presses are applied through the test hook, below the console's handlers: reading the real cancel key event and SIGTERM is
// checked by hand with the Basic sample. Each run is scripted, with one task kept running, so a press is applied when the test
// pumps the run's loop.
public class ConsoleStopTests
{
    [Test]
    public void The_first_ctrl_c_drains_the_run_and_does_not_stop_it()
    {
        var (run, _) = StartedWithATaskRunning();
        using var console = ConsolePresses.Unhooked(run.Run);

        console.PressCtrlC().ShouldBeTrue();
        run.Pump();

        run.Current.Drain.ShouldNotBeNull();
        run.Current.Stop.ShouldBeNull();
    }

    [Test]
    public async Task The_second_ctrl_c_stops_the_run_as_the_app_and_signals_its_tasks()
    {
        var (run, work) = StartedWithATaskRunning();
        using var console = ConsolePresses.Unhooked(run.Run);

        console.PressCtrlC();
        console.PressCtrlC().ShouldBeTrue();
        run.Pump();

        run.Current.Drain.ShouldNotBeNull();
        run.Current.Stop!.By.ShouldBe(StopRequester.App);
        (await work.Started).CancellationToken.IsCancellationRequested.ShouldBeTrue();
    }

    [Test]
    public void A_sigterm_stops_the_run_with_no_drain_first()
    {
        var (run, _) = StartedWithATaskRunning();
        using var console = ConsolePresses.Unhooked(run.Run);

        console.SendSigterm().ShouldBeTrue();
        run.Pump();

        run.Current.Stop!.By.ShouldBe(StopRequester.App);
        run.Current.Drain.ShouldBeNull();
    }

    [Test]
    public void A_sigterm_after_a_drain_stops_the_run()
    {
        var (run, _) = StartedWithATaskRunning();
        using var console = ConsolePresses.Unhooked(run.Run);

        console.PressCtrlC();
        console.SendSigterm().ShouldBeTrue();
        run.Pump();

        run.Current.Drain.ShouldNotBeNull();
        run.Current.Stop.ShouldNotBeNull();
    }

    [Test]
    public void Once_the_hook_has_stopped_the_run_a_ctrl_c_or_a_sigterm_asks_nothing_and_is_not_swallowed()
    {
        var (run, _) = StartedWithATaskRunning();
        using var console = ConsolePresses.Unhooked(run.Run);
        console.SendSigterm();

        console.PressCtrlC().ShouldBeFalse();
        console.SendSigterm().ShouldBeFalse();
        console.PressCtrlC().ShouldBeFalse();
        run.Pump();

        run.Current.Drain.ShouldBeNull();
    }

    [Test]
    public void A_drain_the_app_asks_for_does_not_make_the_next_ctrl_c_a_stop()
    {
        var (run, _) = StartedWithATaskRunning();
        using var console = ConsolePresses.Unhooked(run.Run);

        run.Run.Drain();
        console.PressCtrlC().ShouldBeTrue();
        run.Pump();

        run.Current.Drain.ShouldNotBeNull();
        run.Current.Stop.ShouldBeNull();
    }

    [Test]
    public void A_stop_the_app_asks_for_leaves_the_next_ctrl_c_swallowed()
    {
        var (run, _) = StartedWithATaskRunning();
        using var console = ConsolePresses.Unhooked(run.Run);

        run.Run.Stop();
        run.Pump();

        console.PressCtrlC().ShouldBeTrue();
    }

    [Test]
    public void Once_disposed_a_ctrl_c_or_a_sigterm_asks_nothing_and_is_not_swallowed()
    {
        var (run, _) = StartedWithATaskRunning();
        var console = ConsolePresses.Unhooked(run.Run);
        console.Dispose();

        console.PressCtrlC().ShouldBeFalse();
        console.SendSigterm().ShouldBeFalse();
        run.Pump();

        run.Current.Drain.ShouldBeNull();
        run.Current.Stop.ShouldBeNull();
    }

    [Test]
    public void Hooking_no_run_throws()
    {
        Should.Throw<ArgumentNullException>(() => ConsoleStop.Hook(null!)).ParamName.ShouldBe("run");
    }

    [Test]
    public async Task Hooking_a_run_changes_nothing_about_it_and_disposing_twice_is_harmless()
    {
        var builder = new GraphBuilder();
        builder.Task("work", _ => Task.CompletedTask);
        var run = Runner.Start(builder.Build(), new RunOptions { Journal = Journal.None });

        var console = ConsoleStop.Hook(run);
        var result = await run.Result;
        console.Dispose();
        console.Dispose();

        result.Outcome.ShouldBe(RunOutcome.Complete);
        result.Snapshot.Drain.ShouldBeNull();
        result.Snapshot.Stop.ShouldBeNull();
    }

    private static (ScriptedDriver Run, PausedBody Work) StartedWithATaskRunning()
    {
        var work = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("work", work.Body);
        var run = ScriptedDriver.Start(
            builder.Build(),
            new RunOptions { Journal = Journal.None, TimeProvider = new FakeTimeProvider(), GracePeriod = TimeSpan.FromMinutes(1) });
        run.Pump();
        return (run, work);
    }
}
