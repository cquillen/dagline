namespace Dagline;

/// <summary>
/// A dependency as a snapshot shows it on the dependent node: a directed link to an upstream node.
/// </summary>
public sealed record Dependency
{
    internal Dependency(NodePath upstream, DependencyKind kind)
    {
        Upstream = upstream;
        Kind = kind;
    }

    /// <summary>The path of the upstream node.</summary>
    public NodePath Upstream { get; }

    /// <summary>What the upstream must have done for the dependency to be met.</summary>
    public DependencyKind Kind { get; }
}
