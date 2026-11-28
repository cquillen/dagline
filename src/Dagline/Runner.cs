namespace Dagline;

/// <summary>The entry point: starts a run of a graph, or runs one to its end in one call.</summary>
public static class Runner
{
    /// <summary>
    /// Starts a run of every task of <paramref name="graph"/> in dependency order, and returns the run at once: the app can ask it
    /// to drain and to stop, read its current snapshot, and await its result.
    /// </summary>
    /// <param name="graph">The graph to run. It is not changed, and can be run again, one run after another or several at once.</param>
    /// <param name="options">The choices for this run.</param>
    /// <param name="cancellationToken">
    /// The token whose cancellation stops this run, as <see cref="Run.Stop"/> does: the two are one request, and whichever comes
    /// first applies the stop. A token already cancelled stops the run from its start. Cancelled after the run has ended, it does
    /// nothing, and its cancellation never makes the run's result throw.
    /// </param>
    /// <returns>
    /// The run, already started. Its <see cref="Run.Current"/> answers at once; its <see cref="Run.Result"/> completes when no task
    /// is running and none is ready, and every reporter has handled every event or been dropped.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Every task starts pending, except an earlier success: a task that the journal the run continues records as succeeded, which
    /// is succeeded from the start, never runs and gets no event (see <see cref="Journal.Continue"/>). A task becomes ready once every
    /// dependency it has is met. The run applies each thing that happens one at a time, in one loop of its own, so the order of a
    /// run's events depends only on the order in which task bodies end and requests arrive.
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
    /// and is never thrown from the run, and a task also fails when it reports a failure with <see cref="TaskContext.Fail"/>. Every task that requires the failed task, directly, through a group or through
    /// a chain of such tasks, becomes blocked at once and records the failed task in <see cref="NodeSnapshot.BlockedBy"/>.
    /// A task that runs <em>after</em> it still runs, and so does every task with no dependency on it.
    /// The run outcome is then <see cref="RunOutcome.Incomplete"/>.
    /// </para>
    /// <para>
    /// An app ends a run early in one of two ways. A <em>drain</em> is <see cref="Run.Drain"/>. Once the run has applied it, which
    /// publishes <see cref="DrainRequested"/>, nothing starts, <em>after</em> tasks included, and the running tasks finish as they
    /// would: their <see cref="TaskContext.CancellationToken"/> is not signalled and no time limit is set. Asking again, or once a
    /// stop has been applied, changes nothing.
    /// </para>
    /// <para>
    /// A <em>stop</em> is <see cref="Run.Stop"/> or the cancellation of <paramref name="cancellationToken"/>. Once the run has applied
    /// it, which publishes <see cref="StopRequested"/>, nothing starts, <em>after</em> tasks included, and
    /// <see cref="TaskContext.CancellationToken"/> is signalled on every running task. A stop can follow a drain, and is published
    /// after it. The run then waits for its running tasks to end, for at most <see cref="RunOptions.GracePeriod"/> on the run's clock,
    /// and only the grace period running out ends the wait. When it runs out with tasks still running, <see cref="GraceEnded"/> is
    /// published, each of them is recorded as cancelled in declaration order, and the run lets go of it: the run's result completes
    /// while its body may still be running, and nothing the body does afterwards changes the run. Cancelling the token, like each
    /// call on the run, only posts to the run, so none of the run's work happens on the thread that cancels it. The token a task
    /// sees is the run's own, signalled before <see cref="StopRequested"/> is published, and the grace period is already running
    /// when a reporter hears of the stop.
    /// </para>
    /// <para>
    /// While the run waits after a drain or a stop, a task that succeeds still makes its dependents ready, where they stay,
    /// and a task that fails still blocks its dependents. Tasks the run never reached keep their status, pending or ready, and are
    /// listed in <see cref="Snapshot.NeverReached"/>. A cancellation exception that escapes a task makes it cancelled only after a
    /// stop: during a drain it is a failure.
    /// </para>
    /// <para>
    /// A token already cancelled at the call starts no task: the run publishes <see cref="RunStarted"/>, the tasks that become ready,
    /// <see cref="StopRequested"/> and <see cref="RunEnded"/>, and the run outcome is <see cref="RunOutcome.Stopped"/>, unless the
    /// graph has no task. The token is read after the options and the journal have been checked, so those checks still throw first.
    /// </para>
    /// <para>
    /// The run outcome is <see cref="RunOutcome.Stopped"/> when a drain or a stop was applied and a task has not succeeded,
    /// and <see cref="RunOutcome.Complete"/> when every task succeeded, drain, stop or neither.
    /// </para>
    /// <para>
    /// Once the run has ended, it waits for its reporters to handle the events still queued for them, for at most 30 seconds on the
    /// run's clock from the moment <see cref="RunEnded"/> is queued, whether or not it was stopped. A reporter still busy then is
    /// dropped: the token passed to its <see cref="IReporter.OnEventAsync"/> is cancelled, its remaining events are let go, and the
    /// result completes without waiting for its call to come back. The run's own token has no part in this wait. A reporter's
    /// exception never faults the result either: the reporter is dropped and its error is on <see cref="RunResult.ReporterErrors"/>.
    /// </para>
    /// <para>
    /// A run on a journal opens it at the call, before the run exists: a new journal is created with its first line written, and one to
    /// continue is read in full. A task's body starts only once its start is written, and a settle is written before any dependent
    /// starts and before its event is published. With <see cref="JournalDurability.FlushedToDisk"/>, the default, each of those
    /// writes but a task's start is flushed to the disk first, and with <see cref="JournalDurability.HandedToOperatingSystem"/>
    /// none is. The run holds the journal for its whole
    /// life through a lock file beside it, <c>&lt;id&gt;.lock</c>, so that no other run can use it meanwhile. It closes the journal and lets
    /// go of the lock after writing the run's end and before publishing <see cref="RunEnded"/>, however the run ends, so a later run
    /// can continue it as soon as a reporter has heard that this one ended. The lock file stays behind, and holds nothing.
    /// </para>
    /// <para>
    /// When a journal write fails during the run, for instance because the disk is full or the file is gone, the run starts nothing
    /// more and Dagline stops it: <see cref="StopRequested"/> is published with <see cref="StopRequester.Dagline"/>, unless a stop
    /// was already applied, the running tasks are signalled and the run waits the grace period, which neither a drain nor a stop by
    /// the app shortens. A task whose start could not be written does not start, and the dependents of a task whose settle could
    /// not be written become ready and do not start. No further write is attempted, though events still describe what happens.
    /// The run ends with the run outcome <see cref="RunOutcome.Unrecorded"/>, which takes precedence over the other three, and
    /// nothing is thrown: <see cref="RunEnded.JournalError"/> and <see cref="RunResult.JournalError"/> give the file system's
    /// failure. Only a task whose checkpoint save could not be written is thrown a <see cref="JournalWriteException"/>. When only the
    /// entry for the run's end could not be written, every settle is on disk and the run keeps the outcome it reached, with the
    /// error beside it. Either way the journal has no end recorded, so <see cref="Journals.ListAsync"/> lists the run as
    /// <see cref="JournalEnding.Died"/>, and it can be continued once the cause is fixed.
    /// </para>
    /// <para>
    /// The journal exceptions are thrown at the call, after the options have been checked: no task has started, no event has been
    /// published and no reporter has been called. The checks run in this order: the options, the journal's lock, opening or creating
    /// the journal, reading it.
    /// </para>
    /// <para>
    /// Dagline never writes to the console, and hooks no console or process signal unless the app calls <see cref="ConsoleStop.Hook"/>
    /// with the run: the app decides what drains the run and what stops it.
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
    public static Run Start(Graph graph, RunOptions options, CancellationToken cancellationToken = default)
        => Run.Start(graph, options, ThreadPoolHost.Instance, cancellationToken);

