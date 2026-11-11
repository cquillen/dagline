// PROTOTYPE (throwaway). The reference scenario written against the stub API; it only has to compile.
using Dagline;
using Dagline.Spectre;
using Dagline.Testing;

var shardFilter = args.Where(a => a.StartsWith("--shard=")).Select(a => a[8..]).ToHashSet();
var scheduler = new FakeScheduler(); // the app's services are plain captured variables
var directory = "journals/nightly";

// ---- build: a filter threaded through plain loops ------------------------
var g = new GraphBuilder();
var shardGroups = new List<GroupNode>();
foreach (var shard in Shards.All.Where(s => shardFilter.Count == 0 || shardFilter.Contains(s)))
{
    var shardGroup = g.Group($"shard-{shard}").Title($"Shard {shard}").Limit("db");
    shardGroups.Add(shardGroup);
    foreach (var tenant in Tenants.For(shard))
    {
        var tenantGroup = shardGroup.Group(tenant);
        var prepare = tenantGroup.Task("prepare", ctx => Tasks.Prepare(tenant, ctx));
        tenantGroup.Task("validate", ctx => Tasks.Validate(tenant, ctx)).Requires(prepare);
    }
}

// The gate is a dependency on a collection of group handles: it shrinks with the filter and is valid when empty.
g.Task("submit-job", ctx => Tasks.SubmitAndWait(scheduler, ctx)).Requires(shardGroups).Limit("scheduler");

Graph graph;
try { graph = g.Build(); }
catch (GraphException e) // one error, every problem
{
    foreach (var p in e.Problems) Console.Error.WriteLine($"{p.Path}: {p.Message}");
    return 64;
}

// ---- journal policy: the app's code -------------------------------------
var unfinished = Journals.List(directory).FirstOrDefault(j => !j.Finished);
var journal = unfinished is not null && Ask($"Continue '{unfinished.Description}'?")
    ? Journal.Continue(directory, unfinished.Id)
    : Journal.New(directory, $"nightly {DateTime.Today:yyyy-MM-dd}, shards: {string.Join(',', shardFilter)}");

// ---- run ----------------------------------------------------------------
using var stop = ConsoleStop.Hook();
try
{
    var result = await Runner.RunAsync(graph, new RunOptions
    {
        Cap = 16,
        Limits = new Dictionary<string, int> { ["db"] = 4, ["scheduler"] = 2 },
        Journal = journal,
        Reporters = [SpectreReporter.Auto(options: new SpectreOptions { Ascii = args.Contains("--ascii") })],
    }, stop);

    foreach (var e in result.ReporterErrors) Console.Error.WriteLine(e.Exception);
    return result.Outcome.ToExitCode();
}
catch (JournalInUseException) { Console.Error.WriteLine("That journal is in use by another run."); return 64; }
catch (JournalWriteException e) { Console.Error.WriteLine($"Journal write failed; {e.Snapshot.Counts[NodeStatus.Succeeded]} tasks had succeeded."); return 70; }

static bool Ask(string question) => true;

// ---- the tasks, as named methods so the harness can take them -----------
static class Tasks
{
    public static async Task Validate(string tenant, TaskContext ctx)
    {
        ctx.Status("checking entities");
        var total = await Entities.Count(tenant, ctx.CancellationToken);
        var bad = new List<string>();
        for (var i = 0; i < total; i++)
        {
            ctx.Count(i, total);
            if (!await Entities.Check(tenant, i, ctx.CancellationToken)) bad.Add($"entity {i}");
        }
        if (bad.Count > 0) throw ctx.Fail($"{bad.Count} bad entities", [.. bad]);
        if (Entities.Stale(tenant)) ctx.Warn("directory entry is stale");
    }

    public static Task Prepare(string tenant, TaskContext ctx) => Task.CompletedTask;

    public static async Task SubmitAndWait(FakeScheduler scheduler, TaskContext ctx)
    {
        var jobId = ctx.Checkpoint;
        if (jobId.Length == 0)
        {
            jobId = await scheduler.Trigger(ctx.CancellationToken);
            await ctx.SaveCheckpointAsync(jobId, ctx.CancellationToken); // on disk before it returns
        }
        await ctx.WaitUntil(TimeSpan.FromSeconds(30), async ct =>
        {
            switch (await scheduler.Status(jobId, ct))
            {
                case JobState.Done: return true;
                case JobState.Error: throw ctx.Fail("external job failed", await scheduler.Log(jobId, ct));
                default: return false;
            }
        }, "waiting for the job");
    }
}

// ---- a consumer test of one task, with no test framework needed here ----
static class SampleTests
{
    public static async Task TriggerOrReattach()
    {
        var scheduler = new FakeScheduler();
        await using var harness = TaskHarness.For(ctx => Tasks.SubmitAndWait(scheduler, ctx), new InstantClock());
        await harness.RunUntilWaitingThenStopAsync(); // first run: triggers, saves, waits, is stopped
        var second = await harness.RunAsync();        // continues the harness's journal: re-attaches
        _ = (second.Status == NodeStatus.Succeeded) && scheduler.Triggers == 1;
    }
}

// ---- fakes ---------------------------------------------------------------
enum JobState { Running, Done, Error }
sealed class FakeScheduler
{
    public int Triggers;
    public Task<string> Trigger(CancellationToken ct) => Task.FromResult("job-1");
    public Task<JobState> Status(string id, CancellationToken ct) => Task.FromResult(JobState.Done);
    public Task<string[]> Log(string id, CancellationToken ct) => Task.FromResult(Array.Empty<string>());
}
static class Shards { public static readonly string[] All = ["1", "2", "3"]; }
static class Tenants { public static IEnumerable<string> For(string shard) => ["a", "b"]; }
static class Entities
{
    public static Task<int> Count(string t, CancellationToken ct) => Task.FromResult(1000);
    public static Task<bool> Check(string t, int i, CancellationToken ct) => Task.FromResult(true);
    public static bool Stale(string t) => false;
}
