using Dagline.TestSupport;

namespace Dagline.Tests;

public class TaskCountsTests
{
    [Test]
    public async Task The_counts_of_the_run_of_nested_groups_and_of_a_task_give_their_sums_and_sub_counts()
    {
        var loading = new PausedBody();
        var polling = new PausedBody();
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1");
        var tenant = shard.Group("tenant-a");
        var check = shard.Task("check", context => throw context.Fail("bad data"));
        var load = tenant.Task("load", loading.Body).After(check);
        var poll = tenant.Task("poll", async context =>
        {
            using (context.Waiting("job 17"))
            {
                await polling.Body(context);
            }
        }).After(check);
        var verify = shard.Task("verify", _ => Task.CompletedTask).Requires(check);
        var publish = builder.Task("publish", _ => Task.CompletedTask).Requires(shard);
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        var source = await reporter.Source;
        await Task.WhenAll(loading.Started, polling.Started);
        var midway = source.Current;
        loading.Release();
        polling.Release();
        var result = await run;

        var runCounts = midway.Counts;
        runCounts[NodeStatus.Running].ShouldBe(2);
        runCounts[NodeStatus.Failed].ShouldBe(1);
        runCounts[NodeStatus.Blocked].ShouldBe(2);
        runCounts[NodeStatus.Pending].ShouldBe(0);
        runCounts.Total.ShouldBe(5);
        runCounts.Settled.ShouldBe(3);
        runCounts.Unsucceeded.ShouldBe(5);
        runCounts.EarlierSuccesses.ShouldBe(0);
        runCounts.Waiting.ShouldBe(1);
        runCounts.Working.ShouldBe(1);

        var shardCounts = midway[shard.Path].Counts;
        shardCounts.Total.ShouldBe(4);
        shardCounts.Settled.ShouldBe(2);
        shardCounts.Waiting.ShouldBe(1);
        shardCounts.Working.ShouldBe(1);
        var tenantCounts = midway[tenant.Path].Counts;
        tenantCounts.Total.ShouldBe(2);
        tenantCounts.Settled.ShouldBe(0);
        tenantCounts.Unsucceeded.ShouldBe(2);
        tenantCounts.Waiting.ShouldBe(1);
        tenantCounts.Working.ShouldBe(1);

        var pollCounts = midway[poll.Path].Counts;
        pollCounts[NodeStatus.Running].ShouldBe(1);
        pollCounts.Total.ShouldBe(1);
        pollCounts.Waiting.ShouldBe(1);
        pollCounts.Working.ShouldBe(0);
        var loadCounts = midway[load.Path].Counts;
        loadCounts.Waiting.ShouldBe(0);
        loadCounts.Working.ShouldBe(1);
        var verifyCounts = midway[verify.Path].Counts;
        verifyCounts[NodeStatus.Blocked].ShouldBe(1);
        verifyCounts.Total.ShouldBe(1);
        verifyCounts.Settled.ShouldBe(1);
        verifyCounts.Unsucceeded.ShouldBe(1);
        midway[publish.Path].Counts[NodeStatus.Blocked].ShouldBe(1);

        var settled = reporter.Events.OfType<GroupSettled>().ToList();
        settled.Select(group => group.Path).ShouldBe([tenant.Path, shard.Path]);
        settled[0].Counts[NodeStatus.Succeeded].ShouldBe(2);
        settled[0].Counts.Total.ShouldBe(2);
        settled[0].Counts.Settled.ShouldBe(2);
        settled[0].Counts.Unsucceeded.ShouldBe(0);
        settled[1].Counts[NodeStatus.Succeeded].ShouldBe(2);
        settled[1].Counts.Total.ShouldBe(4);
        settled[1].Counts.Unsucceeded.ShouldBe(2);
        settled[1].Counts.Waiting.ShouldBe(0);
        settled[1].Counts.ShouldBe(result.Snapshot[shard.Path].Counts);

        var final = result.Snapshot.Counts;
        final[NodeStatus.Succeeded].ShouldBe(2);
        final.Total.ShouldBe(5);
        final[NodeStatus.Blocked].ShouldBe(2);
        final.Settled.ShouldBe(5);
        final.Unsucceeded.ShouldBe(3);
        final.Waiting.ShouldBe(0);
        final.Working.ShouldBe(0);
    }

