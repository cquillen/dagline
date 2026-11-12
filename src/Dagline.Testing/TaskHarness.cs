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
/// journal in a directory the harness creates under <see cref="Path.GetTempPath"/>, and <see cref="DisposeAsync"/> deletes
/// it.
/// </para>
/// <para>
/// The grace period is unlimited, so the run never lets go of the task: a body that ignores a stop keeps the call from
/// returning, and the test's own timeout is what fails it.
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
    private readonly TimeProvider _clock;
    private string? _directory;
    private bool _disposed;

    private TaskHarness(TaskBody body, TimeProvider clock)
    {
        var builder = new GraphBuilder();
        _task = builder.Task("task", body).Path;
        _graph = builder.Build();
        _clock = clock;
    }

    /// <summary>Wraps <paramref name="body"/> in a harness. Nothing runs and nothing touches the disk until the first run.</summary>
    /// <param name="body">The task body to run, as it would be handed to <see cref="GraphBuilder.Task"/>.</param>
    /// <param name="clock">
    /// The clock every run uses, as <see cref="RunOptions.TimeProvider"/>, and so the one the body sees as
    /// <see cref="TaskContext.TimeProvider"/>. It is used as is. With none, <see cref="RunAsync"/> uses an
    /// <see cref="InstantClock"/> with the default guard, one for the life of the harness, so its guard counts fake time across
    /// runs.
    /// </param>
    /// <returns>The harness. Dispose it to delete its temporary journal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> is <see langword="null"/>.</exception>
    public static TaskHarness For(TaskBody body, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        return new TaskHarness(body, clock ?? new InstantClock());
    }

    /// <summary>Runs the task to the end of the run and returns its outcome from the run's final snapshot.</summary>
    /// <returns>
    /// The task's outcome. <see cref="TaskOutcome.Waited"/> is <see langword="true"/> if the task began waiting at least once in
    /// this run.
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
    public async Task<TaskOutcome> RunAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _directory ??= CreateDirectory();

        var watcher = new WaitWatcher();
        var options = new RunOptions
        {
            Journal = Journal.New(_directory),
            TimeProvider = _clock,
            GracePeriod = Timeout.InfiniteTimeSpan,
            Reporters = [watcher],
        };

        var result = await Runner.RunAsync(_graph, options).ConfigureAwait(false);
        (_clock as InstantClock)?.ThrowIfPastGuard();
        return TaskOutcome.From(result.Snapshot[_task], watcher.Waited);
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

    private static string CreateDirectory() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"dagline-harness-{Guid.NewGuid():N}")).FullName;

    private sealed class WaitWatcher : IReporter
    {
        public bool Waited { get; private set; }

        public ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken)
        {
            if (runEvent is WaitingChanged { Waiting: true })
            {
                Waited = true;
            }

            return ValueTask.CompletedTask;
        }
    }
}
