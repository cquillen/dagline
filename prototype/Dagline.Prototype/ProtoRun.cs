namespace Dagline.Prototype;

internal sealed record ProtoOptions
{
    public int MaxRunningTasks { get; init; } = 16;

    public IReadOnlyDictionary<string, int> Limits { get; init; } = new Dictionary<string, int>();

    public TimeSpan GracePeriod { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the run waits at its end for its reporters, on the run's clock. Infinite for no limit.</summary>
    public TimeSpan ReporterWait { get; init; } = TimeSpan.FromSeconds(30);

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public IReadOnlyList<IReporter> Reporters { get; init; } = [];

    /// <summary><see langword="null"/> for a run with no journal.</summary>
    public IJournalWriter? Journal { get; init; }
}

/// <summary>What a run returns. A stand-in for RunResult, with the failed write beside the outcome.</summary>
internal sealed record ProtoResult(RunOutcome Outcome, Snapshot Snapshot, IReadOnlyList<ReporterError> ReporterErrors, Exception? JournalFailure);

/// <summary>
/// A run: the handle the app holds (ADR 0016), and the one loop that owns the run (ADR 0015). Everything else posts to its inbox.
/// </summary>
internal sealed class ProtoRun : ISnapshotSource
{
    private readonly Graph _graph;
    private readonly ProtoBody[] _bodyAt;
    private readonly ProtoOptions _options;
    private readonly IRunHost _host;
    private readonly TimeProvider _clock;
    private readonly Inbox _inbox;
    private readonly RunCore _core;
    private readonly Reporters _reporters;
    private readonly IJournalWriter? _journal;
    private readonly CancellationToken _appToken;
    private readonly CancellationTokenSource _tasksTokenSource = new();
    private readonly TaskCompletionSource<ProtoResult> _result;

    private ITimer? _graceTimer;
    private bool _stale;

    // Written by the loop alone, read by anyone. The running tasks are published with the snapshot so a reader can sample them.
    private Snapshot _current;
    private ProtoTask[] _running = [];

    private ProtoRun(Graph graph, ProtoBodies bodies, ProtoOptions options, IRunHost host, CancellationToken appToken)
    {
        _graph = graph;
        _options = options;
        _host = host;
        _clock = options.TimeProvider;
        _journal = options.Journal;
        _appToken = appToken;
        _inbox = new Inbox(host);
        _result = new TaskCompletionSource<ProtoResult>(host.CompletionOptions);
        _bodyAt = new ProtoBody[graph.Nodes.Length];
        foreach (var task in graph.Tasks)
        {
            _bodyAt[task.Position] = bodies.BodyOf(task);
        }

        var settings = RunSettings.From(graph, new RunOptions
        {
            MaxRunningTasks = options.MaxRunningTasks,
            Limits = options.Limits,
            GracePeriod = options.GracePeriod,
            TimeProvider = options.TimeProvider,
            Journal = Journal.None,
        });
        _core = new RunCore(graph, settings, hasJournal: _journal is not null, this);
        _reporters = new Reporters(options.Reporters, host);

        // Before the loop's first turn, on the caller's thread: nothing else can read or change the core yet.
        _current = _core.TakeSnapshot();
    }

    /// <summary>Starts a run and returns it. Nothing of the run has happened yet when this returns: its first turn is the host's to run.</summary>
    public static ProtoRun Start(Graph graph, ProtoBodies bodies, ProtoOptions options, IRunHost? host = null, CancellationToken cancellationToken = default)
    {
        var run = new ProtoRun(graph, bodies, options, host ?? ThreadPoolHost.Instance, cancellationToken);
        run._host.RunLoop(() => _ = run.LoopAsync());
        return run;
    }

    public Task<ProtoResult> Result => _result.Task;

    /// <summary>The token every task sees.</summary>
    public CancellationToken TasksToken => _tasksTokenSource.Token;

    public Inbox Inbox => _inbox;

