using BenchmarkDotNet.Attributes;

namespace Dagline.Benchmarks;

/// <summary>
/// What a whole run costs in time and allocation, from <see cref="Runner.RunAsync"/> to its result, with the plain-text
/// reporter attached and writing to nowhere. Each scenario builds its graph once and runs it afresh on every invocation.
/// </summary>
/// <remarks>
/// Only Dagline's public API is used, and nothing reads the counts on a snapshot, so the scenarios measure the same work
/// before and after the engine changes they are compared across.
/// </remarks>
[MemoryDiagnoser]
public class RunBenchmarks
{
    private const int ProgressReports = 200_000;
    private const int WarningsPerTask = 250;
    private const int WaitsPerTask = 4;
    private const int Shards = 10;
    private const int TenantsPerShard = 150;
    private const int ShardLimitSize = 2;
    private const string SharedLimit = "shared";
    private const int SharedLimitSize = 4;

    private static readonly string[] _statusTexts = Texts("working on item", 1_000);
    private static readonly string[] _warnings = Texts("warning about item", WarningsPerTask);
    private static readonly string[] _waitingTexts = Texts("external job", WaitsPerTask);

    private Graph _largeGraph = null!;
    private Graph _progress = null!;
    private Graph _warningsBeyondKept = null!;
    private Graph _waiting = null!;
    private Graph _heldByLimits = null!;
    private Graph _heldByOneSmallLimit = null!;
    private Graph _onAJournal = null!;
    private string _journals = null!;

    [GlobalSetup]
    public async Task BuildGraphs()
    {
        // 4 levels of 4 groups, 8 tasks in each of the 256 innermost: 2,048 tasks, each requiring the one before it.
        _largeGraph = Nested([4, 4, 4, 4], tasksPerGroup: 8, chained: true, ReportAndSucceed);

        // One task, 3 groups deep, that sets its status text and its count 200,000 times each.
        _progress = Nested([1, 1, 1], tasksPerGroup: 1, chained: false, ReportProgressOften);

        // 256 tasks, 3 groups deep, each given 250 warnings when 100 are kept.
        _warningsBeyondKept = Nested([4, 4, 4], tasksPerGroup: 4, chained: false, WarnBeyondKept);

        // 1,024 tasks, 3 groups deep, each beginning and ending waiting 4 times.
        _waiting = Nested([4, 4, 4], tasksPerGroup: 16, chained: false, WaitAndEndWaiting);

        // 1,500 tasks in 10 shards, each shard's tasks holding a limit of 2, so nearly every ready task is held by a full limit.
        _heldByLimits = Sharded(Shards, TenantsPerShard, limited: true);

        // 10,000 tasks in 100 groups of 100, every task holding one limit of 4, so each settle leaves thousands of ready tasks held.
        _heldByOneSmallLimit = OneSharedLimit(groups: 100, tasksPerGroup: 100);

        // 160 tasks on a journal file, so each settle is flushed to the disk.
        _onAJournal = Sharded(shards: 4, tenantsPerShard: 40, limited: false);
        _journals = Directory.CreateTempSubdirectory("dagline-benchmarks-").FullName;

        await Check(_largeGraph, expectedWarnings: 0).ConfigureAwait(false);
        await Check(_heldByLimits, expectedWarnings: 0).ConfigureAwait(false);
        await Check(_heldByOneSmallLimit, expectedWarnings: 0, OneSharedLimitOptions()).ConfigureAwait(false);
        await Check(_onAJournal, expectedWarnings: 0, OnAJournal()).ConfigureAwait(false);
        await Check(_progress, expectedWarnings: 0).ConfigureAwait(false);
        await Check(_warningsBeyondKept, expectedWarnings: 256 * WarningsPerTask).ConfigureAwait(false);
        await Check(_waiting, expectedWarnings: 0).ConfigureAwait(false);
    }

    [GlobalCleanup]
    public void DeleteJournals() => Directory.Delete(_journals, recursive: true);

    /// <summary>1,500 tasks in 10 shards, each shard's tasks holding a limit of 2 slots.</summary>
    [Benchmark]
    public Task<RunOutcome> HeldByLimits() => Run(_heldByLimits);

    /// <summary>10,000 tasks with no dependencies, every one holding one limit of 4 slots.</summary>
    [Benchmark]
    public Task<RunOutcome> HeldByOneSmallLimit() => Run(_heldByOneSmallLimit, OneSharedLimitOptions());

    /// <summary>160 tasks on a new journal file, each settle flushed to the disk.</summary>
    [Benchmark]
    public Task<RunOutcome> OnAJournalFile() => Run(_onAJournal, OnAJournal());

    /// <summary>2,048 tasks beneath groups nested 4 deep, each reporting a little and succeeding.</summary>
    [Benchmark]
    public Task<RunOutcome> LargeGraph() => Run(_largeGraph);

    /// <summary>One task setting its status text and its count 200,000 times each.</summary>
    [Benchmark]
    public Task<RunOutcome> FrequentProgress() => Run(_progress);

    /// <summary>256 tasks beneath nested groups, each given 250 warnings when 100 are kept.</summary>
    [Benchmark]
    public Task<RunOutcome> WarningsBeyondKept() => Run(_warningsBeyondKept);

