namespace Dagline;

/// <summary>The entry point: runs a graph to its end.</summary>
public static class Runner
{
    /// <summary>Runs every task of <paramref name="graph"/> in dependency order and returns once the run has ended.</summary>
    /// <param name="graph">The graph to run. It is not changed, and can be run again.</param>
    /// <param name="options">The choices for this run.</param>
    /// <returns>
    /// A task that completes when no task is running and none is ready, and every reporter has been handed every event.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Every task starts pending. A task becomes ready once every dependency it has is met, and every ready task is started
    /// on the thread pool, in declaration order. Each change of status is applied one at a time,
    /// so the order of a run's events depends only on the order in which task bodies end.
    /// </para>
    /// <para>
    /// The method takes no <see cref="CancellationToken"/>: a run cannot be stopped.
    /// Dagline never writes to the console.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="graph"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A reporter in <paramref name="options"/> is <see langword="null"/>.</exception>
    public static Task<RunResult> RunAsync(Graph graph, RunOptions options)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(options);

        IReporter[] reporters = [.. options.Reporters];
        if (reporters.Contains(null))
        {
            throw new ArgumentException("A reporter in the options is null.", nameof(options));
        }

        return RunToEndAsync(graph, options.TimeProvider, reporters);
    }

    private static async Task<RunResult> RunToEndAsync(Graph graph, TimeProvider timeProvider, IReporter[] reporters)
    {
        var delivery = new ReporterDelivery(reporters);
        var run = new Run(graph, timeProvider, delivery);

        run.Start();
        var outcome = await run.Ended.ConfigureAwait(false);
        await delivery.Delivered.ConfigureAwait(false);

        return new RunResult(outcome, run.Current);
    }
}
