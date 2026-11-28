namespace Dagline.Testing;

/// <summary>
/// Runs one task body as a run of its own and returns what the task left behind, so that a test of one task is three lines.
/// </summary>
/// <remarks>
/// <para>
/// A run of the harness is a real run through Dagline's public API, a <see cref="ScriptedRun"/> that the harness pumps inside the
/// call, on a graph of one task at the root with default <see cref="RunOptions"/> except for the journal, the clock, an
/// unlimited <see cref="RunOptions.GracePeriod"/> and <see cref="RunOptions.ReporterWait"/>, and a reporter of the harness's own
/// that watches the run. The task body sees exactly what it would in a real run, so a test proves what a real run will do. The
/// body starts on the thread that calls the run method, inside the call, and every await in it that resumes on its context comes
/// back to the harness's pump.
/// </para>
/// <para>
/// With no clock passed to <see cref="For"/>, every run is on a clock of the harness's own, one for its life, on which time moves
/// only when the harness fires a timer: whenever nothing of the run is left to do, the harness fires the timer due first, moving
/// the time on to when it is due. So a delay ends at once, from the body's point of view, and a graph of waits of hours takes no
/// real time. The clock starts at midnight UTC on 1 January 2000, its local time zone is UTC, and it carries its time from one
/// run to the next. Its guard is that of an <see cref="InstantClock"/>: once more than 7 days of fake time have passed, the body
/// can make no delay or timer on it, and the run method throws <see cref="ClockGuardException"/>.
/// </para>
/// <para>
/// A clock passed to <see cref="For"/> is used as it is: the harness never fires its timers, and pumps the run until its result,
/// waiting in real time whenever nothing is queued.
/// </para>
/// <para>
/// Whenever the run's body awaits what the pump cannot see, such as real I/O, the harness waits, in real time and for as long as
/// it takes, until something is queued or a timer is set. A body that never ends keeps the call from returning: the test's own
/// timeout is what fails it.
/// </para>
/// <para>
/// <see cref="For"/> only wraps the body: nothing runs and nothing touches the disk until the first run. That run starts a new
/// journal in a new directory the harness creates directly under <see cref="Path.GetTempPath"/>, named <c>dagline-harness-</c>
/// and random characters, through <see cref="Directory.CreateTempSubdirectory"/>, so on Unix only the current user can open
/// it. Every later run continues that journal, as <see cref="Journal.Continue"/> does. So a task gets back in a later run the
/// checkpoint it saved in an earlier one that ended failed or cancelled, and a task that succeeded is an earlier success that
/// does not run again.
/// <see cref="DisposeAsync"/> deletes the directory.
/// </para>
/// <para>
/// Every run of the harness, the first and each that continues, chooses <see cref="JournalDurability.HandedToOperatingSystem"/>:
/// the journal is written at the same moments as in an app, and the task's checkpoint saves and settles are in the file when they
/// would be, but nothing is flushed to the disk, so a test spends no time waiting on one. The journal lives only as long as the
/// harness, so a power cut's safety would buy it nothing.
/// </para>
/// <para>
/// A checkpoint passed to <see cref="For"/> is planted in the first run without a run before it to save one: the harness
/// saves it through <see cref="TaskContext.SaveCheckpointAsync"/> on the task's own context just before it calls the body, so
/// the body reads it back as <see cref="TaskContext.Checkpoint"/>, and the journal holds it as it would hold the task's own
/// save. The save publishes no event and changes no snapshot.
/// </para>
/// <para>
/// Each run method takes a <see cref="CancellationToken"/>, passed straight through as the run's token: cancelling it stops the
/// run, which signals the task's <see cref="TaskContext.CancellationToken"/>, and the call returns the outcome the task ends with.
/// It never makes the call throw. The grace period is unlimited in every run, so the run never lets go of the task: a body that
/// ignores a stop keeps the call from returning, and the test's own timeout is what fails it.
/// </para>
/// <para>
/// A harness does one run at a time. It is not safe to call from two threads at once. It works with any test framework and
/// assertion library.
/// </para>
/// </remarks>
public sealed class TaskHarness : IAsyncDisposable
{
    private const string _taskName = "task";

    private static readonly NodePath _taskPath = NodePath.Parse(_taskName);