    public void Drain() => _inbox.Post(Input.Drain);

    public void Stop() => _inbox.Post(Input.Stop);

    /// <summary>
    /// The run as of the last event the loop published (or the last time it went idle after a change that is no event), with the
    /// progress of each running task as it is at this read.
    /// </summary>
    public Snapshot Current
    {
        get
        {
            var running = Volatile.Read(ref _running);
            var snapshot = Volatile.Read(ref _current);
            List<(int Index, NodeSnapshot Element)>? sampled = null;
            foreach (var task in running)
            {
                var node = snapshot.Nodes[task.Task.Position];
                SlotSample sample = default;

                // A task that started after this snapshot is running for the loop and not yet for the snapshot: left as it is.
                if (node.Status is not NodeStatus.Running || task.Slot is not { } slot || !slot.Read(ref sample))
                {
                    continue;
                }

                (sampled ??= []).Add((task.Task.Position, WithSample(node, task.Task, sample)));
            }

            if (sampled is null)
            {
                return snapshot;
            }

            return new Snapshot(
                snapshot.Started,
                snapshot.Ended,
                snapshot.Outcome,
                snapshot.Drain,
                snapshot.Stop,
                ((ChunkedList<NodeSnapshot>)snapshot.Nodes).With(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(sampled)),
                _graph.Positions,
                snapshot.Counts,
                snapshot.RunningTasks,
                snapshot.Limits,
                new SnapshotJournal(null));
        }
    }

    private async Task LoopAsync()
    {
        try
        {
            // Registered before the start is posted, so the stop of a token that is already cancelled is in the inbox first,
            // and the core holds it until the tasks have become ready.
            var hearsTheStop = _appToken.UnsafeRegister(static run => ((ProtoRun)run!)._inbox.Post(Input.Stop), this);
            _inbox.Post(Input.Start);

            Input? written = null;
            while (!_core.HasEnded)
            {
                Input input;
                if (written is { } result)
                {
                    input = result;
                    written = null;
                }
                else
                {
                    while (!_inbox.TryRead(out input))
                    {
                        RefreshIfStale();
                        await _inbox.WaitAsync();
                    }
                }

                written = CarryOut(_core.Apply(input, input.Kind is InputKind.Written ? default : _clock.GetUtcNow()));
            }

            hearsTheStop.Dispose();
            await WaitForReportersAsync();
            _inbox.Close();
            _result.TrySetResult(new ProtoResult(_core.Outcome!.Value, _current, _reporters.Errors(), _core.JournalFailure));
        }
        catch (Exception fault)
        {
            // A fault in the prototype: surface it, so that a test fails instead of hanging.
            _inbox.Close();
            _result.TrySetException(fault);
        }
    }

    /// <summary>Carries out the effects of one input, in order.</summary>
    /// <returns>The result of the write, when the last effect was a Record: it is the next input.</returns>
    private Input? CarryOut(IReadOnlyList<Effect> effects)
    {
        Input? written = null;
        for (var i = 0; i < effects.Count; i++)
        {
            var effect = effects[i];
            switch (effect.Kind)
            {
                case EffectKind.Record:
                    written = Input.Written(_journal!.TryWrite(effect.Entries!, effect.Flush));
                    break;
                case EffectKind.Publish:
                    var snapshot = _core.SnapshotAfter(effect.Change);
                    Volatile.Write(ref _running, _core.Running);
                    Volatile.Write(ref _current, snapshot);
                    _reporters.Publish(effect.Event!, snapshot);
                    break;
                case EffectKind.Start:
                    StartBody(effect.Task!, effect.Text!);
                    break;
                case EffectKind.SignalTasks:
                    _host.SignalTasks(_tasksTokenSource);
                    break;
                case EffectKind.ArmGrace:
                    _graceTimer = _clock.CreateTimer(
                        static run => ((ProtoRun)run!)._inbox.Post(Input.GraceElapsed),
                        this,
                        effect.Period,
                        Timeout.InfiniteTimeSpan);
                    break;
                case EffectKind.CompleteSave:
                    Answer(effect);
                    break;
                case EffectKind.CloseJournal:
                    _graceTimer?.Dispose();
                    _journal?.Close();
                    break;
                case EffectKind.Stale:
                    _stale = true;
                    break;
            }
        }

        return written;
    }

