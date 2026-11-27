namespace Dagline.Testing;

/// <summary>
/// Runs one task body as a run of its own and returns what the task left behind, so that a test of one task is three lines.
/// </summary>
/// <remarks>
/// <para>
/// A run of the harness is a real <see cref="Runner.RunAsync"/> through Dagline's public API, on a graph of one task at the
/// root with default <see cref="RunOptions"/> except for the journal, the clock, an unlimited
/// <see cref="RunOptions.GracePeriod"/> and reporters of the harness's own that watch the run. The task body sees exactly
/// what it would in a real run, so a test proves what a real run will do.
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
    private readonly InstantClock _defaultClock = new();
    private readonly string _seed;
    private string _checkpoint;
    private string? _directory;
    private string? _journalId;
    private bool _disposed;

    private TaskHarness(TaskBody body, TimeProvider? timeProvider, string checkpoint)
    {
        _body = body;
        _passedClock = timeProvider;
        _seed = checkpoint;
        _checkpoint = checkpoint;
    }

    /// <summary>Wraps <paramref name="body"/> in a harness. Nothing runs and nothing touches the disk until the first run.</summary>
    /// <param name="body">The task body to run, as it would be handed to <see cref="GraphBuilder.Task"/>.</param>
    /// <param name="timeProvider">
    /// The clock every run uses, as <see cref="RunOptions.TimeProvider"/>, and so the one the body sees as
    /// <see cref="TaskContext.TimeProvider"/>. It is used as is. With none, <see cref="RunAsync"/> uses an
    /// <see cref="InstantClock"/> with the default guard, one for the life of the harness, so its guard counts fake time across
    /// runs. <see cref="RunUntilWaitingThenStopAsync"/> uses a clock built on that same <see cref="InstantClock"/>, so fake time
    /// and the guard carry across both calls: on it a delay or timer ends at once while the task is not waiting, and never
    /// fires while it is.
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
    /// The run's clock is an <see cref="InstantClock"/>, whether the default or passed to <see cref="For"/>, and the run ended
    /// with more fake time passed than its guard. This is thrown in place of the outcome, which would be the unexpected failure
    /// the guard caused, so a check that never returns <see langword="true"/> fails the test with this exception.
    /// </exception>
    public Task<TaskOutcome> RunAsync(CancellationToken cancellationToken = default)
        => RunWithAsync(_passedClock ?? _defaultClock, liveRun: null, stopAtWait: null, cancellationToken);

    /// <summary>
    /// Runs the task, stops the run as soon as it begins waiting, as an operator would, and returns the outcome the run ends
    /// with. This is how a first run is cut short, so that a later <see cref="RunAsync"/> can test how the task re-attaches.
    /// </summary>
    /// <param name="cancellationToken">
    /// The caller's token, to which the harness's own stop is linked: cancelling it stops the run before the task begins waiting,
    /// as the wait would.
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
    /// The stop is made once, by cancelling a token source of this run's own, linked to <paramref name="cancellationToken"/> and
    /// passed as the run's token, when the harness's reporter sees the first <see cref="WaitingChanged"/> that begins a wait. The
    /// grace period is unlimited, so the run waits for the task to end however long it takes.
    /// </para>
    /// <para>
    /// With no clock passed to <see cref="For"/>, the run uses a clock on which delays end at once until the task waits, and
    /// never while it waits. It decides each delay or timer from the run's live snapshot, <see cref="ISnapshotSource.Current"/>
    /// on the source <see cref="RunStarted"/> carries, by whether the task's node <see cref="NodeSnapshot.IsWaiting"/>: when
    /// the task is not waiting, a delay or timer ends at once, as on an <see cref="InstantClock"/>; when it is, it never fires,
    /// even after the wait ends. The decision is made when the timer is made, again on every <see cref="ITimer.Change"/> and
    /// before each repeat of a periodic timer, and again just before every callback, so a timer fires while the task is
    /// waiting only if its firing had already found the task not waiting when the wait began: that callback still runs. Each
    /// call makes a clock of its own, which asks only its own run, so a timer kept from one call into a later one is still
    /// decided by the run it was made in. A task is waiting inside <see cref="TaskContext.WaitUntilAsync"/> and inside a <see cref="TaskContext.BeginWaiting"/>
    /// scope alike, and is marked so as the scope opens, before the wait makes its first delay, so the wait's own delays never
    /// end and the task is still waiting when the stop arrives. A delay made after the wait, such as one in cleanup after the
    /// stop, ends at once again. In this call only, the harness holds the body until its reporter has received
    /// <see cref="RunStarted"/>, so every delay the body makes is decided from the live snapshot.
    /// </para>
    /// <para>
    /// The clock's time and guard are those of the <see cref="InstantClock"/> that <see cref="RunAsync"/> uses, one for the
    /// life of the harness, so fake time carries from this call to a later <see cref="RunAsync"/>, and a body that delays in a
    /// loop and never waits ends with <see cref="ClockGuardException"/> instead of hanging.
    /// </para>
    /// <para>
    /// A clock passed to <see cref="For"/> is used as is. With an <see cref="InstantClock"/> passed in this is a race by nature:
    /// the wait's checks run without delay, so the task may settle before the stop lands, and that outcome is returned.
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
    public async Task<TaskOutcome> RunUntilWaitingThenStopAsync(CancellationToken cancellationToken = default)
    {
        using var stopAtWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_passedClock is { } passed)
        {
            return await RunWithAsync(passed, liveRun: null, stopAtWait, stopAtWait.Token).ConfigureAwait(false);
        }

        // The clock is made for this run and asks only this run, so a timer kept past the run is never decided by a later one.
        var liveRun = new LiveRun(_taskPath);
        var clock = new InstantUntilWaitingClock(_defaultClock, liveRun.IsTaskWaiting);
        return await RunWithAsync(clock, liveRun, stopAtWait, stopAtWait.Token).ConfigureAwait(false);
    }

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

    private async Task<TaskOutcome> RunWithAsync(
        TimeProvider clock,
        LiveRun? liveRun,
        CancellationTokenSource? stopAtWait,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _directory ??= CreateDirectory();

        var builder = new GraphBuilder();
        builder.Task(_taskName, context => RunBodyAsync(liveRun?.Started, context));
        var watcher = new WaitWatcher(stopAtWait);
        var options = new RunOptions
        {
            Journal = _journalId is null ? Journal.New(_directory) : Journal.Continue(_directory, _journalId),
            TimeProvider = clock,
            GracePeriod = Timeout.InfiniteTimeSpan,
            Reporters = liveRun is null ? [watcher] : [watcher, liveRun],
        };

        RunResult result;
        try
        {
            result = await Runner.RunAsync(builder.Build(), options, drain: null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _journalId ??= watcher.JournalId;
        }

        GuardedClock?.ThrowIfPastGuard();
        var task = result.Snapshot[_taskPath];
        // A success discards the checkpoint (ADR 0006). The public API shows no checkpoint after the run, so the rule is restated here.
        if (task.Status is NodeStatus.Succeeded)
        {
            _checkpoint = "";
        }

        return new TaskOutcome(task, watcher.Waited, watcher.WaitingText, _checkpoint);
    }

    private InstantClock? GuardedClock => _passedClock is null ? _defaultClock : _passedClock as InstantClock;

    private async Task RunBodyAsync(Task? liveRunStarted, TaskContext context)
    {
        try
        {
            // Held until the run's own clock can read the live snapshot, so it decides every delay the body makes from it.
            // This waits on a reporter, which ADR 0010 says the run never does, but the run itself is not held: only this body
            // is, by the harness's own reporter, which never throws and is dropped only once the run has ended, so RunStarted
            // always reaches it while the task runs. It takes no token for the same reason.
            if (liveRunStarted is not null)
            {
                await liveRunStarted.ConfigureAwait(false);
            }

            // Saving the seed is the one way to hand a checkpoint to a task on a new journal through the public API. It takes no
            // token, as a task's own save after a trigger should not: a stop under way must not lose the seed for every later run.
            if (_journalId is null && _seed.Length > 0)
            {
                await context.SaveCheckpointAsync(_seed, CancellationToken.None).ConfigureAwait(false);
            }

            await _body(context).ConfigureAwait(false);
        }
        finally
        {
            _checkpoint = context.Checkpoint;
        }
    }

    private static string CreateDirectory() => Directory.CreateTempSubdirectory("dagline-harness-").FullName;

    // Records what the outcome needs from the run's events, and stops the run at the first wait when asked to.
    private sealed class WaitWatcher(CancellationTokenSource? stopAtWait) : IReporter
    {
        public bool Waited { get; private set; }

        public string? WaitingText { get; private set; }

        public string? JournalId { get; private set; }

        public ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken)
        {
            JournalId ??= snapshot.Journal?.Id;
            if (runEvent is not WaitingChanged { Waiting: true } began)
            {
                return ValueTask.CompletedTask;
            }

            WaitingText = began.Text;
            if (!Waited)
            {
                Waited = true;
                stopAtWait?.Cancel();
            }

            return ValueTask.CompletedTask;
        }
    }

    // Holds one run's live snapshot source from RunStarted on, so the run's clock can ask whether the task is waiting now.
    private sealed class LiveRun(NodePath task) : IReporter
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile ISnapshotSource? _source;

        public Task Started => _started.Task;

        public bool IsTaskWaiting() => _source?.Current[task].IsWaiting is true;

        public ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken)
        {
            if (runEvent is RunStarted started)
            {
                _source = started.Source;
                _started.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }
    }
}
