using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace Dagline.Tests;

public class RunTimeTests
{
    private static readonly DateTimeOffset _runStart = new(2026, 11, 11, 4, 12, 0, TimeSpan.Zero);

    [Test]
    public async Task Event_times_task_times_and_the_start_and_end_of_the_run_come_from_the_clock_in_the_options()
    {
        var clock = new FakeTimeProvider(_runStart);
        var prepare = new PausedBody();
        var publish = new PausedBody();
        var builder = new GraphBuilder();
        var prepareTask = builder.Task("prepare", prepare.Body);
        builder.Task("publish", publish.Body).Requires(prepareTask);
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(
            builder.Build(),
            new RunOptions { Journal = Journal.None, TimeProvider = clock, Reporters = [reporter] });
        await prepare.Started;
        clock.Advance(TimeSpan.FromSeconds(5));
        prepare.Release();
        await publish.Started;
        clock.Advance(TimeSpan.FromSeconds(3));
        publish.Release();
        var result = await run;

        var prepareSettled = _runStart.AddSeconds(5);
        var runEnd = _runStart.AddSeconds(8);
        reporter.Events.Select(runEvent => (EventLine.Describe(runEvent), runEvent.At)).ShouldBe(
        [
            ("run started", _runStart),
            ("prepare: pending -> ready", _runStart),
            ("prepare: ready -> running", _runStart),
            ("prepare: running -> succeeded", prepareSettled),
            ("publish: pending -> ready", prepareSettled),
            ("publish: ready -> running", prepareSettled),
            ("publish: running -> succeeded", runEnd),
            ("run ended: complete", runEnd),
        ]);

        result.Snapshot.Started.ShouldBe(_runStart);
        result.Snapshot.Ended.ShouldBe(runEnd);
        result.Snapshot.Nodes[0].Started.ShouldBe(_runStart);
        result.Snapshot.Nodes[0].Settled.ShouldBe(prepareSettled);
        result.Snapshot.Nodes[1].Started.ShouldBe(prepareSettled);
        result.Snapshot.Nodes[1].Settled.ShouldBe(runEnd);
    }
}
