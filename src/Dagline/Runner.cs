namespace Dagline;

/// <summary>The entry point: runs a graph to its end.</summary>
public static class Runner
{
    /// <summary>Runs every task of <paramref name="graph"/> in dependency order and returns once the run has ended.</summary>
    /// <param name="graph">The graph to run. It is not changed, and can be run again.</param>
    /// <param name="options">The choices for this run.</param>
    /// <param name="drain">
    /// The signal through which the app asks this run to drain, or <see langword="null"/> for a run that cannot be drained.
    /// A signal already requested drains the run from its start, and a request made after the run has ended is ignored.
    /// </param>
    /// <param name="cancellationToken">
    /// The token whose cancellation stops this run. A token already cancelled stops the run from its start. Its cancellation never
    /// makes this method or the returned task throw: the run ends and returns its result as usual.
    /// </param>
    /// <returns>
    /// A task that completes when no task is running and none is ready, and every reporter that was not dropped
    /// has handled every event. That wait has no time bound. When the run ended without applying a stop, cancelling
    /// <paramref name="cancellationToken"/> during it ends it: each reporter still busy is dropped, the token passed to its
    /// <see cref="IReporter.OnEventAsync"/> is cancelled, its remaining events are discarded, and the task completes
    /// without waiting for its call to come back. That cancellation does not change the run outcome. A token cancelled before every
    /// task had ended is applied as a stop instead, and drops no reporter, and once a stop has been applied nothing drops a reporter
    /// that hangs. A drain never drops a reporter.
    /// A reporter's exception never faults the task either: the reporter is dropped and its error is on
    /// <see cref="RunResult.ReporterErrors"/>. A reporter dropped by the token has an <see cref="OperationCanceledException"/> there.
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
    /// Each one that finds a slot free in the max running tasks and in every limit it holds takes them all and is started on the thread pool.
    /// One that does not fit takes none, is passed over and stays ready, so it never holds up the tasks behind it.
    /// There are no priorities and no fairness: a task that needs two busy limits can be passed over many times.
    /// A task gives its slots back when it settles, whatever its status.
    /// </para>
    /// <para>
    /// The options are checked before anything else happens: <see cref="RunOptions.MaxRunningTasks"/> and every size in
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
    /// An app ends a run early in one of two ways. A <em>drain</em> is a request on <paramref name="drain"/>. Once the run has
    /// applied it, which publishes <see cref="DrainRequested"/>, nothing starts, <em>after</em> tasks included, and the running
    /// tasks finish as they would: their <see cref="TaskContext.CancellationToken"/> is not signalled and no time limit is set.
    /// Requesting it again, or once a stop has been applied, changes nothing.
    /// </para>
    /// <para>
    /// A <em>stop</em> is the cancellation of <paramref name="cancellationToken"/>. Once the run has applied it, which publishes
    /// <see cref="StopRequested"/>, nothing starts, <em>after</em> tasks included, and <see cref="TaskContext.CancellationToken"/>
    /// is signalled on every running task. A stop can follow a drain, and is published after it. The run then waits for its running
    /// tasks to end, for at most <see cref="RunOptions.GracePeriod"/> on the run's clock, and only the grace period running out ends
    /// the wait. When it runs out with tasks still running, <see cref="GraceEnded"/> is published, each of them is recorded as
    /// cancelled in declaration order, and the run lets go of it: this method returns while its body may still be running, and
    /// nothing the body does afterwards changes the run. The run hears the token on the thread pool, never on the thread that
    /// cancels it, and the token a task sees is the run's own, signalled in the step that applies the stop, so a task never sees
    /// a stop before its <see cref="StopRequested"/>.
    /// </para>
    /// <para>
    /// While the run waits after a drain or a stop, a task that succeeds still makes its dependents ready, where they stay,
    /// and a task that fails still blocks its dependents. Tasks the run never reached keep their status, pending or ready, and are
    /// listed in <see cref="Snapshot.NeverReached"/>. A cancellation exception that escapes a task makes it cancelled only after a
    /// stop: during a drain it is a failure.
    /// </para>
    /// <para>
    /// A drain already requested, or a token already cancelled, at the call starts no task, and the run outcome is
    /// <see cref="RunOutcome.Stopped"/>, unless the graph has no task. Both are read after the options and the journal have been
    /// checked, so those checks still throw first. When both are already made, <see cref="DrainRequested"/> comes first.
    /// </para>
    /// <para>
    /// The run outcome is <see cref="RunOutcome.Stopped"/> when a drain or a stop was applied and a task has not succeeded,
    /// and <see cref="RunOutcome.Complete"/> when every task succeeded, drain, stop or neither.
    /// </para>
    /// <para>
    /// A run on a journal opens it at the call, before the run exists: a new journal is created with its first line written, and one to
    /// continue is read in full. The run writes each step's entries before anything else the step does, and writes every settle before
    /// any dependent starts and before its event is published. With <see cref="JournalDurability.FlushedToDisk"/>, the default, each
    /// of those writes but a task's start is flushed to the disk first, and with <see cref="JournalDurability.HandedToOperatingSystem"/>
    /// none is. The run holds the journal for its whole
    /// life through a lock file beside it, <c>&lt;id&gt;.lock</c>, so that no other run can use it meanwhile. It closes the journal and lets
    /// go of the lock in the step that ends the run, after writing the run's end, however the run ends, so a later run can continue it
    /// as soon as this task completes. The lock file stays behind, and holds nothing.
    /// </para>
    /// <para>
    /// When a journal write fails during the run, for instance because the disk is full or the file is gone, Dagline stops the run
    /// itself: <see cref="StopRequested"/> is published with <see cref="StopRequester.Dagline"/>, unless a stop was already in progress,
    /// nothing new starts, the running tasks are signalled and the run waits the grace period, which neither a drain nor the
    /// cancellation of <paramref name="cancellationToken"/> shortens. A task that the failing step had already started still starts,
    /// and the stop signals it at once.
    /// No further write is attempted, though events still describe what happens. <see cref="RunEnded"/> carries a
    /// <see cref="JournalWriteException"/> and no run outcome, and this method throws it once reporters have handled every event.
    /// </para>
    /// <para>
    /// The journal exceptions are thrown at the call, after the options have been checked: no task has started, no event has been
    /// published and no reporter has been called. The checks run in this order: the options, the journal's lock, opening or creating
    /// the journal, reading it.
    /// </para>
    /// <para>
    /// Dagline never writes to the console, and hooks no console or process signal unless the app calls <see cref="ConsoleStop.Hook"/>:
    /// the app decides what requests a drain and what cancels the token.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="graph"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A reporter in <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="RunConfigurationException">
    /// <see cref="RunOptions.MaxRunningTasks"/> or a size is below 1, the graph names a limit that the options give no size, or the grace period is below zero
    /// or above <see cref="uint.MaxValue"/> - 1 milliseconds and is not <see cref="Timeout.InfiniteTimeSpan"/>. The message lists every problem.
    /// It is thrown at the call: no task has started, no event has been published and no reporter has been called.
    /// </exception>
    /// <exception cref="JournalInUseException">
    /// <see cref="RunOptions.Journal"/> continues a journal that another run holds, in this process or another.
    /// </exception>
    /// <exception cref="JournalNotFoundException">
    /// <see cref="RunOptions.Journal"/> continues a journal that is not in its directory.
    /// </exception>
    /// <exception cref="JournalAccessException">
    /// The journal, its lock file or its directory cannot be opened, created or read, or a new journal's first line cannot be written.
    /// </exception>
    /// <exception cref="JournalFormatException">
    /// The journal to continue cannot be read as one: its first line is not a journal header, its format version is unknown, or a
    /// line other than the last is not an entry.
    /// </exception>
    /// <exception cref="JournalWriteException">
    /// The run's journal could no longer be written during the run. It is thrown by the returned task, once the run has ended and
    /// reporters have handled every event, with the final snapshot in <see cref="JournalWriteException.Snapshot"/> and the cause as its
    /// inner exception. <see cref="RunResult.ReporterErrors"/> is not available.
    /// </exception>
    public static Task<RunResult> RunAsync(
        Graph graph,
        RunOptions options,
        DrainSignal? drain = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(options);

        if (options.RunsOnTheNewEngine)
        {
            return RunOnTheNewEngineAsync(graph, options, drain, cancellationToken);
        }

        IReporter[] reporters = [.. options.Reporters];
        Arguments.ThrowIfAnyNull(reporters, "A reporter in the options is null.", nameof(options));

        var settings = RunSettings.From(graph, options);
        var journal = options.Journal.Open(settings.TimeProvider);

        return RunToEndAsync(graph, settings, journal, reporters, drain, cancellationToken);
    }

