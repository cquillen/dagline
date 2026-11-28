using System.Diagnostics;
using System.Globalization;
using System.Text;
using Dagline;
using Dagline.Prototype;

// Question B: what a run costs on the thread pool, the engine in src/Dagline (through Runner.RunAsync) against the prototype's
// loop, on graphs shaped like those of benchmarks/Dagline.Benchmarks/RunBenchmarks.cs. Old and new runs are interleaved, and
// which goes first alternates, so that whatever else the machine is doing falls on both.
//
//   dotnet run -c Release --project prototype/Dagline.Prototype.Bench [iterations]

var iterations = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 15;
const int Warmups = 3;

// The second argument sets the max running tasks of both engines (16 by default, as in RunOptions). With 1, no two bodies ever
// run at once, so nothing contends for the old engine's lock: what is left is the cost of each call.
Scenarios.MaxRunningTasks = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 16;

Scenario[] scenarios =
[
    Scenarios.BeginAndEndWaiting(),
    Scenarios.WarningsBeyondKept(),
    Scenarios.FrequentProgress(),
    Scenarios.LargeGraph(),
    Scenarios.OneTaskManyWaits(),
    Scenarios.OnAJournalFile(),
];

var report = new StringBuilder();
report.AppendLine(CultureInfo.InvariantCulture, $"{iterations} interleaved iterations after {Warmups} warm-ups, Release, {Environment.ProcessorCount} logical processors, .NET {Environment.Version}, max running tasks {Scenarios.MaxRunningTasks}");
report.AppendLine();
report.AppendLine("What each engine handed its reporters, from one run each (events, and the lines and characters the plain-text reporter wrote):");
report.AppendLine();
report.AppendLine("| scenario | old: events / lines / chars | new: events / lines / chars |");
report.AppendLine("|---|---|---|");
foreach (var scenario in scenarios)
{
    Scenarios.Watch = new Watch();
    await scenario.Old();
    var oldSaw = Scenarios.Watch.Describe();
    Scenarios.Watch = new Watch();
    await scenario.New();
    var newSaw = Scenarios.Watch.Describe();
    Scenarios.Watch = null;
    report.AppendLine(CultureInfo.InvariantCulture, $"| {scenario.Name} | {oldSaw} | {newSaw} |");
}

report.AppendLine();
report.AppendLine("| scenario | engine | wall ms: min / median / max | allocated MB: min / median / max | inputs posted | most queued at once | times the loop was woken |");
report.AppendLine("|---|---|---|---|---|---|---|");

foreach (var scenario in scenarios)
{
    Console.Error.WriteLine($"{scenario.Name}...");
    for (var warmup = 0; warmup < Warmups; warmup++)
    {
        await scenario.Check(await scenario.Old(), "old");
        await scenario.Check(await scenario.New(), "new");
    }

    List<Sample> old = [];
    List<Sample> @new = [];
    for (var iteration = 0; iteration < iterations; iteration++)
    {
        if (iteration % 2 == 0)
        {
            old.Add(await Measure(scenario.Old));
            @new.Add(await Measure(scenario.New));
        }
        else
        {
            @new.Add(await Measure(scenario.New));
            old.Add(await Measure(scenario.Old));
        }
    }

    report.AppendLine(Row(scenario.Name, "old (lock)", old));
    report.AppendLine(Row(scenario.Name, "new (loop)", @new));
}

Console.WriteLine(report);
Scenarios.DeleteJournals();

static async Task<Sample> Measure(Func<Task<Ran>> run)
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    var before = GC.GetTotalAllocatedBytes(precise: true);
    var watch = Stopwatch.StartNew();
    var ran = await run();
    watch.Stop();
    var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
    if (ran.Outcome is not RunOutcome.Complete)
    {
        throw new InvalidOperationException($"A measured run ended {ran.Outcome}.");
    }

    return new Sample(watch.Elapsed.TotalMilliseconds, allocated / (1024.0 * 1024.0), ran.Posted, ran.Peak, ran.Wakes);
}

static string Row(string scenario, string engine, List<Sample> samples)
{
    var ms = samples.Select(sample => sample.Milliseconds).Order().ToArray();
    var mb = samples.Select(sample => sample.Megabytes).Order().ToArray();
    var posted = samples.Max(sample => sample.Posted);
    var peaks = samples.Select(sample => sample.Peak).Order().ToArray();
    return string.Create(
        CultureInfo.InvariantCulture,
        $"| {scenario} | {engine} | {ms[0]:F1} / {ms[ms.Length / 2]:F1} / {ms[^1]:F1} | {mb[0]:F2} / {mb[mb.Length / 2]:F2} / {mb[^1]:F2} "
        + $"| {(posted == 0 ? "-" : posted.ToString("N0", CultureInfo.InvariantCulture))} "
        + $"| {(posted == 0 ? "-" : $"{peaks[0]:N0} to {peaks[^1]:N0}")} "
        + $"| {(posted == 0 ? "-" : $"{samples.Min(sample => sample.Wakes):N0} to {samples.Max(sample => sample.Wakes):N0}")} |");
}

