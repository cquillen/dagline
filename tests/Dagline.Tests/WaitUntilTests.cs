using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace Dagline.Tests;

public class WaitUntilTests
{
    private static readonly DateTimeOffset _runStart = new(2026, 11, 11, 4, 12, 0, TimeSpan.Zero);

    [Test]
    public async Task The_helper_checks_at_once_and_returns_without_a_delay_when_the_check_is_true()
    {
        var clock = new FakeTimeProvider(_runStart);
        var checks = 0;
        var builder = new GraphBuilder();
        var jobTask = builder.Task("job", context => context.WaitUntil(
            TimeSpan.FromSeconds(30),
            _ =>
            {
                checks++;
                return Task.FromResult(true);
            },
            "job 17 to finish"));
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, TimeProvider = clock, Reporters = [reporter] });

        checks.ShouldBe(1);
        result.Snapshot[jobTask.Path].Status.ShouldBe(NodeStatus.Succeeded);
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
        reporter.Events.OfType<WaitingChanged>().Last()
            .ShouldBe(new WaitingChanged(_runStart, jobTask.Path, Waiting: false, "job 17 to finish", Since: _runStart));
    }

    [Test]
    public async Task Checks_are_one_interval_apart_from_the_end_of_one_check_to_the_start_of_the_next()
    {
        var fake = new FakeTimeProvider(_runStart);
        var clock = new WatchedClock(fake);
        var interval = TimeSpan.FromSeconds(30);
        var checkTakes = TimeSpan.FromSeconds(4);
        var starts = new List<DateTimeOffset>();
        var ends = new List<DateTimeOffset>();
        var builder = new GraphBuilder();
        builder.Task("job", context => context.WaitUntil(
            interval,
            _ =>
            {
                starts.Add(fake.GetUtcNow());
                fake.Advance(checkTakes);
                ends.Add(fake.GetUtcNow());
                return Task.FromResult(starts.Count == 3);
            }));
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, TimeProvider = clock, Reporters = [reporter] });
        for (var delay = 0; delay < 2; delay++)
        {
            (await clock.NextTimerAsync()).ShouldBe(interval);
            fake.Advance(interval);
        }

        var result = await run;

        result.Outcome.ShouldBe(RunOutcome.Complete);
        starts.ShouldBe([_runStart, _runStart.AddSeconds(34), _runStart.AddSeconds(68)]);
        ends.ShouldBe([_runStart.AddSeconds(4), _runStart.AddSeconds(38), _runStart.AddSeconds(72)]);
        reporter.Events.OfType<WaitingChanged>().Last()
            .ShouldBe(new WaitingChanged(_runStart.AddSeconds(72), NodePath.Parse("job"), Waiting: false, null, Since: _runStart));
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task An_interval_of_zero_or_less_is_refused_at_the_call(int seconds)
    {
        var checks = 0;
        Exception? refused = null;
        var builder = new GraphBuilder();
        var jobTask = builder.Task("job", context =>
        {
            refused = Should.Throw<ArgumentOutOfRangeException>(() => context.WaitUntil(
                TimeSpan.FromSeconds(seconds),
                _ =>
                {
                    checks++;
                    return Task.FromResult(true);
                },
                "job 17 to finish"));
            return Task.CompletedTask;
        });
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });

        refused.ShouldBeOfType<ArgumentOutOfRangeException>().ParamName.ShouldBe("interval");
        checks.ShouldBe(0);
        reporter.Events.OfType<WaitingChanged>().ShouldBeEmpty();
        result.Snapshot[jobTask.Path].Status.ShouldBe(NodeStatus.Succeeded);
    }
}
