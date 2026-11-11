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
    /// A line for each problem with the cap and the sizes in the options of a run of <paramref name="graph"/>:
    /// the cap or a size is below 1, or the graph names a limit that has no size.
    /// </summary>
    public static IEnumerable<string> ProblemsWith(Graph graph, RunOptions options)
    {
        var given = Given(options);
        if (options.Cap < 1)
        {
            yield return Invariant($"The cap is {options.Cap}. It must be at least 1.");
        }

        foreach (var (name, size) in given.Where(limit => limit.Value < 1))
        {
            yield return Invariant($"The size of the limit '{name}' is {size}. It must be at least 1.");
        }

        foreach (var name in graph.LimitNames.Where(name => !given.ContainsKey(name)))
        {
            yield return $"The graph names the limit '{name}', and the options give it no size.";
        }
    }

    /// <summary>
    /// Reads the cap and the sizes from options that <see cref="ProblemsWith"/> found no problem with.
    /// A size for a limit the graph does not name is left out.
    /// </summary>
    public static LimitSizes For(Graph graph, RunOptions options)
    {
        var given = Given(options);
        return new LimitSizes(options.Cap, [.. graph.LimitNames.Select(name => given[name])]);
    }

    // Read into a dictionary of our own, so that names compare as limit names do whatever comparer the consumer's dictionary uses.
    private static Dictionary<LimitName, int> Given(RunOptions options)
    {
        Dictionary<LimitName, int> given = [];
        foreach (var (name, size) in options.Limits)
        {
            given[new LimitName(name)] = size;
        }

        return given;
    }
}