internal sealed record Sample(double Milliseconds, double Megabytes, int Posted, int Peak, int Wakes);

internal sealed record Ran(RunOutcome Outcome, int Warnings, int ReporterErrors, int Posted = 0, int Peak = 0, int Wakes = 0);

internal sealed record Scenario(string Name, Func<Task<Ran>> Old, Func<Task<Ran>> New, int ExpectedWarnings)
{
    // A scenario that does not do what it says would measure nothing.
    public Task Check(Ran ran, string engine)
    {
        if (ran.Outcome is not RunOutcome.Complete || ran.ReporterErrors > 0 || ran.Warnings != ExpectedWarnings)
        {
            throw new InvalidOperationException(
                $"{Name} on the {engine} engine ended {ran.Outcome} with {ran.Warnings} warnings (expected {ExpectedWarnings}) and {ran.ReporterErrors} reporter errors.");
        }

        return Task.CompletedTask;
    }
}

/// <summary>Counts what a run hands its reporters, and keeps what the plain-text reporter writes.</summary>
internal sealed class Watch : IReporter
{
    private int _events;

    public StringWriter Text { get; } = new();

    public ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken)
    {
        _events++;
        return ValueTask.CompletedTask;
    }

    public string Describe()
    {
        var text = Text.ToString();
        var lines = text.Count(character => character == (char)10);
        return string.Create(CultureInfo.InvariantCulture, $"{_events:N0} / {lines:N0} / {text.Length:N0}");
    }
}

internal static class Scenarios
{
    public static int MaxRunningTasks { get; set; } = 16;

    /// <summary>When set, the next run is watched instead of written to nowhere.</summary>
    public static Watch? Watch { get; set; }

    private static IReporter[] Reporters() => Watch is { } watch ? [new PlainTextReporter(watch.Text), watch] : [new PlainTextReporter(TextWriter.Null)];

    private const int ProgressReports = 200_000;
    private const int WarningsPerTask = 250;
    private const int WaitsPerTask = 4;
    private const int ManyWaits = 50_000;

    private static readonly string[] _statusTexts = Texts("working on item", 1_000);
    private static readonly string[] _warnings = Texts("warning about item", WarningsPerTask);
    private static readonly string[] _waitingTexts = Texts("external job", WaitsPerTask);
    private static readonly string _journals = Directory.CreateTempSubdirectory("dagline-prototype-bench-").FullName;

    public static void DeleteJournals() => Directory.Delete(_journals, recursive: true);

    /// <summary>1,024 tasks, 3 groups deep, each beginning and ending waiting 4 times.</summary>
    public static Scenario BeginAndEndWaiting() => Both(
        "BeginAndEndWaiting (1,024 tasks x 4 waits)",
        body => Nested([4, 4, 4], tasksPerGroup: 16, chained: false, body),
        context =>
        {
            foreach (var text in _waitingTexts)
            {
                using (context.BeginWaiting(text))
                {
                    context.SetCount(1);
                }
            }

            return Task.CompletedTask;
        },
        context =>
        {
            foreach (var text in _waitingTexts)
            {
                using (context.BeginWaiting(text))
                {
                    context.SetCount(1);
                }
            }

            return Task.CompletedTask;
        });

    /// <summary>256 tasks, 3 groups deep, each given 250 warnings when 100 are kept.</summary>
    public static Scenario WarningsBeyondKept() => Both(
        "WarningsBeyondKept (256 tasks x 250 warnings)",
        body => Nested([4, 4, 4], tasksPerGroup: 4, chained: false, body),
        context =>
        {
            foreach (var warning in _warnings)
            {
                context.AddWarning(warning);
            }

            return Task.CompletedTask;
        },
        context =>
        {
            foreach (var warning in _warnings)
            {
                context.AddWarning(warning);
            }

            return Task.CompletedTask;
        },
        expectedWarnings: 256 * WarningsPerTask);

    /// <summary>One task, 3 groups deep, that sets its status text and its count 200,000 times each.</summary>
    public static Scenario FrequentProgress() => Both(
        "FrequentProgress (1 task x 200,000 reports)",
        body => Nested([1, 1, 1], tasksPerGroup: 1, chained: false, body),
        context =>
        {
            for (var report = 0; report < ProgressReports; report++)
            {
                context.SetStatusText(_statusTexts[report % _statusTexts.Length]);
                context.SetCount(report, total: ProgressReports);
            }

            return Task.CompletedTask;
        },
        context =>
        {
            for (var report = 0; report < ProgressReports; report++)
            {
                context.SetStatusText(_statusTexts[report % _statusTexts.Length]);
                context.SetCount(report, total: ProgressReports);
            }

            return Task.CompletedTask;
        });