    private readonly TaskBody _body;
    private readonly TimeProvider? _passedClock;
    private readonly ScriptedClock _defaultClock;
    private readonly Guard _defaultGuard = new(TimeSpan.FromDays(7));
    private readonly string _seed;
    private string _checkpoint;
    private string? _directory;
    private string? _journalId;
    private bool _disposed;
    private bool _bodyIsRunning;

    private TaskHarness(TaskBody body, TimeProvider? timeProvider, string checkpoint)
    {
        _defaultClock = new ScriptedClock(RefuseTimersPastTheGuard);
        _body = body;
        _passedClock = timeProvider;
        _seed = checkpoint;
        _checkpoint = checkpoint;
    }

    /// <summary>Wraps <paramref name="body"/> in a harness. Nothing runs and nothing touches the disk until the first run.</summary>
    /// <param name="body">The task body to run, as it would be handed to <see cref="GraphBuilder.Task"/>.</param>
    /// <param name="timeProvider">
    /// The clock every run uses, as <see cref="RunOptions.TimeProvider"/>, and so the one the body sees as
    /// <see cref="TaskContext.TimeProvider"/>. It is used as is, and the harness never moves it. With none, every run uses a clock
    /// of the harness's own, one for the life of the harness, whose timers the harness fires whenever nothing of the run is left
    /// to do, so its time and its 7-day guard carry across runs and across both run methods. That clock is not public.
    /// </param>
    /// <param name="checkpoint">
    /// A checkpoint for the task to read back as <see cref="TaskContext.Checkpoint"/> in the first run, as if an earlier run had
    /// saved it, so that a test of how a task re-attaches needs no first run to save one. It is planted only in the first run,
    /// which starts the harness's journal: a later run hands the task the checkpoint the run before it left, as
    /// <see cref="TaskOutcome.Checkpoint"/> gives it. <see langword="null"/> or empty plants none, since an absent checkpoint is
    /// empty text.
    /// </param>
    /// <returns>The harness. Dispose it to delete its temporary journal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> is <see langword="null"/>.</exception>
    public static TaskHarness For(TaskBody body, TimeProvider? timeProvider = null, string? checkpoint = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        return new TaskHarness(body, timeProvider, checkpoint ?? "");
    }

    /// <summary>Runs the task to the end of the run and returns its outcome from the run's final snapshot.</summary>
    /// <param name="cancellationToken">
    /// The run's token: cancelling it stops the run, and the task ends as its body chooses. A token already cancelled starts no
    /// run of the body, and the task is returned as it was left: ready, or an earlier success.
    /// </param>
    /// <returns>
    /// The task's outcome. <see cref="TaskOutcome.Waited"/> is <see langword="true"/> if the task began waiting at least once in
    /// this run. When the task succeeded in an earlier run of this harness, the body is not called and the earlier success's
    /// outcome is returned: its message, detail lines and warnings, with <see cref="TaskOutcome.Waited"/>
    /// <see langword="false"/>, no <see cref="TaskOutcome.StatusText"/> and no <see cref="TaskOutcome.WaitingText"/>, since a
    /// journal does not keep progress and the task began no wait in this run.
    /// </returns>
    /// <remarks>
    /// An exception the task body throws never comes out of this call: it is the task's unexpected failure, returned as an
    /// outcome with <see cref="FailureKind.Unexpected"/>. An exception from the run itself, such as a journal that cannot be
    /// written, comes out unchanged.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The harness has been disposed.</exception>
    /// <exception cref="ClockGuardException">
    /// No clock was passed to <see cref="For"/>, or the clock passed is an <see cref="InstantClock"/>, and the run ended with more
    /// fake time passed than its guard. This is thrown in place of the outcome, which would be the unexpected failure the guard
    /// caused, so a check that never returns <see langword="true"/> fails the test with this exception.
    /// </exception>
    public Task<TaskOutcome> RunAsync(CancellationToken cancellationToken = default)
        => RunWithAsync(stopAtWait: false, cancellationToken);

