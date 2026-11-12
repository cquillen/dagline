namespace Dagline;

/// <summary>The entry point: runs a graph to its end.</summary>
public static class Runner
{
    /// <summary>Runs every task of <paramref name="graph"/> in dependency order and returns once the run has ended.</summary>
    /// <param name="graph">The graph to run. It is not changed, and can be run again.</param>
    /// <param name="options">The choices for this run.</param>
    /// <param name="stop">
    /// The signal through which the app asks this run to stop, or <see langword="null"/> for a run that cannot be stopped.
    /// A signal already requested stops the run from its start, and requests made after the run has ended are ignored.
    /// </param>
    /// <returns>
    /// A task that completes when no task is running and none is ready, and every reporter that was not dropped
    /// has handled every event. That wait has no time bound, but any request on <paramref name="stop"/> made during it,
    /// the first or a later one, ends it: each reporter still busy is dropped, the token passed to its
    /// <see cref="IReporter.OnEventAsync"/> is cancelled, its remaining events are discarded, and the task completes
    /// without waiting for its call to come back. Such a request does not change the run outcome.
    /// A reporter's exception never faults the task either: the reporter is dropped and its error is on
    /// <see cref="RunResult.ReporterErrors"/>. A reporter dropped by a stop has an <see cref="OperationCanceledException"/> there.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Every task starts pending, except an earlier success: a task that the journal the run continues records as succeeded, which
    /// is succeeded from the start, never runs and gets no event (see <see cref="Journal.Continue"/>). A task becomes ready once every
    /// dependency it has is met. Each change of status is applied
    /// one at a time, so the order of a run's events depends only on the order in which task bodies end.
    /// </para>
    /// <para>
    /// When the run starts, and whenever a task settles after that, the ready tasks are tried in declaration order.
    /// Each one that finds a slot free in the cap and in every limit it holds takes them all and is started on the thread pool.
    /// One that does not fit takes none, is passed over and stays ready, so it never holds up the tasks behind it.
    /// There are no priorities and no fairness: a task that needs two busy limits can be passed over many times.
    /// A task gives its slots back when it settles, whatever its status.
    /// </para>
    /// <para>
    /// The options are checked before anything else happens: <see cref="RunOptions.Cap"/> and every size in
    /// <see cref="RunOptions.Limits"/> must be at least 1, every limit the graph names must have a size, and
    /// <see cref="RunOptions.GracePeriod"/> must be from zero up to <see cref="uint.MaxValue"/> - 1 milliseconds (about 49.7 days),
    /// or <see cref="Timeout.InfiniteTimeSpan"/>.
    /// A size for a limit the graph does not name is ignored.
    /// </para>
    /// <para>
    /// A task that fails costs only the work that requires it. An exception that escapes a task body fails that task
    /// and is never thrown from this method, and a task also fails when it reports a failure with <see cref="TaskContext.Fail"/>. Every task that requires the failed task, directly, through a group or through
    /// a chain of such tasks, becomes blocked at once and records the failed task in <see cref="NodeSnapshot.BlockedBy"/>.
    /// A task that runs <em>after</em> it still runs, and so does every task with no dependency on it.
    /// The run outcome is then <see cref="RunOutcome.Incomplete"/>.
    /// </para>
    /// <para>
    /// A run is stopped through <paramref name="stop"/>, not through a <see cref="CancellationToken"/>. Once the first request
    /// on it has been applied, which publishes <see cref="StopRequested"/>, nothing starts, <em>after</em> tasks included,
    /// and <see cref="TaskContext.CancellationToken"/> is signalled on every running task. The run then waits for its running tasks
    /// to end, for at most <see cref="RunOptions.GracePeriod"/> on the run's clock, and a second request on <paramref name="stop"/>
    /// ends the wait at once. When the wait ends with tasks still running, <see cref="StoppedWaiting"/> is published, each of them
    /// is recorded as cancelled in declaration order, and the run lets go of it: this method returns while its body may still be
    /// running, and nothing the body does afterwards changes the run. While it waits a task that succeeds still makes its dependents ready, where they stay,
    /// and a task that fails still blocks its dependents. <see cref="TaskContext.CancellationToken"/> says how a task ends
    /// once a stop has been requested. Tasks the run never reached keep their status, pending or ready, and are listed in
    /// <see cref="Snapshot.NeverReached"/>.
    /// </para>
    /// <para>
    /// The run outcome is <see cref="RunOutcome.Stopped"/> when a stop was applied and a task has not succeeded,
    /// and <see cref="RunOutcome.Complete"/> when every task succeeded, stop or no stop.
    /// </para>
    /// <para>
    /// A run on a journal opens it at the call, before the run exists: a new journal is created with its first line flushed to the disk,
    /// and one to continue is read in full. The run writes each step's entries before anything else the step does, and flushes every
    /// settle to the disk before any dependent starts and before its event is published. The run holds the journal for its whole
    /// life through a lock file beside it, <c>&lt;id&gt;.lock</c>, so that no other run can use it meanwhile. It closes the journal and lets
    /// go of the lock in the step that ends the run, after flushing the run's end, however the run ends, so a later run can continue it
    /// as soon as this task completes. The lock file stays behind, and holds nothing.
    /// </para>
    /// <para>
    /// The journal exceptions are thrown at the call, after the options have been checked: no task has started, no event has been
    /// published and no reporter has been called. The checks run in this order: the options, the journal's lock, opening or creating
    /// the journal, reading it.
    /// </para>
    /// <para>
    /// Dagline never writes to the console and never hooks a console or process signal: the app decides what requests a stop.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="graph"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A reporter in <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="RunConfigurationException">
    /// The cap or a size is below 1, the graph names a limit that the options give no size, or the grace period is below zero
    /// or above <see cref="uint.MaxValue"/> - 1 milliseconds and is not <see cref="Timeout.InfiniteTimeSpan"/>. The message lists every problem.
    /// It is thrown at the call: no task has started, no event has been published and no reporter has been called.
    /// </exception>
    /// <exception cref="JournalInUseException">
    /// <see cref="RunOptions.Journal"/> continues a journal that another run holds, in this process or another.
    /// </exception>
    /// <exception cref="JournalNotFoundException">
    /// <see cref="RunOptions.Journal"/> continues a journal that is not in its directory.
    /// </exception>
    /// <exception cref="JournalOpenException">
    /// The journal, its lock file or its directory cannot be opened, created or read, or a new journal's first line cannot be written.
    /// </exception>
    /// <exception cref="JournalFormatException">
    /// The journal to continue cannot be read as one: its first line is not a journal header, its format version is unknown, or a
    /// line other than the last is not an entry.
    /// </exception>
    public static Task<RunResult> RunAsync(Graph graph, RunOptions options, StopSignal? stop = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(options);

        IReporter[] reporters = [.. options.Reporters];
        Arguments.ThrowIfAnyNull(reporters, "A reporter in the options is null.", nameof(options));

        var settings = RunSettings.From(graph, options);
        var journal = options.Journal.Open(settings.TimeProvider);

        return RunToEndAsync(graph, settings, journal, reporters, stop);
    }

    // No token, by design: a run is stopped through its StopSignal, not a CancellationToken, so there is none to pass on.
    private static async Task<RunResult> RunToEndAsync(
        Graph graph,
        RunSettings settings,
        JournalFile? journal,
        IReporter[] reporters,
        StopSignal? stop)
    {
        var delivery = new ReporterDelivery(reporters, stop);
        var run = new Run(graph, settings, delivery, stop, journal);

        run.Start();
        var outcome = await run.Ended.ConfigureAwait(false);
        var reporterErrors = await delivery.Delivered.ConfigureAwait(false);

        return new RunResult(outcome, run.Current, reporterErrors);
    }
}
