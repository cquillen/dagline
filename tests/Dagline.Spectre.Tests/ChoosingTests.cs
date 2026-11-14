using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Spectre.Console;
using Spectre.Console.Testing;

namespace Dagline.Spectre.Tests;

public class ChoosingTests
{
    [Test]
    public async Task Never_gives_plain_text_on_the_writer_and_draws_nothing_on_the_console()
    {
        var builder = new GraphBuilder();
        builder.Task("work", _ => Task.CompletedTask);
        using var console = new TestConsole().Size(new Size(80, 24)).EmitAnsiSequences();
        using var writer = new StringWriter();
        var reporter = SpectreReporter.Create(new SpectreOptions { PlainText = writer, Display = DisplayMode.Never, Console = console });

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        result.ReporterErrors.ShouldBeEmpty();
        console.Output.ShouldBeEmpty();
        writer.ToString().ShouldContain("run started");
        writer.ToString().ShouldContain("run ended");
    }

    [Test]
    public async Task Force_draws_the_display_on_a_console_that_is_not_a_terminal()
    {
        var builder = new GraphBuilder();
        builder.Task("work", _ => Task.CompletedTask);
        using var console = new TestConsole().Size(new Size(80, 24));
        using var writer = new StringWriter();
        var reporter = SpectreReporter.Create(new SpectreOptions { PlainText = writer, Display = DisplayMode.Force, Console = console });

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        result.ReporterErrors.ShouldBeEmpty();
        console.Profile.Out.IsTerminal.ShouldBeFalse();
        console.Output.ShouldStartWith("Dagline");
        console.Output.ShouldContain("Run complete");
        writer.ToString().ShouldBeEmpty();
    }

    [Test]
    public async Task Auto_over_a_console_that_does_not_accept_ansi_sequences_gives_plain_text()
    {
        var builder = new GraphBuilder();
        builder.Task("work", _ => Task.CompletedTask);
        using var console = new TestConsole().Size(new Size(80, 24));
        console.Profile.Capabilities.Ansi = false;
        using var writer = new StringWriter();
        var reporter = SpectreReporter.Create(new SpectreOptions { PlainText = writer, Console = console });

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        result.ReporterErrors.ShouldBeEmpty();
        console.Output.ShouldBeEmpty();
        writer.ToString().ShouldContain("run started");
    }

    [Test]
    public async Task The_choice_is_made_once_when_the_reporter_handles_run_started()
    {
        var work = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("work", work.Body);
        using var console = new TestConsole().Size(new Size(80, 24)).EmitAnsiSequences();
        console.Profile.Capabilities.Ansi = true;
        using var writer = new StringWriter();
        var reporter = new WatchedReporter(SpectreReporter.Create(new SpectreOptions { PlainText = writer, Console = console }));

        // Only what the console accepts when the run starts counts, not what it accepted when the reporter was made.
        console.Profile.Capabilities.Ansi = false;
        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        await reporter.Handled("work: ready -> running");
        console.Profile.Capabilities.Ansi = true;
        work.Release();
        var result = await run;

        result.ReporterErrors.ShouldBeEmpty();
        console.Output.ShouldBeEmpty();
        writer.ToString().ShouldContain("run started");
        writer.ToString().ShouldContain("run ended");
    }

    [Test]
    public async Task The_plain_text_reporter_uses_the_options_clock()
    {
        var work = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("work", work.Body);
        var clock = new FakeTimeProvider(DisplayRun.RunStart);
        using var writer = new StringWriter();
        var reporter = new WatchedReporter(
            SpectreReporter.Create(new SpectreOptions { PlainText = writer, Display = DisplayMode.Never, TimeProvider = clock }));

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, TimeProvider = clock, Reporters = [reporter] });
        await reporter.Handled("work: ready -> running");
        clock.Advance(TimeSpan.FromMinutes(1));
        var printed = writer.ToString();
        work.Release();
        await run;

        printed.ShouldContain("2026-11-11T04:13:00Z tasks: 1 running");
    }
}
