using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Spectre.Console;
using Spectre.Console.Testing;

namespace Dagline.Spectre.Tests;

public class DrawingFailureTests
{
    private const string _failureLine = "display failed, output continues as plain text: IOException: the console is gone";

    [Test]
    public async Task A_console_that_throws_part_of_the_way_through_a_run_leaves_the_rest_of_it_to_plain_text()
    {
        var work = new PausedBody();
        var later = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("work", work.Body);
        builder.Task("later", later.Body).Requires(builder.Task("first", _ => Task.CompletedTask));
        var clock = new FakeTimeProvider(DisplayRun.RunStart);
        using var console = new ScriptedConsole(new TestConsole().Size(new Size(80, 24)).EmitAnsiSequences());
        using var writer = new StringWriter();
        var reporter = new WatchedReporter(
            SpectreReporter.Create(new SpectreOptions { PlainText = writer, Display = DisplayMode.Force, Console = console, TimeProvider = clock }));

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, TimeProvider = clock, Reporters = [reporter] });
        await work.Started;
        await later.Started;
        await reporter.Handled("later: ready -> running");
        console.FailNextWrite();
        clock.Advance(DisplayRun.RedrawInterval);
        var screen = Screen.Read(console.Inner.Output, new Size(80, 24));
        var printedAtFailure = Lines(writer);
        work.Release();
        later.Release();
        var result = await run;
        var printed = Lines(writer);

        screen.OnAlternateScreen.ShouldBeFalse();
        screen.CursorVisible.ShouldBeTrue();
        screen.Modes.ShouldBe(["?1049h", "?25l", "?1049l", "?25h"]);
        printedAtFailure[0].ShouldBe(_failureLine);
        printedAtFailure[1].ShouldContain("run started");
        printedAtFailure.Count.ShouldBe(2);
        printed.ShouldContain(line => line.Contains("succeeded later", StringComparison.Ordinal));
        printed.ShouldContain(line => line.Contains("run ended", StringComparison.Ordinal));
        printed.ShouldNotContain(line => line.Contains("first", StringComparison.Ordinal));
        result.Outcome.ShouldBe(RunOutcome.Complete);
        result.ReporterErrors.ShouldBeEmpty();
        Screen.Read(console.Inner.Output, new Size(80, 24)).Ordinary.ShouldBeEmpty();
    }

    [Test]
    public async Task A_console_that_throws_while_an_event_is_handled_hands_that_event_to_plain_text()
    {
        var builder = new GraphBuilder();
        builder.Task("work", _ => Task.CompletedTask);
        using var console = new ScriptedConsole(new TestConsole().Size(new Size(80, 24)).EmitAnsiSequences());
        using var writer = new StringWriter();
        console.FailNextWrite();
        var reporter = SpectreReporter.Create(new SpectreOptions { PlainText = writer, Display = DisplayMode.Force, Console = console });

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        var printed = Lines(writer);

        printed[0].ShouldBe(_failureLine);
        printed[1].ShouldContain("run started");
        printed.ShouldContain(line => line.Contains("run ended", StringComparison.Ordinal));
        result.ReporterErrors.ShouldBeEmpty();
        var screen = Screen.Read(console.Inner.Output, new Size(80, 24));
        screen.OnAlternateScreen.ShouldBeFalse();
        screen.CursorVisible.ShouldBeTrue();
    }

    [Test]
    public async Task A_plain_text_reporter_that_then_throws_is_dropped_like_any_reporter()
    {
        var builder = new GraphBuilder();
        builder.Task("work", _ => Task.CompletedTask);
        using var console = new ScriptedConsole(new TestConsole().Size(new Size(80, 24)).EmitAnsiSequences());
        using var writer = new ThrowingWriter();
        console.FailNextWrite();
        var reporter = SpectreReporter.Create(new SpectreOptions { PlainText = writer, Display = DisplayMode.Force, Console = console });

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        result.Outcome.ShouldBe(RunOutcome.Complete);
        var error = result.ReporterErrors.ShouldHaveSingleItem();
        error.Reporter.ShouldBeSameAs(reporter);
        error.Exception.ShouldBeOfType<IOException>().Message.ShouldBe("the writer is gone");
    }

    [Test]
    public async Task A_stop_that_ends_the_wait_for_the_display_restores_the_terminal()
    {
        for (var repeat = 0; repeat < 50; repeat++)
        {
            var builder = new GraphBuilder();
            builder.Task("work", _ => Task.CompletedTask);
            using var console = new ScriptedConsole(new TestConsole().Size(new Size(80, 24)).EmitAnsiSequences());
            var display = SpectreReporter.Create(new SpectreOptions
            {
                PlainText = TextWriter.Null,
                Display = DisplayMode.Force,
                Console = console,
                TimeProvider = new FakeTimeProvider(DisplayRun.RunStart),
            });
            var stuck = new WatchedReporter(new StuckBefore("run ended: complete", display));
            var watching = new RecordingReporter();
            var stop = new StopSignal();

            var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [stuck, watching] }, stop);
            await watching.Ended;
            await stuck.Handled("work: running -> succeeded");
            var restored = console.NextWrite();
            stop.Request();
            var result = await run;
            await restored.WaitAsync(TimeSpan.FromSeconds(30));
            var screen = Screen.Read(console.Inner.Output, new Size(80, 24));

            result.ReporterErrors.ShouldHaveSingleItem().Exception.ShouldBeAssignableTo<OperationCanceledException>();
            screen.OnAlternateScreen.ShouldBeFalse();
            screen.CursorVisible.ShouldBeTrue();
            screen.Modes.ShouldBe(["?1049h", "?25l", "?1049l", "?25h"]);
        }
    }
    private static List<string> Lines(StringWriter writer)
        => [.. writer.ToString().Split(writer.NewLine, StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>
    /// Hands every event to another reporter, until the event whose line is <paramref name="stuckOn"/>: that call waits until its
    /// token is cancelled, as a reporter stuck at the end of a run would.
    /// </summary>
    private sealed class StuckBefore(string stuckOn, IReporter inner) : IReporter
    {
        public async ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken)
        {
            if (EventLine.Describe(runEvent) == stuckOn)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            await inner.OnEventAsync(runEvent, snapshot, cancellationToken);
        }
    }

    private sealed class ThrowingWriter : StringWriter
    {
        public override void Write(string? value) => throw new IOException("the writer is gone");

        public override void WriteLine(string? value) => throw new IOException("the writer is gone");
    }
}
