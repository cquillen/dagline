using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;

namespace Dagline.Prototype.Tests;

/// <summary>Records every event it is handed, as one line each, with the thread that delivered it.</summary>
internal sealed class Recorder : IReporter
{
    private readonly ConcurrentQueue<(string Line, RunEvent Event, Snapshot Snapshot, int Thread)> _events = new();

    public Action<RunEvent, Snapshot>? OnEvent { get; set; }

    public string[] Lines => [.. _events.Select(recorded => recorded.Line)];

    public RunEvent[] Events => [.. _events.Select(recorded => recorded.Event)];

    public int[] Threads => [.. _events.Select(recorded => recorded.Thread).Distinct()];

    public ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken)
    {
        _events.Enqueue((Describe(runEvent), runEvent, snapshot, Environment.CurrentManagedThreadId));
        OnEvent?.Invoke(runEvent, snapshot);
        return ValueTask.CompletedTask;
    }

    public static string Describe(RunEvent runEvent) => runEvent switch
    {
        RunStarted => "run started",
        TaskStatusChanged changed => $"{changed.Node.Path}: {Lower(changed.From)} -> {Lower(changed.To)}",
        WarningAdded added => $"warning {added.Path}: {added.Warning}",
        WaitingChanged { Waiting: true, Text: null } began => $"waiting {began.Path}",
        WaitingChanged { Waiting: true } began => $"waiting {began.Path}: {began.Text}",
        WaitingChanged ended => $"waited {ended.Path}",
        GroupSettled settled => $"group {settled.Path} settled: {Lower(settled.Status)}",
        DrainRequested => "drain requested",
        StopRequested stop => $"stop requested by {stop.By}",
        GraceEnded => "grace period over",
        RunEnded ended => $"run ended: {ProtoOutcome.Name(ended.Outcome).ToLowerInvariant()}",
        _ => runEvent.ToString(),
    };

    private static string Lower<T>(T value)
        where T : struct, Enum
        => value.ToString().ToLowerInvariant();
}

/// <summary>A point a body waits at until the test opens it.</summary>
internal sealed class Gate(TaskCreationOptions options = TaskCreationOptions.None)
{
    private readonly TaskCompletionSource _source = new(options);

    public Task Task => _source.Task;

    public void Open() => _source.SetResult();
}

/// <summary>One scripted run on a manual host, a fake clock and a journal in memory.</summary>
internal sealed class Scenario(BodyMode mode = BodyMode.SynchronizationContext)
{
    public GraphBuilder Builder { get; } = new();

    public ProtoBodies Bodies { get; } = new();

    public ManualHost Host { get; } = new(mode);

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));

    public MemoryJournal Journal { get; } = new();

    public Recorder Reporter { get; } = new();

    public ProtoRun Run { get; private set; } = null!;

    public string[] Events => Reporter.Lines;

    public TaskNode Task(string name, ProtoBody body) => Builder.Task(name, Bodies.Of(body));

    public ProtoRun Start(
        TimeSpan? gracePeriod = null,
        bool journal = true,
        TimeSpan? reporterWait = null,
        IReporter[]? reporters = null,
        CancellationToken cancellationToken = default)
    {
        Run = ProtoRun.Start(
            Builder.Build(),
            Bodies,
            new ProtoOptions
            {
                GracePeriod = gracePeriod ?? TimeSpan.FromSeconds(30),
                ReporterWait = reporterWait ?? TimeSpan.FromSeconds(30),
                TimeProvider = Clock,
                Reporters = reporters ?? [Reporter],
                Journal = journal ? Journal : null,
            },
            Host,
            cancellationToken);
        return Run;
    }

    public ProtoResult Result
    {
        get
        {
            Run.Result.IsCompleted.ShouldBeTrue("the run has not returned its result");
            return Run.Result.GetAwaiter().GetResult();
        }
    }
}