    private void Answer(in Effect effect)
    {
        switch (effect.Answer)
        {
            case SaveAnswer.Saved:
                effect.Save!.Saved(effect.Text!);
                break;
            case SaveAnswer.NotRunning:
                effect.Save!.Refused(SaveRefusals.NotRunning(effect.Task!));
                break;
            default:
                effect.Save!.Refused(new JournalWriteException(effect.Cause!, _current));
                break;
        }
    }

    private void StartBody(BuiltTask task, string checkpoint)
    {
        var context = new ProtoContext(
            _inbox,
            task,
            _core.SlotOf(task),
            checkpoint,
            _clock,
            _host.CompletionOptions,
            _tasksTokenSource.Token);
        _host.StartBody(() => RunBodyAsync(task, context));
    }

    private async Task RunBodyAsync(BuiltTask task, ProtoContext context)
    {
        TaskOutcome outcome;
        try
        {
            await _bodyAt[task.Position](context);
            outcome = TaskOutcome.Succeeded;
        }
        catch (Exception escaped)
        {
            outcome = TaskOutcome.UnexpectedFailure(escaped);
        }

        _inbox.Post(Input.BodyEnded(task, outcome));
    }

    // A change that is no event was applied, and the inbox is empty: build the snapshot now, once for however many there were.
    private void RefreshIfStale()
    {
        if (_stale)
        {
            _stale = false;
            var snapshot = _core.TakeSnapshot();
            Volatile.Write(ref _running, _core.Running);
            Volatile.Write(ref _current, snapshot);
        }
    }

    /// <summary>
    /// Waits for reporters for at most the reporter wait, on the run's clock, then drops those still busy. The loop goes on
    /// reading its inbox while it waits: that the reporters are done, and that the wait ran out, are posted like anything else,
    /// and a save that arrives now is still answered.
    /// </summary>
    private async Task WaitForReportersAsync()
    {
        var wait = _options.ReporterWait == Timeout.InfiniteTimeSpan
            ? null
            : _clock.CreateTimer(
                static run => ((ProtoRun)run!)._inbox.Post(Input.ReporterWaitElapsed),
                this,
                _options.ReporterWait,
                Timeout.InfiniteTimeSpan);
        _reporters.Complete(() => _inbox.Post(Input.ReportersDone));
        while (true)
        {
            Input input;
            while (!_inbox.TryRead(out input))
            {
                await _inbox.WaitAsync();
            }

            if (input.Kind is InputKind.ReportersDone)
            {
                break;
            }

            if (input.Kind is InputKind.ReporterWaitElapsed)
            {
                _reporters.DropTheBusy();
                continue;
            }

            CarryOut(_core.Apply(input, default));
        }

        wait?.Dispose();
    }

    private static NodeSnapshot WithSample(NodeSnapshot node, BuiltTask task, SlotSample sample) => new(task, node.Status, node.Counts)
    {
        Failure = node.Failure,
        BlockedBy = node.BlockedBy,
        Message = node.Message,
        Detail = node.Detail,
        DetailOmitted = node.Detail.Count >= TaskSlot.Kept ? sample.DetailBeyondKept : 0,
        Warnings = node.Warnings,
        WarningsOmitted = node.Warnings.Count >= TaskSlot.Kept ? sample.WarningsBeyondKept : 0,
        StatusText = sample.StatusText,
        Count = sample.Count,
        Total = sample.Total,
        WaitingSince = node.WaitingSince,
        WaitingText = node.WaitingText,
        Started = node.Started,
        Settled = node.Settled,
    };
}
