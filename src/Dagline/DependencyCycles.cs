using System.Collections.Immutable;

namespace Dagline;

/// <summary>Finds the cycles among the dependencies of a graph that is being built.</summary>
/// <remarks>
/// <para>
/// One dependency leads on to another when the dependent of the second is <em>in line with</em> the upstream of the first:
/// it is that upstream, a group that holds it, or a node beneath it. The upstream cannot be met before the second dependency is,
/// because a node has the dependencies declared on every group that holds it, and a group stands for whatever is beneath it.
/// A cycle is a chain of such steps that comes back to the dependency it started from.
/// </para>
/// <para>
/// A group counts whether or not it has tasks, so a cycle through an empty group is found.
/// </para>
/// </remarks>
internal sealed class DependencyCycles
{
    private readonly ILookup<Node, DeclaredDependency> _declaredOn;
    private readonly Dictionary<DeclaredDependency, int> _declarationIndex;
    private readonly HashSet<Node> _entered = [];
    private readonly List<ImmutableArray<DeclaredDependency>> _cycles = [];

    private DependencyCycles(IReadOnlyList<DeclaredDependency> dependencies)
    {
        _declaredOn = dependencies.ToLookup(dependency => dependency.Dependent);
        _declarationIndex = dependencies.Index().ToDictionary(dependency => dependency.Item, dependency => dependency.Index);
    }

    /// <summary>
    /// One cycle for each step that arrives back on the chain it came along, so at least one whenever there is any.
    /// Each is its dependencies in the order they lead on to one another, starting from the one declared first.
    /// </summary>
    /// <param name="dependencies">
    /// The dependencies to search, in the order declared: one for each dependent and upstream,
    /// with both nodes in the same graph and neither in line with the other.
    /// </param>
    public static IReadOnlyList<ImmutableArray<DeclaredDependency>> Among(IReadOnlyList<DeclaredDependency> dependencies)
    {
        var search = new DependencyCycles(dependencies);
        foreach (var dependency in dependencies)
        {
            search.ExploreFrom(dependency);
        }

        return search._cycles;
    }

    /// <summary>
    /// Walks depth first along every chain of dependencies that starts with one dependency, and records a cycle
    /// each time a step arrives back at an upstream of the chain it is on.
    /// </summary>
    /// <remarks>
    /// The walk keeps its own stack, because a chain can be as long as the graph and would overflow the call stack.
    /// </remarks>
    private void ExploreFrom(DeclaredDependency first)
    {
        List<Step> chain = [];
        Dictionary<Node, int> positions = [];

        Follow(first);
        while (chain.Count > 0)
        {
            var upstream = chain[^1].Via.Upstream;
            if (!chain[^1].Onward.TryDequeue(out var next))
            {
                chain.RemoveAt(chain.Count - 1);
                positions.Remove(upstream);
            }
            else if (positions.TryGetValue(next.Upstream, out var position))
            {
                // The steps after that upstream lead away from it, and this one leads back.
                _cycles.Add(FromEarliestDeclared([.. chain.Skip(position + 1).Select(step => step.Via), next]));
            }
            else
            {
                Follow(next);
            }
        }

        void Follow(DeclaredDependency dependency)
        {
            if (_entered.Add(dependency.Upstream))
            {
                positions[dependency.Upstream] = chain.Count;
                chain.Add(new Step(dependency, Onward(dependency.Upstream)));
            }
        }
    }

    /// <summary>The dependencies that must be met before a node can be, one for each upstream they lead to.</summary>
    private Queue<DeclaredDependency> Onward(Node node)
        => new(InLineWith(node).SelectMany(inLine => _declaredOn[inLine]).DistinctBy(dependency => dependency.Upstream));

    private static IEnumerable<Node> InLineWith(Node node)
        => [.. node.WithAncestors(), .. node.NodesBeneath()];

    private ImmutableArray<DeclaredDependency> FromEarliestDeclared(DeclaredDependency[] cycle)
    {
        var earliest = Array.IndexOf(cycle, cycle.MinBy(dependency => _declarationIndex[dependency]));
        return [.. cycle[earliest..], .. cycle[..earliest]];
    }

    /// <summary>One step of a chain: the dependency followed, and those still to follow from its upstream.</summary>
    private sealed record Step(DeclaredDependency Via, Queue<DeclaredDependency> Onward);
}
