namespace Dagline.Testing.Tests;

public class InstantClockRunTests
{
    private static readonly DateTimeOffset _start = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task A_graph_whose_tasks_wait_for_hours_completes_without_real_waiting()
    {
        var clock = new InstantClock();
        var firstChecks = 0;
        var secondChecks = 0;
        var builder = new GraphBuilder();
        var first = builder.Task("first", context => context.WaitUntilAsync(
            TimeSpan.FromHours(1),
            _ => Task.FromResult(++firstChecks == 4),
            "the first export to finish"));
        var second = builder.Task("second", context => context.WaitUntilAsync(
            TimeSpan.FromHours(2),
            _ => Task.FromResult(++secondChecks == 3),
            "the second export to finish"));
        second.Requires(first);

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, TimeProvider = clock });

        result.Outcome.ShouldBe(RunOutcome.Complete);
        firstChecks.ShouldBe(4);
        secondChecks.ShouldBe(3);
        clock.GetUtcNow().ShouldBe(_start.AddHours(7));
        result.Snapshot[second.Path].Settled.ShouldBe(_start.AddHours(7));
    }

    [Test]
    public async Task A_check_that_never_returns_true_fails_its_task_once_the_guard_is_passed_and_the_run_ends()
    {
        var clock = new InstantClock(TimeSpan.FromDays(1));
        var checks = 0;
        var builder = new GraphBuilder();
        var stuck = builder.Task("stuck", context => context.WaitUntilAsync(
            TimeSpan.FromHours(1),
            _ =>
            {
                checks++;
                return Task.FromResult(false);
            }));
        var other = builder.Task("other", _ => Task.CompletedTask);

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, TimeProvider = clock });

        result.Outcome.ShouldBe(RunOutcome.Incomplete);
        result.Snapshot[stuck.Path].Status.ShouldBe(NodeStatus.Failed);
        result.Snapshot[stuck.Path].Failure.ShouldBe(FailureKind.Unexpected);
        result.Snapshot[stuck.Path].Message!.ShouldContain("guard");
        result.Snapshot[other.Path].Status.ShouldBe(NodeStatus.Succeeded);
        checks.ShouldBe(26);
    }
}