    /// <summary>2,048 tasks beneath groups nested 4 deep, each requiring the one before it, reporting a little and succeeding.</summary>
    public static Scenario LargeGraph() => Both(
        "LargeGraph (2,048 chained tasks)",
        body => Nested([4, 4, 4, 4], tasksPerGroup: 8, chained: true, body),
        OldReportAndSucceed,
        NewReportAndSucceed);

    /// <summary>Not in RunBenchmarks: one task that begins and ends waiting 50,000 times in a loop, to see what the inbox holds.</summary>
    public static Scenario OneTaskManyWaits() => Both(
        "OneTaskManyWaits (1 task x 50,000 waits)",
        body => Nested([1], tasksPerGroup: 1, chained: false, body),
        context =>
        {
            for (var wait = 0; wait < ManyWaits; wait++)
            {
                context.BeginWaiting().Dispose();
            }

            return Task.CompletedTask;
        },
        context =>
        {
            for (var wait = 0; wait < ManyWaits; wait++)
            {
                context.BeginWaiting().Dispose();
            }

            return Task.CompletedTask;
        });

    /// <summary>160 tasks on a new journal file, each settle flushed to the disk.</summary>
    public static Scenario OnAJournalFile()
    {
        var oldGraph = Sharded(OldReportAndSucceed);
        var bodies = new ProtoBodies();
        var newGraph = Sharded(bodies.Of(NewReportAndSucceed));
        return new Scenario(
            "OnAJournalFile (160 tasks, every settle flushed)",
            () => RunOld(oldGraph, Journal.New(_journals)),
            () => RunNew(newGraph, bodies, FileJournal.New(_journals, TimeProvider.System)),
            ExpectedWarnings: 0);

        static Graph Sharded(TaskBody body)
        {
            var builder = new GraphBuilder();
            for (var shard = 1; shard <= 4; shard++)
            {
                var group = builder.Group($"shard-{shard}");
                for (var tenant = 1; tenant <= 40; tenant++)
                {
                    group.Task($"tenant-{tenant}", body);
                }
            }

            return builder.Build();
        }
    }

    private static Scenario Both(string name, Func<TaskBody, Graph> shape, TaskBody oldBody, ProtoBody newBody, int expectedWarnings = 0)
    {
        var oldGraph = shape(oldBody);
        var bodies = new ProtoBodies();
        var newGraph = shape(bodies.Of(newBody));
        return new Scenario(name, () => RunOld(oldGraph, Journal.None), () => RunNew(newGraph, bodies, journal: null), expectedWarnings);
    }

    private static async Task<Ran> RunOld(Graph graph, Journal journal)
    {
        var result = await Runner.RunAsync(graph, new RunOptions
        {
            Journal = journal,
            MaxRunningTasks = MaxRunningTasks,
            Reporters = Reporters(),
        }).ConfigureAwait(false);

        return new Ran(result.Outcome, result.Snapshot.WarningCount, result.ReporterErrors.Count);
    }

    private static async Task<Ran> RunNew(Graph graph, ProtoBodies bodies, IJournalWriter? journal)
    {
        var run = ProtoRun.Start(graph, bodies, new ProtoOptions
        {
            Journal = journal,
            MaxRunningTasks = MaxRunningTasks,
            Reporters = Reporters(),
        });
        var result = await run.Result.ConfigureAwait(false);

        return new Ran(result.Outcome, result.Snapshot.WarningCount, result.ReporterErrors.Count, run.Inbox.Posted, run.Inbox.Peak, run.Inbox.Wakes);
    }

    private static Task OldReportAndSucceed(TaskContext context)
    {
        context.SetStatusText(_statusTexts[0]);
        context.SetCount(1, total: 1);
        context.SetMessage("done");

        return Task.CompletedTask;
    }

    private static Task NewReportAndSucceed(ProtoContext context)
    {
        context.SetStatusText(_statusTexts[0]);
        context.SetCount(1, total: 1);
        context.SetMessage("done");

        return Task.CompletedTask;
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
                TaskNode? previous = null;
                for (var index = 1; index <= tasksPerGroup; index++)
                {
                    var task = group.Task($"task-{index}", body);
                    if (chained && previous is not null)
                    {
                        task.Requires(previous);
                    }

                    previous = task;
                }

                continue;
            }

            var inner = Enumerable.Range(1, groupsPerLevel[0]).Select(index => group.Group($"group-{index}"));
            AddLevels(inner, groupsPerLevel[1..], tasksPerGroup, chained, body);
        }
    }

    private static string[] Texts(string prefix, int count)
        => [.. Enumerable.Range(1, count).Select(index => $"{prefix} {index}")];
}