    /// <summary>
    /// Runs the task, stops the run as soon as it begins waiting, as an operator would, and returns the outcome the run ends
    /// with. This is how a first run is cut short, so that a later <see cref="RunAsync"/> can test how the task re-attaches.
    /// </summary>
    /// <param name="cancellationToken">
    /// The run's token: cancelling it stops the run, before the task begins waiting or after, as the harness's own stop does.
    /// </param>
    /// <returns>
    /// The task's outcome. For a task that waits through <see cref="TaskContext.WaitUntilAsync"/> it is
    /// <see cref="NodeStatus.Cancelled"/> with <see cref="TaskOutcome.Waited"/> <see langword="true"/>. A task that handles the
    /// stop itself, by catching the cancellation and reporting, returns the outcome it chose. A task that settles without ever
    /// waiting returns its own outcome, and the call does not hang. A task that delays on its clock before it begins waiting,
    /// such as a back-off or a retry loop around a trigger, reaches its wait and is stopped there.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The harness pumps the run until nothing is queued. If the task is then waiting, as the run has applied it, the harness
    /// stops the run with <see cref="Run.Stop"/>, once, and pumps it to its result. If it is not, and no clock was passed to
    /// <see cref="For"/>, it fires the harness clock's next timer, moving the time on to when it is due, and pumps again. A task
    /// is waiting inside <see cref="TaskContext.WaitUntilAsync"/> and inside a <see cref="TaskContext.BeginWaiting"/> scope alike.
    /// So a body that delays before it waits reaches its wait with the clock moved by exactly those delays, and is stopped there
    /// once the wait has made its first check, before the wait's first delay ends. A wait that has begun and ended between two
    /// moments when nothing is queued is never seen, and the run goes on. After the stop, timers fire again whenever nothing is
    /// queued, so a delay in cleanup after the stop ends. The grace period is unlimited, so the run waits for the task to end
    /// however long it takes.
    /// </para>
    /// <para>
    /// The harness's pump stays exact only for what it can see: a body whose awaits are completed by the run, by the harness's
    /// clock or by the body itself on its context. Real I/O is waited for in real time; the stop then lands once the run has
    /// applied the wait, whatever the I/O is doing.
    /// </para>
    /// <para>
    /// A clock passed to <see cref="For"/> is used as is, and the harness never fires its timers. With an
    /// <see cref="InstantClock"/> passed in this is a race by nature: its timers fire on the thread pool at once, so the wait's
    /// checks run without delay, the task may settle before the harness sees it waiting, and that outcome is returned.
    /// </para>
    /// <para>
    /// Exceptions are as for <see cref="RunAsync"/>: one from the body is the task's unexpected failure, and one from the run
    /// itself comes out unchanged.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The harness has been disposed.</exception>
    /// <exception cref="ClockGuardException">
    /// No clock was passed to <see cref="For"/>, or the clock passed is an <see cref="InstantClock"/>, and the run ended with
    /// more fake time passed than its guard, as for <see cref="RunAsync"/>.
    /// </exception>
    public Task<TaskOutcome> RunUntilWaitingThenStopAsync(CancellationToken cancellationToken = default)
        => RunWithAsync(stopAtWait: true, cancellationToken);

    /// <summary>
    /// Deletes the harness's temporary directory and its journal, if a run created them. Disposing twice is harmless, and a run
    /// after disposal is refused with <see cref="ObjectDisposedException"/>.
    /// </summary>
    /// <returns>A task that completes once the directory is gone.</returns>
    /// <exception cref="IOException">The directory or a file in it cannot be deleted.</exception>
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_directory is { } directory && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private async Task<TaskOutcome> RunWithAsync(bool stopAtWait, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _directory ??= CreateDirectory();

        var builder = new GraphBuilder();
        builder.Task(_taskName, RunBodyAsync);
        var watcher = new WaitWatcher();
        var options = new RunOptions
        {
            Journal = _journalId is null
                ? Journal.New(_directory, durability: JournalDurability.HandedToOperatingSystem)
                : Journal.Continue(_directory, _journalId, JournalDurability.HandedToOperatingSystem),
            TimeProvider = _passedClock ?? _defaultClock,
            GracePeriod = Timeout.InfiniteTimeSpan,
            Reporters = [watcher],

            // The harness's one reporter never blocks, so nothing is gained by a limit, and a limit's timer on an instant clock
            // passed in would fire at once on the thread pool and could drop the reporter before it heard the run's last events.
            ReporterWait = Timeout.InfiniteTimeSpan,
        };

        var scripted = ScriptedRun.Start(builder.Build(), options, _passedClock is null ? _defaultClock : null, cancellationToken);
        RunResult result;
        try
        {
            await PumpToTheEndAsync(scripted, stopAtWait).ConfigureAwait(false);
            result = await scripted.Run.Result.ConfigureAwait(false);
        }
        finally
        {
            // From the run, not the reporter: a reporter the run let go of at its end would have missed the journal.
            _journalId ??= scripted.Run.Current.Journal?.Id;
        }

        ThrowIfPastTheGuard();
        var task = result.Snapshot[_taskPath];
        // A success discards the checkpoint (ADR 0006). The public API shows no checkpoint after the run, so the rule is restated here.
        if (task.Status is NodeStatus.Succeeded)
        {
            _checkpoint = "";
        }

        return new TaskOutcome(task, watcher.Waited, watcher.WaitingText, _checkpoint);
    }

