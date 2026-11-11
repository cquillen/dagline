namespace Dagline;

/// <summary>
/// Thrown by <see cref="GraphBuilder.Build"/> when the graph cannot be built. It carries every problem that was found,
/// so that all of them can be fixed in one pass.
/// </summary>
/// <remarks>
/// No graph exists when this is thrown, so nothing can have run.
/// </remarks>
public sealed class GraphException : DaglineException
{
    internal GraphException(IReadOnlyList<GraphProblem> problems)
        : base(string.Join(Environment.NewLine, problems.Select(problem => $"  {problem.Message}").Prepend("The graph cannot be built:")))
    {
        Problems = problems;
    }

    /// <summary>
    /// Every problem found in the graph, never empty. <see cref="Exception.Message"/> lists the same problems, one to a line.
    /// </summary>
    public IReadOnlyList<GraphProblem> Problems { get; }
}
