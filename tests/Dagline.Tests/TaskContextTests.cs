using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace Dagline.Tests;

public class TaskContextTests
{
    [Test]
    public async Task A_task_body_receives_its_path_and_the_clock_of_the_run()
    {
        var clock = new FakeTimeProvider();
        var prepare = new PausedBody();
        var publish = new PausedBody();
        var builder = new GraphBuilder();
        var prepareTask = builder.Task("prepare", prepare.Body);
        builder.Task("publish", publish.Body).Requires(prepareTask);

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, TimeProvider = clock });
        var prepareContext = await prepare.Started;
        prepare.Release();
        var publishContext = await publish.Started;
        publish.Release();
        await run;

        prepareContext.Path.ShouldBe(NodePath.Parse("prepare"));
        prepareContext.TimeProvider.ShouldBeSameAs(clock);
        publishContext.Path.ShouldBe(NodePath.Parse("publish"));
        publishContext.TimeProvider.ShouldBeSameAs(clock);
    }

    [Test]
    public async Task A_delay_a_task_takes_on_the_clock_of_the_run_ends_when_the_test_advances_that_clock()
    {
        var clock = new FakeTimeProvider();
        var builder = new GraphBuilder();
        builder.Task("prepare", context => Task.Delay(TimeSpan.FromMinutes(1), context.TimeProvider));

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, TimeProvider = clock });
        await clock.AdvanceUntilAsync(run, TimeSpan.FromSeconds(10));
        var result = await run;

        result.Outcome.ShouldBe(RunOutcome.Complete);
        var prepare = result.Snapshot.Nodes[0];
        (prepare.Settled!.Value - prepare.Started!.Value).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMinutes(1));
    }
}
