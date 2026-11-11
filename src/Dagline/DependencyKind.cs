namespace Dagline;

/// <summary>
/// The kinds of dependency. A dependency orders work and carries no data.
/// </summary>
public enum DependencyKind
{
    /// <summary>Met when the upstream has succeeded.</summary>
    Requires,

    /// <summary>Met when the upstream has settled, whatever the outcome.</summary>
    After,
}
