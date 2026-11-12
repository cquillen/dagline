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
        case "--dir" when arguments.TryDequeue(out var path):
            directory = path;
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
            return Refuse($"bad option: {option}. Options: --shard 1,3  --tenants <n>  --fast  --plain  --ascii  --dir <path>");
    }
}

// --fast makes every fake delay and wait interval a thousand times shorter.
var timeScale = fast ? 0.001 : 1;
var store = new EntityStore(timeScale);
var scheduler = new FakeScheduler(Path.Combine(directory, "jobs"), TimeProvider.System, timeScale);
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
    var name = $"shard-{shard}";
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
    return Refuse(exception.Message);
}

// The limits of all ten shards are sized whatever --shard chose: a size for a limit the graph does not name is ignored.
var limits = Enumerable.Range(1, ShardCount).ToDictionary(shard => $"shard-{shard}-db", _ => 4);
limits["scheduler"] = 3;

// A new journal on every run, described by the shards it covers.
var description = shards.Count == ShardCount ? "all shards" : $"shards {string.Join(',', shards)}";
var journal = Journal.New(directory, description);

// A new journal starts the jobs of this graph's shards afresh, so the scheduler forgets them. Other shards' jobs stay.
scheduler.Forget(shards.Select(shard => $"shard-{shard}"));

// Ctrl+C: the first press stops the run, the second stops waiting for the running tasks to end, the third kills.
using var stop = ConsoleStop.Hook();

RunResult result;
try
{
    result = await Runner.RunAsync(graph, new RunOptions
    {
        Cap = 16,
        Limits = limits,
        Journal = journal,
        Reporters = [SpectreReporter.Auto(options: new SpectreOptions
        {
            Display = plain ? DisplayMode.Never : DisplayMode.Auto,
            Ascii = ascii,
        })],
    }, stop);
}
catch (DaglineException exception) when (exception is RunConfigurationException or JournalInUseException or JournalOpenException or JournalFormatException)
{
    return Refuse(exception.Message);
}
catch (JournalWriteException exception)
{
    await Console.Error.WriteLineAsync(exception.Message);
    return 70;
}

// 0 when every task succeeded, 1 when one did not, 2 when the run was stopped.
return result.Outcome.ToExitCode();

static int Refuse(string message)
{
    Console.Error.WriteLine(message);
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
