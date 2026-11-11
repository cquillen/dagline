namespace Dagline;

/// <summary>The one home of how a list of problems is written as the message of an exception.</summary>
internal static class ProblemsText
{
    /// <summary>
    /// A heading line, then each problem on a line of its own, as in <c>- The cap is 0. It must be at least 1.</c>
    /// </summary>
    public static string Under(string heading, IEnumerable<string> problems)
        => string.Join(Environment.NewLine, problems.Select(problem => $"- {problem}").Prepend(heading));
}
