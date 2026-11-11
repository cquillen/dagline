namespace Dagline;

/// <summary>The entry point: runs a graph to its end.</summary>
public static class Runner
{
    /// <summary>Runs every task of <paramref name="graph"/> in dependency order and returns once the run has ended.</summary>
    /// <param name="graph">The graph to run. It is not changed, and can be run again.</param>
    /// <param name="options">The choices for this run.</param>
    /// <returns>
    /// A task that completes when no task is running and none is ready, and every reporter that was not dropped
    /// has handled every event. That wait has no time bound. A reporter's exception never faults the task:
    /// the reporter is dropped and its error is on <see cref="RunResult.ReporterErrors"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Every task starts pending. A task becomes ready once every dependency it has is met. Each change of status is applied
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
    /// <see cref="RunOptions.Limits"/> must be at least 1, and every limit the graph names must have a size.
    /// A size for a limit the graph does not name is ignored.
    /// </para>
    /// <para>
    /// A task that fails costs only the work that requires it. An exception that escapes a task body fails that task
    /// and is never thrown from this method. Every task that requires the failed task, directly, through a group or through
    /// a chain of such tasks, becomes blocked at once and records the failed task in <see cref="NodeSnapshot.BlockedBy"/>.
    /// A task that runs <em>after</em> it still runs, and so does every task with no dependency on it.
    /// The run outcome is then <see cref="RunOutcome.Incomplete"/>.
    /// </para>
    /// <para>
    /// The method takes no <see cref="CancellationToken"/>: a run cannot be stopped.
    /// Dagline never writes to the console.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="graph"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A reporter in <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="RunConfigurationException">
    /// The cap or a size is below 1, or the graph names a limit that the options give no size. The message lists every problem.
    /// It is thrown at the call: no task has started, no event has been published and no reporter has been called.
    /// </exception>
    public static Task<RunResult> RunAsync(Graph graph, RunOptions options)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(options);

        IReporter[] reporters = [.. options.Reporters];
        if (reporters.Contains(null))
        {
            throw new ArgumentException("A reporter in the options is null.", nameof(options));
        }

        return RunToEndAsync(graph, LimitSizes.For(graph, options), options.TimeProvider, reporters);
    }

    // No token, by design: a run is not stopped through a CancellationToken, so there is none to pass on.
    private static async Task<RunResult> RunToEndAsync(
        Graph graph,
        LimitSizes sizes,
        TimeProvider timeProvider,
        IReporter[] reporters)
    {
        var delivery = new ReporterDelivery(reporters);
        var run = new Run(graph, sizes, timeProvider, delivery);

        run.Start();
        var outcome = await run.Ended.ConfigureAwait(false);
        var reporterErrors = await delivery.Delivered.ConfigureAwait(false);

        return new RunResult(outcome, run.Current, reporterErrors);
    }
}
