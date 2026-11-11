using System.Diagnostics;

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

    /// <summary>
    /// Dependencies that were declared more than once, taken as one each: one kind for each key, in the order each key
    /// was first declared, which is the <see cref="Strongest"/> of the kinds declared for that key.
    /// </summary>
    /// <param name="declared">The dependencies, in the order declared.</param>
    /// <param name="same">What two declarations share when they are one dependency, such as their upstream.</param>
    public static IEnumerable<(TKey Key, DependencyKind Kind)> MergedBy<TKey>(
        this IEnumerable<DeclaredDependency> declared,
        Func<DeclaredDependency, TKey> same)
        => declared.GroupBy(same, dependency => dependency.Kind).Select(kinds => (kinds.Key, kinds.Strongest()));

    /// <summary>The words that join a dependent to its upstream in a sentence about a dependency of this kind.</summary>
    public static string Verb(this DependencyKind kind)
        => kind switch
        {
            DependencyKind.Requires => "requires",
            DependencyKind.After => "runs after",
            _ => throw new UnreachableException(),
        };
}
