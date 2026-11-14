namespace Dagline;

/// <summary>
/// Thrown by <see cref="Runner.RunAsync"/> when the options cannot work for the graph, before a run exists.
/// </summary>
/// <remarks>
/// <para>
/// The problems are a <see cref="RunOptions.MaxRunningTasks"/> below 1, a size below 1 in <see cref="RunOptions.Limits"/>, a limit the graph names
/// that the options give no size, and a <see cref="RunOptions.GracePeriod"/> below zero, or above
/// <see cref="uint.MaxValue"/> - 1 milliseconds (about 49.7 days), that is not <see cref="Timeout.InfiniteTimeSpan"/>.
/// <see cref="Problems"/> lists every problem found, so that all of them can be fixed in one pass, and <see cref="Exception.Message"/>
/// lists them too, one to a line.
/// </para>
/// <para>
/// The exception is thrown at the call. No task has started, no event has been published and no reporter has been called.
/// </para>
/// </remarks>
public sealed class RunConfigurationException : DaglineException
{
    internal RunConfigurationException(IReadOnlyList<string> problems)
        : base(ProblemsText.Under("The options cannot be used to run the graph:", problems))
    {
        Problems = problems;
    }

    /// <summary>The message of each problem found in the options. There is always at least one.</summary>
    /// <remarks>
    /// <see cref="RunOptions.MaxRunningTasks"/> comes first, then sizes below 1 in <see cref="RunOptions.Limits"/>, then limits the
    /// graph names with no size, then <see cref="RunOptions.GracePeriod"/>. <see cref="Exception.Message"/> is a heading line followed
    /// by each of these, one to a line and in this order, as in <c>- MaxRunningTasks is 0. It must be at least 1.</c>
    /// </remarks>
    public IReadOnlyList<string> Problems { get; }
}
