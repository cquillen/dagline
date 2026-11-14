namespace Dagline.Testing;

/// <summary>
/// Runs one task body as a run of its own and returns what the task left behind, so that a test of one task is three lines.
/// </summary>
/// <remarks>
/// <para>
/// A run of the harness is a real <see cref="Runner.RunAsync"/> through Dagline's public API, on a graph of one task at the
/// root with default <see cref="RunOptions"/> except for the journal, the clock, an unlimited
/// <see cref="RunOptions.GracePeriod"/> and a reporter of the harness's own that watches the run. The task body sees exactly
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
/// The grace period is unlimited in every run, so the run never lets go of the task: a body that ignores a stop keeps the call
/// from returning, and the test's own timeout is what fails it.
/// </para>
/// <para>
/// A harness does one run at a time. It is not safe to call from two threads at once. It works with any test framework and
/// assertion library.
/// </para>
/// </remarks>
public sealed class TaskHarness : IAsyncDisposable
{
    private readonly Graph _graph;
    private readonly NodePath _task;
    private readonly TimeProvider _runClock;
    private readonly TimeProvider _stopClock;
    private string? _directory;
    private string? _journalId;
    private bool _disposed;

    private TaskHarness(TaskBody body, TimeProvider? timeProvider)
    {
        var builder = new GraphBuilder();
        _task = builder.Task("task", body).Path;
        _graph = builder.Build();
        _runClock = timeProvider ?? new InstantClock();
        _stopClock = timeProvider ?? new NeverEndingClock();
    }

    /// <summary>Wraps <paramref name="body"/> in a harness. Nothing runs and nothing touches the disk until the first run.</summary>
    /// <param name="body">The task body to run, as it would be handed to <see cref="GraphBuilder.Task"/>.</param>
    /// <param name="timeProvider">
    /// The clock every run uses, as <see cref="RunOptions.TimeProvider"/>, and so the one the body sees as
    /// <see cref="TaskContext.TimeProvider"/>. It is used as is. With none, <see cref="RunAsync"/> uses an
    /// <see cref="InstantClock"/> with the default guard, one for the life of the harness, so its guard counts fake time across
    /// runs, and <see cref="RunUntilWaitingThenStopAsync"/> uses a clock on which no delay ever ends, so a body that delays on
    /// its clock before it begins waiting never reaches the wait in that call.
    /// </param>
    /// <returns>The harness. Dispose it to delete its temporary journal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> is <see langword="null"/>.</exception>
    public static TaskHarness For(TaskBody body, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        return new TaskHarness(body, timeProvider);
    }

    /// <summary>Runs the task to the end of the run and returns its outcome from the run's final snapshot.</summary>
    /// <returns>
    /// The task's outcome. <see cref="TaskOutcome.Waited"/> is <see langword="true"/> if the task began waiting at least once in
    /// this run. When the task succeeded in an earlier run of this harness, the body is not called and the earlier success's
    /// outcome is returned, with <see cref="TaskOutcome.Waited"/> <see langword="false"/>.
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
    public Task<TaskOutcome> RunAsync() => RunWithAsync(_runClock, stopAtWait: null);

    /// <summary>
    /// Runs the task, requests a stop as soon as it begins waiting, as an operator would, and returns the outcome the run ends
    /// with. This is how a first run is cut short, so that a later <see cref="RunAsync"/> can test how the task re-attaches.
    /// </summary>
    /// <returns>
    /// The task's outcome. For a task that waits through <see cref="TaskContext.WaitUntilAsync"/> it is
    /// <see cref="NodeStatus.Cancelled"/> with <see cref="TaskOutcome.Waited"/> <see langword="true"/>. A task that handles the
    /// stop itself, by catching the cancellation and reporting, returns the outcome it chose. A task that settles without ever
    /// waiting returns its own outcome, and the call does not hang.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The stop is requested once, through a <see cref="StopSignal"/> of this run's own, when the harness's reporter sees the
    /// first <see cref="WaitingChanged"/> that begins a wait. The grace period is unlimited, so the run waits for the task to end
    /// however long it takes.
    /// </para>
    /// <para>
    /// With no clock passed to <see cref="For"/>, the run uses a clock on which no delay ever ends, so the task is still waiting
    /// when the stop arrives. That holds for every delay on the clock, not only the wait's: a body that delays on its clock
    /// before it begins waiting never reaches the wait, so the stop is never requested and the call does not return. Pass a
    /// clock to <see cref="For"/> for such a body. A clock passed in is used as is. With an <see cref="InstantClock"/> passed in this is a race by
    /// nature: the wait's checks run without delay, so the task may settle before the stop lands, and that outcome is returned.
    /// </para>
    /// <para>
    /// Exceptions are as for <see cref="RunAsync"/>: one from the body is the task's unexpected failure, and one from the run
    /// itself comes out unchanged.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The harness has been disposed.</exception>
    /// <exception cref="ClockGuardException">
    /// The clock passed to <see cref="For"/> is an <see cref="InstantClock"/>, and the run ended with more fake time passed than
    /// its guard.
    /// </exception>
    public Task<TaskOutcome> RunUntilWaitingThenStopAsync() => RunWithAsync(_stopClock, stopAtWait: new StopSignal());

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

    private async Task<TaskOutcome> RunWithAsync(TimeProvider clock, StopSignal? stopAtWait)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _directory ??= CreateDirectory();

        var watcher = new WaitWatcher(stopAtWait);
        var options = new RunOptions
        {
            Journal = _journalId is null ? Journal.New(_directory) : Journal.Continue(_directory, _journalId),
            TimeProvider = clock,
            GracePeriod = Timeout.InfiniteTimeSpan,
            Reporters = [watcher],
        };

        RunResult result;
        try
        {
            result = await Runner.RunAsync(_graph, options, stopAtWait).ConfigureAwait(false);
        }
        finally
        {
            _journalId ??= watcher.JournalId;
        }

        (clock as InstantClock)?.ThrowIfPastGuard();
        return TaskOutcome.From(result.Snapshot[_task], watcher.Waited);
    }

    private static string CreateDirectory() => Directory.CreateTempSubdirectory("dagline-harness-").FullName;

    private sealed class WaitWatcher(StopSignal? stopAtWait) : IReporter
    {
        public bool Waited { get; private set; }

        public string? JournalId { get; private set; }

        public ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken)
        {
            JournalId ??= snapshot.Journal?.Id;
            if (runEvent is WaitingChanged { Waiting: true } && !Waited)
            {
                Waited = true;

                // A second request would end the unlimited grace wait and let go of the task.
                stopAtWait?.Request();
            }

            return ValueTask.CompletedTask;
        }
    }
}
