using System.Collections.Immutable;
using static System.FormattableString;

namespace Dagline;

/// <summary>
/// The sizes one run gives the cap and each limit its graph names, checked before the run exists.
/// </summary>
internal sealed class LimitSizes
{
    private LimitSizes(int cap, ImmutableArray<int> limits)
    {
        Cap = cap;
        Limits = limits;
    }

    /// <summary>The most tasks that may be running at once. At least 1.</summary>
    public int Cap { get; }

    /// <summary>The size of each limit the graph names, in the order of <see cref="Graph.LimitNames"/>. Each is at least 1.</summary>
    public ImmutableArray<int> Limits { get; }

    /// <summary>
    /// Reads the cap and the sizes from the options of a run of <paramref name="graph"/>.
    /// A size for a limit the graph does not name is left out.
    /// </summary>
    /// <exception cref="RunConfigurationException">
    /// The cap or a size is below 1, or the graph names a limit that has no size. The message lists every problem.
    /// </exception>
    public static LimitSizes For(Graph graph, RunOptions options)
    {
        // Read into a dictionary of our own, so that names compare as limit names do whatever comparer the consumer's dictionary uses.
        Dictionary<LimitName, int> given = [];
        foreach (var (name, size) in options.Limits)
        {
            given[new LimitName(name)] = size;
        }

        List<string> problems = [];
        if (options.Cap < 1)
        {
            problems.Add(Invariant($"The cap is {options.Cap}. It must be at least 1."));
        }

        problems.AddRange(given
            .Where(limit => limit.Value < 1)
            .Select(limit => Invariant($"The size of the limit '{limit.Key}' is {limit.Value}. It must be at least 1.")));
        problems.AddRange(graph.LimitNames
            .Where(name => !given.ContainsKey(name))
            .Select(name => $"The graph names the limit '{name}', and the options give it no size."));

        return problems.Count == 0
            ? new LimitSizes(options.Cap, [.. graph.LimitNames.Select(name => given[name])])
            : throw new RunConfigurationException(problems);
    }
}
