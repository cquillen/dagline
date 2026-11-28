using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace Dagline.Tests;

/// <summary>
/// What the plain-text reporter prints of a run on the new engine, where a report is dated by when the run applies it (ADR 0015).
/// Each test is one scripted run, which covers on the new engine a plain-text test that stays on the existing one.
/// </summary>
public class LoopPlainTextReporterTests
{
    private static readonly DateTimeOffset _runStart = new(2026, 11, 11, 4, 12, 0, TimeSpan.Zero);

    [Test]
    public async Task A_wait_with_no_text_prints_the_path_alone()
    {
        var clock = new FakeTimeProvider(_runStart);
        var writer = new StringWriter();
        var waitOver = new TaskCompletionSource();
        var builder = new GraphBuilder();
        builder.Task("job", async context =>
        {
            using (context.BeginWaiting())
            {
                await waitOver.Task;
            }
        });
        var run = ScriptedDriver.Start(
            builder.Build(),
            new RunOptions { Journal = Journal.None, TimeProvider = clock, Reporters = [new PlainTextReporter(writer, clock)] });
        run.Pump();

        clock.Advance(TimeSpan.FromSeconds(3));
        waitOver.SetResult();
        await run.Finish();

        var written = writer.ToString();
        written[..^writer.NewLine.Length].Split(writer.NewLine).ShouldBe(
        [
            "2026-11-11T04:12:00Z run started: 1 task",
            "2026-11-11T04:12:00Z started job",
            "2026-11-11T04:12:00Z waiting job",
            "2026-11-11T04:12:03Z waited job for 3.0s",
            "2026-11-11T04:12:03Z succeeded job in 3.0s",
            "2026-11-11T04:12:03Z run ended: complete in 3.0s",
            "tasks: 1 succeeded",
        ]);
    }
}
