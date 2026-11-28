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
/// input. It reads the run's clock once for each timed input it takes from the inbox (see <see cref="Input.IsTimed"/>), and the
/// result of a write, the next input after a record, is given to the core with no time, ahead of anything in the inbox.
/// </para>
/// <para>
/// Each turn of the loop is run by the host: the first when the run starts, and each after the loop finds its inbox empty, when an
/// input is posted. A fault inside the loop faults the run's result and closes the inbox.
/// </para>
/// </remarks>
[SuppressMessage(
    "Reliability",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The source of the tasks' token has no timer and no wait handle is ever taken from it, and a body the run has ended may still hold its token. The grace period's timer is disposed when the run ends.")]
internal sealed class RunLoop
{
    private readonly IRunHost _host;
    private readonly TimeProvider _clock;
    private readonly JournalFile? _journal;
    private readonly StepCore _core;
    private readonly HostedDelivery _delivery;
    private readonly LoopTaskRun _taskRun;
    private readonly TaskCheckpoints _checkpoints;
    private readonly CancellationTokenSource _tasksTokenSource = new();
    private readonly TaskCompletionSource<RunResult> _result;
    private readonly TimeSpan _gracePeriod;

    private RunOutcome? _outcome;

    // The last save the loop took: the only one that can still be waiting for its answer, since the core answers a save in the
    // input that brings it or in the result of its write, which is the next input.
    private CheckpointSave? _lastSave;
    private bool _currentIsOutOfDate;
    private CancellationTokenRegistration _hearsTheStop;
    private ITimer? _gracePeriodTimer;

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
        _gracePeriod = settings.GracePeriod;
        _journal = journal;
        Inbox = new Inbox(host);
        _delivery = new HostedDelivery(reporters, host);
        _result = new TaskCompletionSource<RunResult>(host.CompletionOptions);
        var samples = new TaskSamples(graph);
        Source = new CurrentSnapshot(samples);
        _core = new StepCore(graph, settings, journal?.Fold, Source, samples);
        _checkpoints = new TaskCheckpoints(graph, _core.CheckpointOf);
        _taskRun = new LoopTaskRun(Inbox, samples, graph, Source, _checkpoints);

        // Before the loop's first turn, on the starting thread: nothing else can read or change the core yet.
        Source.Publish(_core.TakeSnapshot(), _core.Sampled);
    }

    /// <summary>The run's inbox: the only way into the run.</summary>
    public Inbox Inbox { get; }

    /// <summary>The run's current snapshot, which the loop writes and anyone may read.</summary>
    public CurrentSnapshot Source { get; }

    /// <summary>
    /// Completes with the run's result once the run has ended and every reporter has handled every event or been dropped, or faults
    /// with what faulted the loop.
    /// </summary>
    public Task<RunResult> Result => _result.Task;

    /// <summary>
    /// Posts the run's start and has the host run the loop's first turn. A token already cancelled, and a drain already asked for,
    /// go with the start; a later cancellation of the token posts a stop by the app, and the token is let go of once the run ends.
    /// </summary>
    /// <param name="drained">Whether a drain was already asked for, which only the engine switch can do until the cutover.</param>
    /// <param name="cancellationToken">The app's token, whose cancellation is a stop by the app.</param>
    public void Start(bool drained, CancellationToken cancellationToken)
    {
        var stopped = cancellationToken.IsCancellationRequested;
        Inbox.Post(Input.StartAsked(drained, stopped));
        if (!stopped)
        {
            // Posting runs no engine code, so the callback is safe on whichever thread cancels the token. Registered before the
            // first turn, so the loop that lets go of it at the end finds it set.
            _hearsTheStop = cancellationToken.UnsafeRegister(static loop => ((RunLoop)loop!).Stop(StopRequester.App), this);
        }

        _host.RunLoop(() => _ = RunAsync());
    }

    /// <summary>Asks the run to drain. Asked again, during a stop, or once the run has ended, it changes nothing.</summary>
    public void Drain() => Inbox.Post(Input.Drain);

    /// <summary>Asks the run to stop. Asked again, or once the run has ended, it changes nothing.</summary>
    public void Stop(StopRequester by) => Inbox.Post(Input.Stop(by));

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

                    // Kept as soon as it leaves the inbox, so that a fault from here on answers it.
                    if (input.Kind is InputKind.CheckpointSave)
                    {
                        _lastSave = input.Save;
                    }

                    if (input.IsTimed)
                    {
                        now = _clock.GetUtcNow();
                    }
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
            // A fault in Dagline itself: the app awaiting the result is told, and so is a task awaiting a save, rather than either
            // being left waiting for ever. Closing the inbox refuses the saves still queued.
            _lastSave?.Refuse(fault);
            Inbox.Close();
            LetGoOfTheTokenAndTheTimer();
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
                case EffectKind.SignalTheTasks:
                    _host.Signal(_tasksTokenSource);
                    break;
                case EffectKind.StartGracePeriod:
                    _gracePeriodTimer = _clock.CreateTimer(
                        static loop => ((RunLoop)loop!).Inbox.Post(Input.GracePeriodOver),
                        this,
                        _gracePeriod,
                        Timeout.InfiniteTimeSpan);
                    break;
                case EffectKind.EndRun:
                    End(effect.Outcome);
                    break;
                case EffectKind.MarkCurrentOutOfDate:
                    _currentIsOutOfDate = true;
                    break;
                case EffectKind.AnswerSave:
                    AnswerSave(effect.Task!, effect.Save!, effect.Refusal);
                    break;
            }
        }

        return written;
    }

    /// <summary>
    /// Answers a save: a made save's text is what the task's checkpoint reads back from before the answer resumes the task, and the
    /// answer resumes it as the host says, never inline on the loop in an app.
    /// </summary>
    private void AnswerSave(BuiltTask task, CheckpointSave save, Exception? refusal)
    {
        if (refusal is not null)
        {
            save.Refuse(refusal);
            return;
        }

        _checkpoints.Saved(task, save.Text);
        save.Made();
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
        Source.Publish(snapshot, _core.Sampled);
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
            Source.Publish(_core.TakeSnapshot(), _core.Sampled);
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

    /// <summary>
    /// Lets go of the app's token and the grace period's timer, which mean nothing once the run has ended. Neither waits for a
    /// callback that is running: each only posts, and the inbox takes a post after the end and drops one after the loop has gone.
    /// </summary>
    private void LetGoOfTheTokenAndTheTimer()
    {
        _hearsTheStop.Unregister();
        _gracePeriodTimer?.Dispose();
    }

    /// <summary>The run has ended: no event follows, and the loop waits for the reporters to be done, as an input.</summary>
    private void End(RunOutcome outcome)
    {
        _outcome = outcome;
        LetGoOfTheTokenAndTheTimer();
        _delivery.Complete(() => Inbox.Post(Input.ReportersDone));
    }
}
