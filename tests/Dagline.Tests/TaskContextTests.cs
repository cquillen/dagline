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

    [Test]
    public async Task A_context_called_from_several_threads_at_once_loses_no_report()
    {
        const int repeats = 50;
        const int threads = 4;
        const int reportsOfEachThread = 25;

        for (var repeat = 0; repeat < repeats; repeat++)
        {
            var builder = new GraphBuilder();
            var load = builder.Task("load", async context =>
            {
                var together = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var reporting = Enumerable.Range(0, threads).Select(thread => Task.Run(async () =>
                {
                    await together.Task;
                    for (var report = 0; report < reportsOfEachThread; report++)
                    {
                        context.Warn($"thread {thread} warning {report}");
                        context.Detail($"thread {thread} line {report}");
                        context.Count(report);
                    }
                })).ToList();
                together.SetResult();
                await Task.WhenAll(reporting);
            });
            var reporter = new RecordingReporter();

            var result = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });

            var reported = result.Snapshot[load.Path];
            reported.Status.ShouldBe(NodeStatus.Succeeded);
            reported.Warnings.ShouldBe(EveryReport("warning"), ignoreOrder: true);
            reported.WarningsOmitted.ShouldBe(0);
            reported.Detail.ShouldBe(EveryReport("line"), ignoreOrder: true);
            reported.DetailOmitted.ShouldBe(0);
            reported.Count.ShouldBe(reportsOfEachThread - 1);
            reporter.Events.OfType<WarningAdded>().Select(added => added.Warning).ShouldBe(reported.Warnings);
        }

        static IEnumerable<string> EveryReport(string kind)
            => from thread in Enumerable.Range(0, threads)
               from report in Enumerable.Range(0, reportsOfEachThread)
               select $"thread {thread} {kind} {report}";
    }

    [Test]
    public async Task Once_its_task_has_settled_a_context_changes_nothing_and_still_answers_StatusOf()
    {
        TaskContext? loadContext = null;
        var verify = new PausedBody();
        var builder = new GraphBuilder();
        var prepare = builder.Task("prepare", _ => Task.CompletedTask);
        var load = builder.Task("load", context =>
        {
            loadContext = context;
            context.Status("loading entities");
            context.Count(40, 40);
            context.Message("loaded 40 entities");
            context.Detail("tenant-a: 25 entities");
            context.Warn("entity 9 has no region");
            return Task.CompletedTask;
        }).Requires(prepare);
        builder.Task("verify", verify.Body).After(load);
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        await verify.Started;
        loadContext.ShouldNotBeNull();
        loadContext.Status("still loading");
        loadContext.Count(1, 2);
        loadContext.Warn("a late warning");
        loadContext.Message("a late message");
        loadContext.Detail("a late line");
        var late = loadContext.Fail("a late failure", "a late detail line");
        var upstream = loadContext.StatusOf(prepare);
        verify.Release();
        var result = await run;

        result.Outcome.ShouldBe(RunOutcome.Complete);
        var settled = result.Snapshot[load.Path];
        settled.Status.ShouldBe(NodeStatus.Succeeded);
        settled.Failure.ShouldBeNull();
        settled.StatusText.ShouldBe("loading entities");
        settled.Count.ShouldBe(40);
        settled.Total.ShouldBe(40);
        settled.Message.ShouldBe("loaded 40 entities");
        settled.Detail.ShouldBe(["tenant-a: 25 entities"]);
        settled.Warnings.ShouldBe(["entity 9 has no region"]);
        settled.WarningsOmitted.ShouldBe(0);
        reporter.Events.OfType<WarningAdded>().ShouldHaveSingleItem().Warning.ShouldBe("entity 9 has no region");
        late.Message.ShouldBe("a late failure");
        late.Detail.ShouldBe(["a late detail line"]);
        upstream.ShouldBe(NodeStatus.Succeeded);
    }
}
