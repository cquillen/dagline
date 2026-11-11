using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace Dagline.Tests;

public class WaitingTests
{
    private static readonly DateTimeOffset _runStart = new(2026, 11, 11, 4, 12, 0, TimeSpan.Zero);

    [Test]
    public async Task Opening_a_scope_begins_the_wait_and_closing_it_ends_the_wait_with_the_same_text_and_start_time()
    {
        var clock = new FakeTimeProvider(_runStart);
        var trigger = new PausedBody();
        var poll = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("job", async context =>
        {
            await trigger.Body(context);
            using (context.Waiting("job 17 to finish"))
            {
                await poll.Body(context);
            }
        });
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, TimeProvider = clock, Reporters = [reporter] });
        await trigger.Started;
        clock.Advance(TimeSpan.FromSeconds(2));
        trigger.Release();
        await poll.Started;
        clock.Advance(TimeSpan.FromSeconds(98));
        poll.Release();
        await run;

        var waitBegan = _runStart.AddSeconds(2);
        var waitEnded = _runStart.AddSeconds(100);
        reporter.Lines.ShouldBe(
        [
            "run started",
            "job: pending -> ready",
            "job: ready -> running",
            "waiting job: job 17 to finish",
            "waited job",
            "job: running -> succeeded",
            "run ended: complete",
        ]);
        var waits = reporter.Events.OfType<WaitingChanged>().ToList();
        waits[0].ShouldBe(new WaitingChanged(waitBegan, NodePath.Parse("job"), Waiting: true, "job 17 to finish", Since: waitBegan));
        waits[1].ShouldBe(new WaitingChanged(waitEnded, NodePath.Parse("job"), Waiting: false, "job 17 to finish", Since: waitBegan));
    }
}
