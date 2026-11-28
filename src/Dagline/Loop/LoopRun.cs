namespace Dagline;

/// <summary>
/// A run on the new engine, as the app holds it (ADR 0016): its current snapshot from the moment it is started, and its result
/// once it has ended. Everything it is asked posts to the run's inbox, and the run's loop does the rest (ADR 0015).
/// </summary>
/// <remarks>
/// It is the new engine's handle while the engine is being built beside the existing one, and is reachable only from tests. It
/// becomes the run that <see cref="Runner"/> returns at the cutover, which renames it.
/// </remarks>
internal sealed class LoopRun
{
    private readonly RunLoop _loop;

    private LoopRun(RunLoop loop)
    {
        _loop = loop;
    }

    /// <summary>The run as it is now: as of the last event the loop published, or as at the start before the loop's first turn.</summary>
    public Snapshot Current => _loop.Source.Current;

    /// <summary>
    /// Completes with the run's result once it has ended and every reporter has handled every event or been dropped, or faults with
    /// what faulted the run's loop.
    /// </summary>
    public Task<RunResult> Result => _loop.Result;

    /// <summary>The run's inbox, which a test reads to see that a loop that faulted has closed it.</summary>
    public Inbox Inbox => _loop.Inbox;

    /// <summary>
    /// Asks the run to drain: it starts nothing new and lets its running tasks finish, untold, however long they take. Asked again,
    /// during a stop, or once the run has ended, it changes nothing. It posts to the inbox and returns at once.
    /// </summary>
    public void Drain() => _loop.Drain();

    /// <summary>
    /// Asks the run to stop, as cancelling its token does, and the two are one request: it starts nothing new, signals the tasks'
    /// token and waits the grace period, then lets go of the tasks still running. A drain may come before it. Asked again, or once
    /// the run has ended, it changes nothing. It posts to the inbox and returns at once.
    /// </summary>
    public void Stop() => _loop.Stop(StopRequester.App);

    /// <summary>
    /// Starts a run of <paramref name="graph"/> on the new engine, with <paramref name="host"/> to run its loop, its bodies and its
    /// reporters. The options are checked and the journal opened here, as <see cref="Runner.RunAsync"/> does, so each throws at the
    /// call before anything has happened. The run's first turn is the host's to run.
    /// </summary>
    /// <param name="graph">The graph to run.</param>
    /// <param name="options">The options of the run.</param>
    /// <param name="host">What runs the loop, the bodies and the reporters.</param>
    /// <param name="drained">
    /// Whether a drain was already asked for, which goes with the start as a cancelled token does. Only the engine switch passes it,
    /// for a drain signal already requested, until the cutover removes the signal.
    /// </param>
    /// <param name="cancellationToken">
    /// The app's token: cancelling it is a stop, as <see cref="Stop"/> is. One already cancelled reaches the run with its start, so
    /// the tasks become ready and none starts. Cancelled after the run has ended, it does nothing.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="graph"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A reporter in <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="RunConfigurationException">The options do not fit the graph.</exception>
    public static LoopRun Start(
        Graph graph,
        RunOptions options,
        IRunHost host,
        bool drained = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(options);

        IReporter[] reporters = [.. options.Reporters];
        Arguments.ThrowIfAnyNull(reporters, "A reporter in the options is null.", nameof(options));

        var settings = RunSettings.From(graph, options);
        var journal = options.Journal.Open(settings.TimeProvider);

        var run = new LoopRun(new RunLoop(graph, settings, reporters, journal, host));
        run._loop.Start(drained, cancellationToken);
        return run;
    }
}
