using Dagline.TestSupport;

namespace Dagline.Tests;

public class LimitTests
{
    private const int _repeats = 50;

    [Test]
    public async Task The_number_of_running_tasks_that_hold_a_limit_never_exceeds_its_size()
    {
        const int size = 2;

        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var loads = new OverlappingBodies(expected: size);
            var builder = new GraphBuilder();
            for (var shard = 1; shard <= 10; shard++)
            {
                builder.Task($"load-{shard}", loads.Body).Limit("database");
                builder.Task($"verify-{shard}", async _ => await Task.Yield());
            }

            var reporter = new RecordingReporter();

            var result = await Runner.RunAsync(
                builder.Build(),
                new RunOptions
                {
                    Journal = Journal.None,
                    Limits = new Dictionary<string, int> { ["database"] = size },
                    Reporters = [reporter],
                });

            result.Outcome.ShouldBe(RunOutcome.Complete);
            loads.Highest.ShouldBe(size);

            // A body the run should not have started can begin after the others have left, and then it is not counted above.
            // The snapshot handed with its start counts it whenever its body gets a thread.
            reporter.Snapshots.Max(RunningHoldersOfDatabase).ShouldBe(size);
            reporter.Snapshots.Max(snapshot => snapshot.Limits["database"].InUse).ShouldBe(size);
        }
    }

    [Test]
    public async Task A_limit_named_on_a_group_applies_to_every_task_beneath_it_at_any_depth()
    {
        const int size = 2;

        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var loads = new OverlappingBodies(expected: size);
            var builder = new GraphBuilder();
            var shard = builder.Group("shard-1");
            shard.Task("prepare", loads.Body);
            foreach (var tenant in new[] { "tenant-a", "tenant-b", "tenant-c" })
            {
                var tenantGroup = shard.Group(tenant);
                tenantGroup.Task("load", loads.Body);
                tenantGroup.Group("batch-1").Task("verify", loads.Body);
            }

            // The limit covers the tasks added before it was named as well.
            shard.Limit("database");
            var reporter = new RecordingReporter();

            var result = await Runner.RunAsync(
                builder.Build(),
                new RunOptions
                {
                    Journal = Journal.None,
                    Limits = new Dictionary<string, int> { ["database"] = size },
                    Reporters = [reporter],
                });

            result.Outcome.ShouldBe(RunOutcome.Complete);
            loads.Highest.ShouldBe(size);

            // Every task of this graph is beneath the group, so every running task holds the limit.
            reporter.Snapshots.Max(snapshot => snapshot.Counts[NodeStatus.Running]).ShouldBe(size);
            reporter.Snapshots.Max(snapshot => snapshot.Limits["database"].InUse).ShouldBe(size);
        }
    }

    [Test]
    [Arguments("network")]
    [Arguments("database")]
    [Arguments("files")]
    public async Task A_task_holds_its_own_limits_and_those_of_every_group_that_holds_it(string fullLimit)
    {
        // Not repeated: the two tasks share a limit of size 1, so one body runs at a time and the events can come in one order only.
        var builder = new GraphBuilder();
        builder.Task("holder", _ => Task.CompletedTask).Limit(fullLimit);
        builder.Group("shard-1").Limit("network")
            .Group("tenant-a").Limit("database")
            .Task("load", _ => Task.CompletedTask).Limit("files");
        var reporter = new RecordingReporter();

        var result = await Runner.RunAsync(
            builder.Build(),
            new RunOptions
            {
                Journal = Journal.None,
                Limits = new Dictionary<string, int> { ["network"] = 1, ["database"] = 1, ["files"] = 1 },
                Reporters = [reporter],
            });

        // Whichever of its three limits the other task fills, the nested task is passed over until that task settles.
        result.Outcome.ShouldBe(RunOutcome.Complete);
        reporter.Lines.Where(line => !line.StartsWith("group ", StringComparison.Ordinal)).ShouldBe(
        [
            "run started",
            "holder: pending -> ready",
            "shard-1/tenant-a/load: pending -> ready",
            "holder: ready -> running",
            "holder: running -> succeeded",
            "shard-1/tenant-a/load: ready -> running",
            "shard-1/tenant-a/load: running -> succeeded",
            "run ended: complete",
        ]);
    }

    [Test]
    public async Task A_task_that_needs_two_limits_holds_neither_while_one_of_them_is_full()
    {
        // Repeated: load and export end in either order, and merge must start only once both have given their slots back.
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            var load = new PausedBody();
            var builder = new GraphBuilder();
            builder.Task("load", load.Body).Limit("database");
            builder.Task("merge", _ => Task.CompletedTask).Limit("database").Limit("files");
            builder.Task("export", _ => Task.CompletedTask).Limit("files");
            var reporter = new RecordingReporter();

            var run = Runner.RunAsync(
                builder.Build(),
                new RunOptions
                {
                    Journal = Journal.None,
                    Limits = new Dictionary<string, int> { ["database"] = 1, ["files"] = 1 },
                    Reporters = [reporter],
                });
            await load.Started;
            load.Release();
            var result = await run;

            // Had merge taken its slot of files while it waited for database, export could not have started in the first step.
            result.Outcome.ShouldBe(RunOutcome.Complete);
            var lines = reporter.Lines.ToList();
            lines.Take(6).ShouldBe(
            [
                "run started",
                "load: pending -> ready",
                "merge: pending -> ready",
                "export: pending -> ready",
                "load: ready -> running",
                "export: ready -> running",
            ]);
            lines.IndexOf("merge: ready -> running").ShouldBeGreaterThan(lines.IndexOf("load: running -> succeeded"));
            lines.IndexOf("merge: ready -> running").ShouldBeGreaterThan(lines.IndexOf("export: running -> succeeded"));
        }
    }

    [Test]
    public async Task A_ready_task_whose_limit_is_full_is_passed_over_and_a_task_behind_it_starts()
    {
        // Not repeated: every body is paused and the test releases one at a time, so the events asserted can come in one order only.
        var firstLoad = new PausedBody();
        var secondLoad = new PausedBody();
        var publish = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("load-1", firstLoad.Body).Limit("database");
        var held = builder.Task("load-2", secondLoad.Body).Limit("database");
        builder.Task("publish", publish.Body);
        var reporter = new RecordingReporter();

        var run = Runner.RunAsync(
            builder.Build(),
            new RunOptions
            {
                Journal = Journal.None,
                Limits = new Dictionary<string, int> { ["database"] = 1 },
                Reporters = [reporter],
            });
        var source = await reporter.Source;
        await firstLoad.Started;
        await publish.Started;
        var limitFull = source.Current;
        firstLoad.Release();
        await secondLoad.Started;
        secondLoad.Release();
        publish.Release();
        var result = await run;

        result.Outcome.ShouldBe(RunOutcome.Complete);
        limitFull[held.Path].Status.ShouldBe(NodeStatus.Ready);
        reporter.Lines.Take(8).ShouldBe(
        [
            "run started",
            "load-1: pending -> ready",
            "load-2: pending -> ready",
            "publish: pending -> ready",
            "load-1: ready -> running",
            "publish: ready -> running",
            "load-1: running -> succeeded",
            "load-2: ready -> running",
        ]);
    }

    private static int RunningHoldersOfDatabase(Snapshot snapshot)
        => snapshot.Nodes.Count(node => node.Limits.Contains("database") && node.Status is NodeStatus.Running);
}
