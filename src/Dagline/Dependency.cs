namespace Dagline;

/// <summary>
/// A dependency as a snapshot shows it on the dependent node: a directed link to an upstream node.
/// </summary>
/// <param name="Upstream">The path of the upstream node.</param>
/// <param name="Kind">What the upstream must have done for the dependency to be met.</param>
public sealed record Dependency(NodePath Upstream, DependencyKind Kind);
