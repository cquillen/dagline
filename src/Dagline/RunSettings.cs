namespace Dagline;

/// <summary>What one run takes from its options once they have been checked against the graph.</summary>
/// <param name="Sizes">The sizes of the cap and of each limit the graph names.</param>
/// <param name="GracePeriod">
/// How long the run waits for its running tasks after a stop: zero or more, or <see cref="Timeout.InfiniteTimeSpan"/>.
/// </param>
/// <param name="TimeProvider">The clock of the run.</param>
internal sealed record RunSettings(LimitSizes Sizes, TimeSpan GracePeriod, TimeProvider TimeProvider)
{
    /// <summary>
    /// Checks the options of a run of <paramref name="graph"/> and reads its settings from them. This is the one place
    /// the options are checked: every problem found is gathered into one exception.
    /// </summary>
    /// <exception cref="RunConfigurationException">
    /// The cap or a size is below 1, the graph names a limit that has no size, or the grace period is not allowed.
    /// </exception>
    public static RunSettings From(Graph graph, RunOptions options)
    {
        List<string> problems = [.. LimitSizes.ProblemsWith(graph, options)];
        if (GracePeriodRules.ProblemWith(options.GracePeriod) is { } gracePeriodProblem)
        {
            problems.Add(gracePeriodProblem);
        }

        return problems.Count > 0
            ? throw new RunConfigurationException(problems)
            : new RunSettings(LimitSizes.For(graph, options), options.GracePeriod, options.TimeProvider);
    }
}
