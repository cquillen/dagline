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

    private static readonly string[] _statusTexts = Texts("working on item", 1_000);
    private static readonly string[] _warnings = Texts("warning about item", WarningsPerTask);
    private static readonly string[] _waitingTexts = Texts("external job", WaitsPerTask);

    private Graph _largeGraph = null!;
    private Graph _progress = null!;
    private Graph _warningsBeyondKept = null!;
    private Graph _waiting = null!;

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

        await Check(_largeGraph, expectedWarnings: 0).ConfigureAwait(false);
        await Check(_progress, expectedWarnings: 0).ConfigureAwait(false);
        await Check(_warningsBeyondKept, expectedWarnings: 256 * WarningsPerTask).ConfigureAwait(false);
        await Check(_waiting, expectedWarnings: 0).ConfigureAwait(false);
    }

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

    private static async Task<RunOutcome> Run(Graph graph)
    {
        var result = await Runner.RunAsync(graph, Options()).ConfigureAwait(false);

        return result.Outcome;
    }

    private static RunOptions Options() => new()
    {
        Journal = Journal.None,
        Reporters = [new PlainTextReporter(TextWriter.Null)],
    };

    // A scenario that does not do what it says would measure nothing, so each is run once and checked before it is measured.
    private static async Task Check(Graph graph, int expectedWarnings)
    {
        var result = await Runner.RunAsync(graph, Options()).ConfigureAwait(false);

        if (result.Outcome is not RunOutcome.Complete || result.ReporterErrors.Count > 0)
        {
            throw new InvalidOperationException($"The scenario ended {result.Outcome} with {result.ReporterErrors.Count} reporter errors.");
        }

        if (result.Snapshot.WarningCount != expectedWarnings)
        {
            throw new InvalidOperationException($"The scenario gave {result.Snapshot.WarningCount} warnings, not {expectedWarnings}.");
        }
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
        context.Status(_statusTexts[0]);
        context.Count(1, total: 1);
        context.Message("done");

        return Task.CompletedTask;
    }

    private static Task ReportProgressOften(TaskContext context)
    {
        for (var report = 0; report < ProgressReports; report++)
        {
            context.Status(_statusTexts[report % _statusTexts.Length]);
            context.Count(report, total: ProgressReports);
        }

        return Task.CompletedTask;
    }

    private static Task WarnBeyondKept(TaskContext context)
    {
        foreach (var warning in _warnings)
        {
            context.Warn(warning);
        }

        return Task.CompletedTask;
    }

    private static Task WaitAndEndWaiting(TaskContext context)
    {
        foreach (var text in _waitingTexts)
        {
            using (context.Waiting(text))
            {
                context.Count(1);
            }
        }

        return Task.CompletedTask;
    }

    private static string[] Texts(string prefix, int count)
        => [.. Enumerable.Range(1, count).Select(index => $"{prefix} {index}")];
}