    // The engine switch (spec #139), until the cutover. Not async, so that what LoopRun.Start throws is thrown at the call. A drain
    // signal already requested goes with the start, as the existing engine applies it; one requested later drains the run.
    private static Task<RunResult> RunOnTheNewEngineAsync(Graph graph, RunOptions options, DrainSignal? drain, CancellationToken cancellationToken)
    {
        var run = LoopRun.Start(graph, options, ThreadPoolHost.Instance, drain?.IsRequested ?? false, cancellationToken);
        return EndedAsTheExistingEngineEndsAsync(run, drain);
    }

    // A drain signal requested once the run has started drains it. A run that ended unrecorded throws what its end carried, as the
    // existing engine does, until the cutover (#153) makes unrecorded a run outcome.
    private static async Task<RunResult> EndedAsTheExistingEngineEndsAsync(LoopRun run, DrainSignal? drain)
    {
        using var hearsTheDrain = drain?.OnRequest(run.Drain);
        var result = await run.Result.ConfigureAwait(false);
        return result.Unrecorded is { } unrecorded ? throw unrecorded : result;
    }

    // The token is the run's own input, which stops it: the awaits here wait for the run to end however it ends, so none takes it.
    private static async Task<RunResult> RunToEndAsync(
        Graph graph,
        RunSettings settings,
        JournalFile? journal,
        IReporter[] reporters,
        DrainSignal? drain,
        CancellationToken cancellationToken)
    {
        var delivery = new ReporterDelivery(reporters);
        var run = new Run(graph, settings, delivery, drain, journal, cancellationToken);

        run.Start();
        await ((Task)run.Ended).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        var reporterErrors = await delivery.Delivered.ConfigureAwait(false);

        // Only now, once reporters have handled every event, does a run that ended with a JournalWriteException throw it.
        var outcome = await run.Ended.ConfigureAwait(false);
        return new RunResult(outcome, run.Current, reporterErrors);
    }
}
