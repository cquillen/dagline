namespace Dagline;

/// <summary>
/// Thrown by <see cref="GraphBuilder.Build"/> when the graph cannot be built. It carries every problem that was found,
/// so that all of them can be fixed in one pass.
/// </summary>
/// <remarks>
/// <para>
/// No graph exists when this is thrown, so no task has run. <see cref="GraphBuilder.Build"/> lists the kinds of problem.
/// </para>
/// <para>
/// One mistake is one problem. An invalid segment is reported for the node it was given to and not again for each node beneath it,
/// a path added several times is reported once, and a dependency that is a problem taken alone is not reported again as part of a cycle.
/// </para>
/// <para>
/// Where cycles share dependencies, at least one of them is reported and not necessarily each of them,
/// so removing a dependency from every cycle reported can leave another for the next call to find.
/// </para>
/// </remarks>
public sealed class GraphException : DaglineException
{
    internal GraphException(IReadOnlyList<GraphProblem> problems)
        : base(string.Join(Environment.NewLine, problems.Select(problem => $"  {problem.Message}").Prepend("The graph cannot be built:")))
    {
        Problems = problems;
    }

    /// <summary>Every problem found in the graph. There is always at least one.</summary>
    /// <remarks>
    /// Problems with nodes come first (invalid segments, then duplicate paths, then siblings that differ only by case),
    /// then problems with one dependency, then cycles. The same calls on a builder always give the same list.
    /// <see cref="Exception.Message"/> is a heading line followed by the message of each problem, one to a line, in this order.
    /// </remarks>
    public IReadOnlyList<GraphProblem> Problems { get; }
}
