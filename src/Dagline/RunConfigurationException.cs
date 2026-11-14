namespace Dagline;

/// <summary>
/// Thrown by <see cref="Runner.RunAsync"/> when the options cannot work for the graph, before a run exists.
/// </summary>
/// <remarks>
/// <para>
/// The problems are a <see cref="RunOptions.MaxRunningTasks"/> below 1, a size below 1 in <see cref="RunOptions.Limits"/>, a limit the graph names
/// that the options give no size, and a <see cref="RunOptions.GracePeriod"/> below zero, or above
/// <see cref="uint.MaxValue"/> - 1 milliseconds (about 49.7 days), that is not <see cref="Timeout.InfiniteTimeSpan"/>. <see cref="Exception.Message"/> lists every problem found, one to a line,
/// so that all of them can be fixed in one pass.
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
    }
}
