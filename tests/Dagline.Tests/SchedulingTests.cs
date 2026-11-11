namespace Dagline.Tests;

public class SchedulingTests
{
    [Test]
    public async Task A_body_that_blocks_before_its_first_await_does_not_stop_other_tasks_from_starting()
    {
        using var publishStarted = new ManualResetEventSlim();
        var sawPublishStart = false;
        var builder = new GraphBuilder();
        builder.Task("prepare", _ =>
        {
            // Not a sleep: the wait ends as soon as the other body starts, and its time bound only fails a stuck test.
            sawPublishStart = publishStarted.Wait(TimeSpan.FromSeconds(30));
            return Task.CompletedTask;
        });
        builder.Task("publish", _ =>
        {
            publishStarted.Set();
            return Task.CompletedTask;
        });

        var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None });

        result.Outcome.ShouldBe(RunOutcome.Complete);
        sawPublishStart.ShouldBeTrue();
    }
}
