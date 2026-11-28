namespace Dagline;

/// <summary>
/// One run of a graph, as the app holds it: <see cref="Runner.Start"/> returns it. The app asks it to <see cref="Drain"/> or to
/// <see cref="Stop"/>, reads its <see cref="Current"/> snapshot at any time, and awaits its <see cref="Result"/> once it has ended.
/// </summary>
/// <remarks>
/// <para>
/// One loop owns the run from its start until its result is returned. A drain, a stop, the cancellation of the run's token, the
/// end of a task body and every report a task makes are each posted to the loop, and the call that posts returns at once: no
/// call on a run waits for the run, for a task or for a reporter, and none runs any of the run's work on the caller's thread. The
/// loop applies what is posted one at a time, in the order it was posted, on the thread pool.
/// </para>
/// <para>
/// A drain or a stop asked for once the run has ended changes nothing, and so does one asked for again. Where the run is in its
/// life is read from its snapshot: <see cref="Snapshot.Drain"/>, <see cref="Snapshot.Stop"/> and <see cref="Snapshot.Ended"/>.
/// </para>
/// <para>
/// A run holds no resource of its own and is not disposed: the journal it is on is closed, and let go of, before
/// <see cref="RunEnded"/> is published. Reporters are never handed the run, only the read-only source of snapshots that
/// <see cref="RunStarted.Source"/> carries, so a reporter cannot drain or stop it.
/// </para>
/// </remarks>
public sealed class Run : ISnapshotSource
{
    private readonly RunLoop _loop;

    private Run(RunLoop loop)
    {
        _loop = loop;
        Result = ThrowIfUnrecordedAsync(loop.Result);
    }

    /// <summary>
    /// The run as it is now: as of the last event the run published, with the progress each running task has reported since.
    /// It answers from the moment <see cref="Runner.Start"/> returns, before the run has published anything, and once the run has
    /// ended it is the final snapshot.
    /// </summary>
    /// <remarks>
    /// Safe to read from any thread, and reading it never waits for the run, a task or a reporter. A report a task makes, other than
    /// its progress, shows here once the run has applied it, which may be a little after the task's call has returned. Progress
    /// shows on the next read.
    /// </remarks>
    public Snapshot Current => _loop.Source.Current;

    /// <summary>
    /// Completes with the run's result once the run has ended and every reporter has handled every event or been dropped. It is
    /// the same task each time it is read, and <see cref="Runner.RunAsync"/> returns it.
    /// </summary>
    /// <remarks>
    /// A task's exception, a reporter's exception and the cancellation of the run's token never fault it. It faults with a
    /// <see cref="JournalWriteException"/> when the run's journal could no longer be written during the run, once reporters have
    /// handled every event (see <see cref="Runner.Start"/>).
    /// </remarks>
    public Task<RunResult> Result { get; }

    /// <summary>
    /// Asks the run to drain: once the run has applied it, which publishes <see cref="DrainRequested"/>, it starts nothing new,
    /// <em>after</em> tasks included, and lets its running tasks finish as they would, untold and however long they take.
    /// </summary>
    /// <remarks>
    /// It posts to the run and returns at once, and is safe to call from any thread, a console's cancel handler included. It never
    /// throws. Asked again, once a stop has been applied, or once the run has ended, it changes nothing. A stop may follow it.
    /// </remarks>
    public void Drain() => _loop.Drain();

    /// <summary>
    /// Asks the run to stop, as cancelling the token given to <see cref="Runner.Start"/> does: the two are one request, and
    /// whichever comes first applies the stop. Once the run has applied it, which publishes <see cref="StopRequested"/> with
    /// <see cref="StopRequester.App"/>, it starts nothing new, signals <see cref="TaskContext.CancellationToken"/> on every running
    /// task, and waits for them for at most <see cref="RunOptions.GracePeriod"/>.
    /// </summary>
    /// <remarks>
    /// It posts to the run and returns at once, and is safe to call from any thread. It never throws. A drain may come before it.
    /// Asked again, after the token was cancelled, or once the run has ended, it changes nothing.
    /// </remarks>
    public void Stop() => _loop.Stop(StopRequester.App);

    /// <summary>
    /// The run's result as its loop gives it, an unrecorded run's included, which <see cref="Result"/> throws instead. For the
    /// scripted tests, until unrecorded becomes a run outcome (#153).
    /// </summary>
    internal Task<RunResult> ResultEvenIfUnrecorded => _loop.Result;

    /// <summary>The run's inbox, which a test reads to see that a loop that faulted has closed it.</summary>
    internal Inbox Inbox => _loop.Inbox;

    /// <summary>
    /// Starts a run of <paramref name="graph"/> with <paramref name="host"/> to run its loop, its bodies and its reporters. The
    /// options are checked and the journal opened here, so each throws at the call before anything has happened. The run's first
    /// turn is the host's to run.
    /// </summary>
    /// <param name="graph">The graph to run.</param>
    /// <param name="options">The options of the run.</param>
    /// <param name="host">What runs the loop, the bodies and the reporters.</param>
    /// <param name="cancellationToken">
    /// The app's token: cancelling it is a stop, as <see cref="Stop"/> is. One already cancelled reaches the run with its start, so
    /// the tasks become ready and none starts. Cancelled after the run has ended, it does nothing.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="graph"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A reporter in <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="RunConfigurationException">The options do not fit the graph.</exception>
    internal static Run Start(Graph graph, RunOptions options, IRunHost host, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(options);

        IReporter[] reporters = [.. options.Reporters];
        Arguments.ThrowIfAnyNull(reporters, "A reporter in the options is null.", nameof(options));

        var settings = RunSettings.From(graph, options);
        var journal = options.Journal.Open(settings.TimeProvider);

        var run = new Run(new RunLoop(graph, settings, reporters, journal, host));
        run._loop.Start(cancellationToken);
        return run;
    }

    // Until #153 makes unrecorded a run outcome: an unrecorded run's result throws the exception its end carried, as before.
    private static async Task<RunResult> ThrowIfUnrecordedAsync(Task<RunResult> ended)
    {
        var result = await ended.ConfigureAwait(false);
        return result.Unrecorded is { } unrecorded ? throw unrecorded : result;
    }
}