    /// <summary>
    /// Pumps the run until its result is complete. Whenever nothing is queued: once the task is waiting, as the run has applied it,
    /// the run is stopped, if this run stops at the wait and has not yet; otherwise the default clock's next timer fires; and with
    /// neither to do, the pump waits, in real time and for as long as it takes, for something to be queued or a timer to be set.
    /// </summary>
    private async Task PumpToTheEndAsync(ScriptedRun scripted, bool stopAtWait)
    {
        var stopped = !stopAtWait;
        while (true)
        {
            scripted.Pump();
            if (scripted.Run.Result.IsCompleted)
            {
                return;
            }

            if (!stopped && scripted.Run.Current[_taskPath].IsWaiting)
            {
                scripted.Run.Stop();
                stopped = true;
                continue;
            }

            if (_passedClock is null && scripted.FireNextTimer())
            {
                continue;
            }

            await scripted.WhenThereIsSomethingToDoAsync().ConfigureAwait(false);
        }
    }

    /// <exception cref="ClockGuardException">The run's clock has passed more fake time than its guard.</exception>
    private void ThrowIfPastTheGuard()
    {
        if (_passedClock is null)
        {
            _defaultGuard.ThrowIfPast(_defaultClock.Elapsed);
        }
        else if (_passedClock is InstantClock instant)
        {
            instant.ThrowIfPastGuard();
        }
    }

    // The awaits here are a task body's own: each resumes on the host that started the body (ADR 0015), so that a scripted run's
    // body stays on its pump. The seed's save is answered on the loop's turn, which has no context: resuming there would take the
    // consumer's body off the pump.
    private async Task RunBodyAsync(TaskContext context)
    {
        Volatile.Write(ref _bodyIsRunning, true);
        try
        {
            // Saving the seed is the one way to hand a checkpoint to a task on a new journal through the public API. It takes no
            // token, as a task's own save after a trigger should not: a stop under way must not lose the seed for every later run.
            if (_journalId is null && _seed.Length > 0)
            {
                await context.SaveCheckpointAsync(_seed, CancellationToken.None).ConfigureAwait(true);
            }

            await _body(context).ConfigureAwait(true);
        }
        finally
        {
            _checkpoint = context.Checkpoint;
            Volatile.Write(ref _bodyIsRunning, false);
        }
    }

    // The default clock's guard refuses only a timer made while the body runs, as an instant clock's refuses the body's delays: the
    // run's own timer for the reporter wait, made once the body has ended, is never refused, so the run always ends.
    private void RefuseTimersPastTheGuard()
    {
        if (Volatile.Read(ref _bodyIsRunning))
        {
            _defaultGuard.ThrowIfPast(_defaultClock.Elapsed);
        }
    }

    private static string CreateDirectory() => Directory.CreateTempSubdirectory("dagline-harness-").FullName;

    // Records what the outcome needs from the run's events.
    private sealed class WaitWatcher : IReporter
    {
        public bool Waited { get; private set; }

        public string? WaitingText { get; private set; }

        public ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken)
        {
            if (runEvent is WaitingChanged { Waiting: true } began)
            {
                WaitingText = began.Text;
                Waited = true;
            }

            return ValueTask.CompletedTask;
        }
    }

    // The guard of the default clock: the most fake time it may pass, as an instant clock's guard is.
    private sealed class Guard(TimeSpan guard)
    {
        /// <exception cref="ClockGuardException"><paramref name="elapsed"/> is more than the guard.</exception>
        public void ThrowIfPast(TimeSpan elapsed)
        {
            if (elapsed > guard)
            {
                throw new ClockGuardException(elapsed, guard);
            }
        }
    }
}
