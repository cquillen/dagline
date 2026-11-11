namespace Dagline;

/// <summary>Finds every problem that stops a graph from being built.</summary>
internal static class GraphValidation
{
    /// <summary>The problems of a graph that is being built, or none when it can be built.</summary>
    /// <param name="root">The nodes at the root of the graph.</param>
    public static IReadOnlyList<GraphProblem> Problems(ChildNodes root)
    {
        Node[] nodes = [.. root.InDeclarationOrder()];
        ChildNodes[] scopes = [root, .. nodes.OfType<GroupNode>().Select(group => group.Children)];

        return
        [
            .. nodes.Where(node => !NodePath.IsSegment(node.Segment)).Select(InvalidSegment),
            .. scopes.SelectMany(DuplicatePaths),
            .. scopes.SelectMany(SiblingsThatDifferOnlyByCase),
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

    private static string Quoted(Node node) => $"'{node.Path}'";
}