    /// <summary>1,024 tasks beneath nested groups, each beginning and ending waiting 4 times.</summary>
    [Benchmark]
    public Task<RunOutcome> BeginAndEndWaiting() => Run(_waiting);

    private static async Task<RunOutcome> Run(Graph graph, RunOptions? options = null)
    {
        var result = await Runner.RunAsync(graph, options ?? Options()).ConfigureAwait(false);

        return result.Outcome;
    }

    private static RunOptions Options() => new()
    {
        Journal = Journal.None,
        Limits = Enumerable.Range(1, Shards).ToDictionary(shard => $"shard-{shard}", _ => ShardLimitSize),
        Reporters = [new PlainTextReporter(TextWriter.Null)],
    };

    private static RunOptions OneSharedLimitOptions() => new()
    {
        Journal = Journal.None,
        Limits = new Dictionary<string, int> { [SharedLimit] = SharedLimitSize },
        Reporters = [new PlainTextReporter(TextWriter.Null)],
    };

    private RunOptions OnAJournal() => Options() with { Journal = Journal.New(_journals) };

    // A scenario that does not do what it says would measure nothing, so each is run once and checked before it is measured.
    private static async Task Check(Graph graph, int expectedWarnings, RunOptions? options = null)
    {
        var result = await Runner.RunAsync(graph, options ?? Options()).ConfigureAwait(false);

        if (result.Outcome is not RunOutcome.Complete || result.ReporterErrors.Count > 0)
        {
            throw new InvalidOperationException($"The scenario ended {result.Outcome} with {result.ReporterErrors.Count} reporter errors.");
        }

        if (result.Snapshot.WarningCount != expectedWarnings)
        {
            throw new InvalidOperationException($"The scenario gave {result.Snapshot.WarningCount} warnings, not {expectedWarnings}.");
        }
    }

    private static Graph Sharded(int shards, int tenantsPerShard, bool limited)
    {
        var builder = new GraphBuilder();
        for (var shard = 1; shard <= shards; shard++)
        {
            var group = builder.Group($"shard-{shard}");
            if (limited)
            {
                group.Limit($"shard-{shard}");
            }

            for (var tenant = 1; tenant <= tenantsPerShard; tenant++)
            {
                group.Task($"tenant-{tenant}", ReportAndSucceed);
            }
        }

        return builder.Build();
    }

    private static Graph OneSharedLimit(int groups, int tasksPerGroup)
    {
        var builder = new GraphBuilder();
        for (var index = 1; index <= groups; index++)
        {
            var group = builder.Group($"group-{index}");
            for (var task = 1; task <= tasksPerGroup; task++)
            {
                group.Task($"task-{task}", ReportAndSucceed).Limit(SharedLimit);
            }
        }

        return builder.Build();
    }

    private static Graph Nested(int[] groupsPerLevel, int tasksPerGroup, bool chained, TaskBody body)
    {
        var builder = new GraphBuilder();
        var outermost = Enumerable.Range(1, groupsPerLevel[0]).Select(index => builder.Group($"group-{index}"));
        AddLevels(outermost, groupsPerLevel.AsSpan(1), tasksPerGroup, chained, body);

        return builder.Build();
    }

    private static void AddLevels(IEnumerable<GroupNode> groups, ReadOnlySpan<int> groupsPerLevel, int tasksPerGroup, bool chained, TaskBody body)
    {
        foreach (var group in groups.ToList())
        {
            if (groupsPerLevel.IsEmpty)
            {
                AddTasks(group, tasksPerGroup, chained, body);
                continue;
            }

            var inner = Enumerable.Range(1, groupsPerLevel[0]).Select(index => group.Group($"group-{index}"));
            AddLevels(inner, groupsPerLevel[1..], tasksPerGroup, chained, body);
        }
    }

    private static void AddTasks(GroupNode group, int count, bool chained, TaskBody body)
    {
        TaskNode? previous = null;
        for (var index = 1; index <= count; index++)
        {
            var task = group.Task($"task-{index}", body);
            if (chained && previous is not null)
            {
                task.Requires(previous);
            }

            previous = task;
        }
    }

    private static Task ReportAndSucceed(TaskContext context)
    {
        context.SetStatusText(_statusTexts[0]);
        context.SetCount(1, total: 1);
        context.SetMessage("done");

        return Task.CompletedTask;
    }

    private static Task ReportProgressOften(TaskContext context)
    {
        for (var report = 0; report < ProgressReports; report++)
        {
            context.SetStatusText(_statusTexts[report % _statusTexts.Length]);
            context.SetCount(report, total: ProgressReports);
        }

        return Task.CompletedTask;
    }

    private static Task WarnBeyondKept(TaskContext context)
    {
        foreach (var warning in _warnings)
        {
            context.AddWarning(warning);
        }

        return Task.CompletedTask;
    }

    private static Task WaitAndEndWaiting(TaskContext context)
    {
        foreach (var text in _waitingTexts)
        {
            using (context.BeginWaiting(text))
            {
                context.SetCount(1);
            }
        }

        return Task.CompletedTask;
    }

    private static string[] Texts(string prefix, int count)
        => [.. Enumerable.Range(1, count).Select(index => $"{prefix} {index}")];
}
