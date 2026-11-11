using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace Dagline.Tests;

public class PlainTextReporterTests
{
    private static readonly DateTimeOffset _runStart = new(2026, 11, 11, 4, 12, 0, TimeSpan.Zero);

    [Test]
    public async Task A_complete_run_with_no_warnings_prints_each_task_starting_and_settling_and_only_the_counts_as_its_summary()
    {
        var clock = new FakeTimeProvider(_runStart);
        var writer = new StringWriter();
        var builder = new GraphBuilder();
        builder.Task("prepare", _ => Task.CompletedTask);

        var result = await Runner.RunAsync(builder.Build(), Options(clock, new PlainTextReporter(writer, clock)));

        result.ReporterErrors.ShouldBeEmpty();
        Lines(writer).ShouldBe(
        [
            "2026-11-11T04:12:00Z run started: 1 task",
            "2026-11-11T04:12:00Z started prepare",
            "2026-11-11T04:12:00Z succeeded prepare in 0.0s",
            "2026-11-11T04:12:00Z run ended: complete in 0.0s",
            "tasks: 1 succeeded",
        ]);
    }

    private static RunOptions Options(FakeTimeProvider clock, params IReporter[] reporters)
        => new() { Journal = Journal.None, TimeProvider = clock, Reporters = reporters };

    /// <summary>What was written, one entry for each line, without the line break that ends the last one.</summary>
    private static string[] Lines(StringWriter writer)
    {
        var written = writer.ToString();
        written.ShouldEndWith(writer.NewLine);
        return written[..^writer.NewLine.Length].Split(writer.NewLine);
    }
}
