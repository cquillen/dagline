namespace Dagline;

/// <summary>The one home of each question asked of a dependency kind.</summary>
internal static class DependencyKindRules
{
    /// <summary>
    /// The one kind that stands for several dependencies of one dependent on one upstream: <em>requires</em> wins,
    /// because whatever meets a requires dependency also meets an after dependency.
    /// </summary>
    public static DependencyKind Strongest(this IEnumerable<DependencyKind> kinds)
        => kinds.Contains(DependencyKind.Requires) ? DependencyKind.Requires : DependencyKind.After;
}
