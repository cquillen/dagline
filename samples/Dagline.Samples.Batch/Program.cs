using Dagline;
using Dagline.Samples.Batch;
using Dagline.Spectre;

const int ShardCount = 10;

// The options, parsed by hand. A bad one exits with 64.
List<int> shards = [.. Enumerable.Range(1, ShardCount)];
var tenantsPerShard = 75;
var fast = false;
var plain = false;
var ascii = false;
var directory = "batch-data";
var startNew = false;
var faults = new HashSet<string>();
string[] knownFaults = ["validate", "poll", "job"];

var arguments = new Queue<string>(args);
while (arguments.TryDequeue(out var option))
{
    switch (option)
    {
        case "--shard" when arguments.TryDequeue(out var list) && TryParseShards(list, out var chosen):
            shards = chosen;
            break;
        case "--tenants" when arguments.TryDequeue(out var count) && int.TryParse(count, out tenantsPerShard) && tenantsPerShard > 0:
            break;
        case "--dir" when arguments.TryDequeue(out var path) && !string.IsNullOrWhiteSpace(path):
            directory = path;
            break;
        case "--new":
            startNew = true;
            break;
        case "--fault" when arguments.TryDequeue(out var fault):
            if (!knownFaults.Contains(fault))
            {
                return await RefuseAsync($"bad fault: {fault}. Faults: {string.Join(", ", knownFaults)}");
            }

            faults.Add(fault);
            break;
        case "--fast":
            fast = true;
            break;
        case "--plain":
            plain = true;
            break;
        case "--ascii":
            ascii = true;
            break;
        default:
            return await RefuseAsync($"bad option: {option}. Options: --shard 1,3  --tenants <n>  --new  --fault validate|poll|job  --fast  --plain  --ascii  --dir <path>");
    }
}

// --fast makes every fake delay and wait interval a thousand times shorter. Each fault is set up in a fake:
// validate gives the first tenant of the first shard bad entities, poll makes the first status call of the first job
// throw, and job makes the last job end in error.
var timeScale = fast ? 0.001 : 1;
var store = new EntityStore(timeScale)
{
    TenantsWithBadEntities = faults.Contains("validate") ? new HashSet<string> { $"tenant-{shards[0]}-001" } : [],
};
var scheduler = new FakeScheduler(Path.Combine(directory, "jobs"), TimeProvider.System, timeScale)
{
    JobsWhoseFirstStatusCallThrows = faults.Contains("poll") ? new HashSet<string> { JobName(shards[0]) } : [],
    JobsThatEndInError = faults.Contains("job") ? new HashSet<string> { JobName(shards[^1]) } : [],
};
var waitInterval = TimeSpan.FromSeconds(2 * timeScale);

// The graph: a group per shard, each holding a group per tenant with a load and a validate task. The jobs group
// requires every shard group, so one failed validation blocks every job, and the gate shrinks with --shard.
var builder = new GraphBuilder();
var shardGroups = new List<GroupNode>();
foreach (var shard in shards)
{
    var shardGroup = builder.Group($"shard-{shard}").Limit($"shard-{shard}-db");
    for (var number = 1; number <= tenantsPerShard; number++)
    {
        var tenant = $"tenant-{shard}-{number:000}";
        var tenantGroup = shardGroup.Group(tenant);
        var load = tenantGroup.Task("load", context => BatchTasks.Load(tenant, store, context));
        tenantGroup.Task("validate", context => BatchTasks.Validate(tenant, store, context)).Requires(load);
    }

    shardGroups.Add(shardGroup);
}

var jobs = builder.Group("jobs").Requires(shardGroups);
var jobTasks = new List<TaskNode>();
foreach (var shard in shards)
{
    var name = JobName(shard);
    jobTasks.Add(jobs.Task(name, context => BatchTasks.Job(name, scheduler, waitInterval, context)).Limit("scheduler"));
}

builder.Task("report", context => BatchTasks.Report(jobTasks, context)).After(jobs);

Graph graph;
try
{
    graph = builder.Build();
}
catch (GraphException exception)
{
    return await RefuseAsync(exception.Message);
}

// The limits of all ten shards are sized whatever --shard chose: a size for a limit the graph does not name is ignored.
var limits = Enumerable.Range(1, ShardCount).ToDictionary(shard => $"shard-{shard}-db", _ => 4);
limits["scheduler"] = 3;

// The journal policy: continue the newest unfinished journal for the same shards, or start a new one. A journal that
// another run holds is not skipped, so that run refuses it below.
var description = shards.Count == ShardCount ? "all shards" : $"shards {string.Join(',', shards)}";
IReadOnlyList<JournalSummary> journals;
try
{
    journals = await Journals.ListAsync(directory);
}
catch (DaglineException exception) when (exception is JournalOpenException or JournalFormatException)
{
    return await RefuseAsync(exception.Message);
}

var unfinished = startNew ? null : journals.LastOrDefault(summary => !summary.Finished && summary.Description == description);
Journal journal;
if (unfinished is null)
{
    journal = Journal.New(directory, description);
    Console.WriteLine($"Starting a new journal for {description}.");

    // A new journal starts the jobs of this graph's shards afresh, so the scheduler forgets them. Other shards' jobs stay.
    scheduler.Forget(shards.Select(JobName));
}
else
{
    journal = Journal.Continue(directory, unfinished.Id);
    Console.WriteLine($"Continuing journal {unfinished.Id} for {description} (runs so far: {unfinished.Runs}).");
}

// Ctrl+C or SIGTERM: the first stops the run, the second stops waiting for the running tasks to end, the third kills.
using var stop = ConsoleStop.Hook();

RunResult result;
try
{
    result = await Runner.RunAsync(graph, new RunOptions
    {
        Cap = 16,
        Limits = limits,
        Journal = journal,
        Reporters = [SpectreReporter.Create(new SpectreOptions
        {
            Display = plain ? DisplayMode.Never : DisplayMode.Auto,
            Ascii = ascii,
        })],
    }, stop);
}
catch (DaglineException exception) when (exception is RunConfigurationException or JournalInUseException or JournalNotFoundException or JournalOpenException or JournalFormatException)
{
    return await RefuseAsync(exception.Message);
}
catch (JournalWriteException exception)
{
    await Console.Error.WriteLineAsync(exception.Message);
    return 70;
}

// 0 when every task succeeded, 1 when one did not, 2 when the run was stopped.
return result.Outcome.ToExitCode();

// Each shard's job and its job task share one name, which the faults and the scheduler's Forget use as well.
static string JobName(int shard) => $"shard-{shard}";

static async Task<int> RefuseAsync(string message)
{
    await Console.Error.WriteLineAsync(message);
    return 64;
}

static bool TryParseShards(string list, out List<int> shards)
{
    shards = [];
    foreach (var item in list.Split(','))
    {
        if (!int.TryParse(item, out var shard) || shard is < 1 or > ShardCount)
        {
            return false;
        }

        shards.Add(shard);
    }

    shards = [.. shards.Distinct().Order()];
    return true;
}