    /// <summary>
    /// Runs every task of <paramref name="graph"/> in dependency order and returns once the run has ended: the same as awaiting
    /// <see cref="Run.Result"/> of the run that <see cref="Start"/> returns, for an app that never drains or reads the run.
    /// </summary>
    /// <param name="graph">The graph to run. It is not changed, and can be run again.</param>
    /// <param name="options">The choices for this run.</param>
    /// <param name="cancellationToken">
    /// The token whose cancellation stops this run. A token already cancelled stops the run from its start. Its cancellation never
    /// makes the returned task throw: the run ends and returns its result as usual.
    /// </param>
    /// <returns>The run's <see cref="Run.Result"/>. Everything <see cref="Start"/> says of a run holds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="graph"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A reporter in <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="RunConfigurationException">
    /// The options do not fit the graph, as <see cref="Start"/> says. It is thrown at the call, not by the returned task.
    /// </exception>
    /// <exception cref="JournalInUseException">
    /// <see cref="RunOptions.Journal"/> continues a journal that another run holds. It is thrown at the call.
    /// </exception>
    /// <exception cref="JournalNotFoundException">
    /// <see cref="RunOptions.Journal"/> continues a journal that is not in its directory. It is thrown at the call.
    /// </exception>
    /// <exception cref="JournalAccessException">
    /// The journal, its lock file or its directory cannot be opened, created or read. It is thrown at the call.
    /// </exception>
    /// <exception cref="JournalFormatException">The journal to continue cannot be read as one. It is thrown at the call.</exception>
    public static Task<RunResult> RunAsync(Graph graph, RunOptions options, CancellationToken cancellationToken = default)
        => Start(graph, options, cancellationToken).Result;
}