    [Test]
    public async Task A_continuing_run_counts_its_earlier_successes_in_the_counts_of_the_run_and_of_a_group()
    {
        using var temporary = new TemporaryDirectory();
        var fail = true;
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1");
        var done = shard.Group("tenant-a");
        var doneLoad = done.Task("load", _ => Task.CompletedTask);
        var mixed = shard.Group("tenant-b");
        mixed.Task("load", _ => Task.CompletedTask);
        mixed.Task("verify", context => fail ? throw context.Fail("bad data") : Task.CompletedTask);
        var graph = builder.Build();

        var first = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path) });
        fail = false;
        var reporter = new RecordingReporter();
        var second = await Runner.RunAsync(
            graph,
            new RunOptions { Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id), Reporters = [reporter] });

        var start = reporter.Snapshots[0];
        start.Counts.EarlierSuccesses.ShouldBe(2);
        start.Counts[NodeStatus.Succeeded].ShouldBe(2);
        start.Counts.Unsucceeded.ShouldBe(1);
        start[done.Path].Counts.EarlierSuccesses.ShouldBe(1);
        start[mixed.Path].Counts.EarlierSuccesses.ShouldBe(1);
        start[doneLoad.Path].Counts.EarlierSuccesses.ShouldBe(1);
        start[doneLoad.Path].Counts[NodeStatus.Succeeded].ShouldBe(1);
        var settled = reporter.Events.OfType<GroupSettled>().ToList();
        settled.Select(group => (group.Path, group.Counts.EarlierSuccesses, group.Counts[NodeStatus.Succeeded])).ShouldBe(
        [
            (mixed.Path, 1, 2),
            (shard.Path, 2, 3),
        ]);
        second.Snapshot.Counts.EarlierSuccesses.ShouldBe(2);
        second.Snapshot.Counts.Unsucceeded.ShouldBe(0);
        second.Snapshot[shard.Path].Counts.EarlierSuccesses.ShouldBe(2);
    }

    [Test]
    public async Task A_group_says_how_many_tasks_beneath_it_wait_and_flips_between_waiting_and_working_as_they_do()
    {
        var firstPoll = new PausedBody();
        var secondPoll = new PausedBody();
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1");
        var tenant = shard.Group("tenant-a");
        tenant.Task("poll-a", firstPoll.Body);
        shard.Task("poll-b", secondPoll.Body);
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        var source = await reporter.Source;
        var first = await firstPoll.Started;
        var second = await secondPoll.Started;
        var bothWorking = source.Current;
        var firstWait = first.Waiting("job 17");
        var oneWaiting = source.Current;
        var secondWait = second.Waiting("job 18");
        var bothWaiting = source.Current;
        firstWait.Dispose();
        var oneWaitingAgain = source.Current;
        secondWait.Dispose();
        var neitherWaiting = source.Current;
        firstPoll.Release();
        secondPoll.Release();
        var result = await run;

        bothWorking.Counts.Waiting.ShouldBe(0);
        bothWorking[shard.Path].IsWorking.ShouldBeTrue();
        bothWorking[shard.Path].IsWaiting.ShouldBeFalse();
        bothWorking[tenant.Path].IsWorking.ShouldBeTrue();

        oneWaiting.Counts.Waiting.ShouldBe(1);
        oneWaiting[shard.Path].Counts.Waiting.ShouldBe(1);
        oneWaiting[shard.Path].Counts.Working.ShouldBe(1);
        oneWaiting[shard.Path].IsWaiting.ShouldBeFalse();
        oneWaiting[shard.Path].IsWorking.ShouldBeTrue();
        oneWaiting[tenant.Path].Counts.Waiting.ShouldBe(1);
        oneWaiting[tenant.Path].IsWaiting.ShouldBeTrue();
        oneWaiting[tenant.Path].IsWorking.ShouldBeFalse();

        bothWaiting.Counts.Waiting.ShouldBe(2);
        bothWaiting.Counts.Working.ShouldBe(0);
        bothWaiting[shard.Path].Counts.Waiting.ShouldBe(2);
        bothWaiting[shard.Path].IsWaiting.ShouldBeTrue();
        bothWaiting[shard.Path].IsWorking.ShouldBeFalse();

        oneWaitingAgain[tenant.Path].Counts.Waiting.ShouldBe(0);
        oneWaitingAgain[tenant.Path].IsWaiting.ShouldBeFalse();
        oneWaitingAgain[tenant.Path].IsWorking.ShouldBeTrue();
        oneWaitingAgain[shard.Path].Counts.Waiting.ShouldBe(1);
        oneWaitingAgain[shard.Path].IsWaiting.ShouldBeFalse();

        neitherWaiting.Counts.Waiting.ShouldBe(0);
        neitherWaiting[shard.Path].Counts.Waiting.ShouldBe(0);
        neitherWaiting[shard.Path].IsWorking.ShouldBeTrue();

        result.Snapshot.Counts.Waiting.ShouldBe(0);
        result.Snapshot[shard.Path].IsWaiting.ShouldBeFalse();
        result.Snapshot[shard.Path].IsWorking.ShouldBeFalse();
        result.Snapshot[tenant.Path].IsWaiting.ShouldBeFalse();
        result.Snapshot[tenant.Path].IsWorking.ShouldBeFalse();
    }

    [Test]
    public async Task A_task_that_settles_while_waiting_stops_counting_as_waiting_in_every_group_above_it()
    {
        var polling = new PausedBody();
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1");
        var tenant = shard.Group("tenant-a");
        tenant.Task("poll", polling.Body);
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        var source = await reporter.Source;
        var context = await polling.Started;
        using var wait = context.Waiting("job 17");
        var waiting = source.Current;
        polling.Release();
        var result = await run;

        waiting[shard.Path].IsWaiting.ShouldBeTrue();
        waiting[tenant.Path].Counts.Waiting.ShouldBe(1);
        result.Snapshot.Counts.Waiting.ShouldBe(0);
        result.Snapshot[shard.Path].Counts.Waiting.ShouldBe(0);
        result.Snapshot[tenant.Path].Counts.Waiting.ShouldBe(0);
        var shardSettled = reporter.Events.OfType<GroupSettled>().Single(group => group.Path == shard.Path);
        shardSettled.Counts.Waiting.ShouldBe(0);
    }

    [Test]
    public async Task A_group_counts_every_warning_beneath_it_including_those_beyond_the_number_kept()
    {
        var warning = new PausedBody();
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1");
        var tenant = shard.Group("tenant-a");
        var noisy = tenant.Task("noisy", warning.Body);
        var quiet = shard.Task("quiet", context =>
        {
            context.Warn("entity 9 has no region");
            return Task.CompletedTask;
        });
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.None, Reporters = [reporter] });
        var source = await reporter.Source;
        var context = await warning.Started;
        for (var entity = 1; entity <= 100; entity++)
        {
            context.Warn($"entity {entity} has no region");
        }

        var atTheNumberKept = source.Current;
        context.Warn("entity 101 has no region");
        var beyondIt = source.Current;
        warning.Release();
        var result = await run;

        atTheNumberKept[noisy.Path].WarningCount.ShouldBe(100);
        atTheNumberKept[tenant.Path].WarningCount.ShouldBe(100);
        beyondIt[noisy.Path].WarningCount.ShouldBe(101);
        beyondIt[tenant.Path].WarningCount.ShouldBe(101);
        beyondIt[tenant.Path].Warnings.ShouldBeEmpty();
        result.Snapshot[tenant.Path].WarningCount.ShouldBe(101);
        result.Snapshot[shard.Path].WarningCount.ShouldBe(102);
        result.Snapshot[quiet.Path].WarningCount.ShouldBe(1);
        result.Snapshot.WarningCount.ShouldBe(102);
    }
}
