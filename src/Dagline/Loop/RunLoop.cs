using System.Diagnostics.CodeAnalysis;

namespace Dagline;

/// <summary>
/// The one loop that owns a run on the new engine, from its start until its result is returned (ADR 0015). It is the only reader
/// of the run's inbox and the only code that touches the run's state: it takes one input at a time, asks the step core what it
/// means, and carries out the effects the core returns, in the order given, deciding nothing.
/// </summary>
/// <remarks>
/// <para>
/// The run's life is the loop's control flow: read, decide, carry out, repeat; then, once the core has ended the run, wait for
/// the reporters, still reading the inbox, and return. The loop awaits only its inbox: the reporters being done reaches it as an
/// input. It reads the run's clock once for each input it takes from the inbox, and the result of a write, the next input after a
/// record, is given to the core with no time, ahead of anything in the inbox.
/// </para>
/// <para>
/// Each turn of the loop is run by the host: the first when the run starts, and each after the loop finds its inbox empty, when an
/// input is posted. A fault inside the loop faults the run's result and closes the inbox.
/// </para>
/// </remarks>
[SuppressMessage(
    "Reliability",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The source of the tasks' token has no timer and no wait handle is ever taken from it, and a body the run has ended may still hold its token.")]
internal sealed class RunLoop
{
    private readonly IRunHost _host;
    private readonly TimeProvider _clock;
    private readonly JournalFile? _journal;
    private readonly StepCore _core;
    private readonly HostedDelivery _delivery;
    private readonly LoopTaskRun _taskRun;
    private readonly CancellationTokenSource _tasksTokenSource = new();
    private readonly TaskCompletionSource<RunResult> _result;

    private RunOutcome? _outcome;
    private bool _currentIsOutOfDate;

    /// <param name="graph">The graph to run.</param>
    /// <param name="settings">The settings of the run.</param>
    /// <param name="reporters">The run's reporters.</param>
    /// <param name="journal">
    /// The journal the run is on, already held, opened and read, or <see langword="null"/> for none. The loop owns it from here, and
    /// closes it before the run's end is published.
    /// </param>
    /// <param name="host">What runs things for the loop.</param>
    public RunLoop(Graph graph, RunSettings settings, IReadOnlyList<IReporter> reporters, JournalFile? journal, IRunHost host)
    {
        _host = host;
        _clock = settings.TimeProvider;
        _journal = journal;
        Inbox = new Inbox(host);
        _delivery = new HostedDelivery(reporters, host);
        _result = new TaskCompletionSource<RunResult>(host.CompletionOptions);
        _core = new StepCore(graph, settings, journal?.Fold, Source);
        _taskRun = new LoopTaskRun(Inbox, _core.Samples);

        // Before the loop's first turn, on the starting thread: nothing else can read or change the core yet.
        Source.Publish(_core.TakeSnapshot());
    }

    /// <summary>The run's inbox: the only way into the run.</summary>
    public Inbox Inbox { get; }

    /// <summary>The run's current snapshot, which the loop writes and anyone may read.</summary>
    public CurrentSnapshot Source { get; } = new();

    /// <summary>
    /// Completes with the run's result once the run has ended and every reporter has handled every event or been dropped, or faults
    /// with what faulted the loop.
    /// </summary>
    public Task<RunResult> Result => _result.Task;

    /// <summary>Posts the run's start and has the host run the loop's first turn.</summary>
    public void Start()
    {
        Inbox.Post(Input.Start);
        _host.RunLoop(() => _ = RunAsync());
    }

    private async Task RunAsync()
    {
        try
        {
            Input? written = null;
            while (true)
            {
                Input input;
                var now = default(DateTimeOffset);
                if (written is { } result)
                {
                    input = result;
                    written = null;
                }
                else
                {
                    while (!Inbox.TryTake(out input))
                    {
                        RebuildTheCurrentIfOutOfDate();
                        await Inbox.WaitAsync();
                    }

                    now = _clock.GetUtcNow();
                }

                if (input.Kind is InputKind.ReportersDone)
                {
                    break;
                }

                written = CarryOut(_core.Apply(input, now));
            }

            Inbox.Close();
            _result.TrySetResult(new RunResult(_outcome!.Value, Source.Current, _delivery.Errors));
        }
        catch (Exception fault)
        {
            // A fault in Dagline itself: the app awaiting the result is told, rather than left waiting for ever.
            Inbox.Close();
            _result.TrySetException(fault);
        }
    }

    /// <summary>Carries out the effects of one input, in order.</summary>
    /// <returns>The result of the write, when the last effect was a record: it is the next input.</returns>
    private Input? CarryOut(IReadOnlyList<Effect> effects)
    {
        Input? written = null;

        // Indexed, not enumerated, so that no enumerator is allocated for each input.
        for (var i = 0; i < effects.Count; i++)
        {
            var effect = effects[i];
            switch (effect.Kind)
            {
                case EffectKind.Record:
                    written = Write(effect.Entries!, effect.Flush);
                    break;
                case EffectKind.Publish:
                    Publish(effect.Event!, effect.Change);
                    break;
                case EffectKind.StartBody:
                    StartBody(effect.Task!);
                    break;
                case EffectKind.CloseJournal:
                    _journal!.Dispose();
                    break;
                case EffectKind.EndRun:
                    End(effect.Outcome);
                    break;
                case EffectKind.MarkCurrentOutOfDate:
                    _currentIsOutOfDate = true;
                    break;
            }
        }

        return written;
    }

    private Input Write(IReadOnlyList<JournalEntry> entries, JournalFlush flush)
    {
        try
        {
            _journal!.Append(entries, flush);
            return Input.Written(null);
        }
        catch (Exception cause) when (FileSystemFailure.Is(cause))
        {
            return Input.Written(cause);
        }
    }

    /// <summary>
    /// Builds the snapshot just after an event, makes it the current one, then hands the event to the reporters, so a reporter
    /// never reads a current snapshot older than an event it holds.
    /// </summary>
    private void Publish(RunEvent runEvent, SnapshotChange change)
    {
        var snapshot = _core.SnapshotAfter(change);
        _currentIsOutOfDate = false;
        Source.Publish(snapshot);
        _delivery.Publish(runEvent, snapshot);
    }

    /// <summary>
    /// Makes the run as it is now the current snapshot, if something that is not an event has changed it since the last event.
    /// Called only once the inbox is empty, so a burst of such inputs costs one snapshot.
    /// </summary>
    private void RebuildTheCurrentIfOutOfDate()
    {
        if (_currentIsOutOfDate)
        {
            _currentIsOutOfDate = false;
            Source.Publish(_core.TakeSnapshot());
        }
    }

    private void StartBody(BuiltTask task)
    {
        var context = new TaskContext(_taskRun, task, _clock, _tasksTokenSource.Token);
        _host.StartBody(() => RunBodyAsync(task, context));
    }

    /// <summary>
    /// Runs a task's body, then posts its end. This is the one place a body is invoked on the new engine, and so the one catch-all
    /// for it: whatever escapes the body is a failure of that task and of nothing else.
    /// </summary>
    private async Task RunBodyAsync(BuiltTask task, TaskContext context)
    {
        TaskOutcome outcome;
        try
        {
            // On the context the host started the body on, so that the end of a body under a host a test pumps comes back to the
            // pump, however the body's own awaits resumed. In an app a body starts on the thread pool, with no context to keep.
            await task.Body(context).ConfigureAwait(true);
            outcome = TaskOutcome.Succeeded;
        }
        catch (Exception escaped)
        {
            outcome = TaskOutcome.UnexpectedFailure(escaped);
        }

        Inbox.Post(Input.BodyEnded(task, outcome));
    }

    /// <summary>The run has ended: no event follows, and the loop waits for the reporters to be done, as an input.</summary>
    private void End(RunOutcome outcome)
    {
        _outcome = outcome;
        _delivery.Complete(() => Inbox.Post(Input.ReportersDone));
    }
}
