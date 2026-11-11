using System.Collections.Immutable;

namespace Dagline;

/// <summary>Finds every problem that stops a graph from being built.</summary>
internal static class GraphValidation
{
    /// <summary>The problems of a graph that is being built, or none when it can be built.</summary>
    /// <param name="builder">The builder the graph is being built from.</param>
    /// <param name="root">The nodes at the root of the graph.</param>
    /// <param name="declared">Every dependency, in the order declared.</param>
    public static IReadOnlyList<GraphProblem> Problems(GraphBuilder builder, ChildNodes root, IReadOnlyList<DeclaredDependency> declared)
    {
        Node[] nodes = [.. root.InDeclarationOrder()];
        ChildNodes[] scopes = [root, .. nodes.OfType<GroupNode>().Select(group => group.Children)];
        (DeclaredDependency Dependency, GraphProblem? Problem)[] dependencies =
            [.. Merged(declared).Select(dependency => (dependency, ProblemWith(dependency, builder)))];

        // A dependency with a problem of its own is left out of the search, so that one mistake is one problem.
        DeclaredDependency[] sound = [.. dependencies.Where(checkedOne => checkedOne.Problem is null).Select(checkedOne => checkedOne.Dependency)];

        return
        [
            .. nodes.Where(node => !NodePath.IsSegment(node.Segment)).Select(InvalidSegment),
            .. scopes.SelectMany(DuplicatePaths),
            .. scopes.SelectMany(SiblingsThatDifferOnlyByCase),
            .. dependencies.Select(checkedOne => checkedOne.Problem).OfType<GraphProblem>(),
            .. DependencyCycles.Among(sound).Select(Cycle),
        ];
    }

    private static GraphProblem InvalidSegment(Node node)
    {
        var scope = node.Parent is null ? "at the root" : $"in '{node.Parent.Path}'";
        return new(Path: null, $"'{node.Segment}' is not a valid segment for a node {scope}: {NodePath.SegmentRule}");
    }

    private static IEnumerable<GraphProblem> DuplicatePaths(ChildNodes scope)
        => scope.Siblings
            .Where(node => NodePath.IsSegment(node.Segment))
            .GroupBy(node => node.Segment, StringComparer.Ordinal)
            .Where(sharing => sharing.Count() > 1)
            .Select(sharing => DuplicatePath([.. sharing]));

    private static GraphProblem DuplicatePath(Node[] sharing)
    {
        var first = sharing[0];
        var reopened = sharing.All(node => node is GroupNode)
            ? " Adding a group again does not reopen it: keep its handle to add more children."
            : string.Empty;

        return new(first.ValidPath, $"'{first.Path}' was added {sharing.Length} times: a path is one node.{reopened}");
    }

    private static IEnumerable<GraphProblem> SiblingsThatDifferOnlyByCase(ChildNodes scope)
        => scope.Siblings
            .Where(node => NodePath.IsSegment(node.Segment))
            .DistinctBy(node => node.Segment, StringComparer.Ordinal)
            .GroupBy(node => node.Segment, StringComparer.OrdinalIgnoreCase)
            .Where(alike => alike.Count() > 1)
            .Select(alike => DifferOnlyByCase([.. alike]));

    private static GraphProblem DifferOnlyByCase(Node[] alike)
    {
        var allButLast = string.Join(", ", alike[..^1].Select(Quoted));
        return new(
            alike[0].ValidPath,
            $"{allButLast} and {Quoted(alike[^1])} differ only by case: siblings must differ by more than case.");
    }

    /// <summary>One dependency for each dependent and upstream, in the order each pair was first declared.</summary>
    private static IEnumerable<DeclaredDependency> Merged(IEnumerable<DeclaredDependency> declared)
        => declared
            .GroupBy(dependency => (dependency.Dependent, dependency.Upstream), dependency => dependency.Kind)
            .Select(kinds => new DeclaredDependency(kinds.Key.Dependent, kinds.Key.Upstream, kinds.Strongest()));

    /// <summary>The problem with one dependency taken alone, or <see langword="null"/> when it has none.</summary>
    private static GraphProblem? ProblemWith(DeclaredDependency dependency, GraphBuilder builder)
    {
        var (dependent, upstream, kind) = dependency;
        var problem =
            upstream.Builder != builder ? $"{Sentence(dependency)}, a handle from another builder."
            : upstream == dependent ? $"{Quoted(dependent)} {kind.Verb()} itself."
            : dependent.Ancestors().Contains(upstream) ? $"{Sentence(dependency)}, its own ancestor."
            : upstream.Ancestors().Contains(dependent) ? $"{Sentence(dependency)}, its own descendant."
            : null;

        return problem is null ? null : new(dependent.ValidPath, problem);
    }

    private static GraphProblem Cycle(ImmutableArray<DeclaredDependency> cycle)
        => new(cycle[0].Dependent.ValidPath, $"These dependencies form a cycle: {string.Join(", ", cycle.Select(Sentence))}.");

    private static string Sentence(DeclaredDependency dependency)
        => $"{Quoted(dependency.Dependent)} {dependency.Kind.Verb()} {Quoted(dependency.Upstream)}";

    private static string Quoted(Node node) => $"'{node.Path}'";
}
