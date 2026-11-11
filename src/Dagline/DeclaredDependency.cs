namespace Dagline;

/// <summary>A dependency as the consumer declared it on a handle, before the graph is built.</summary>
internal sealed record DeclaredDependency(Node Dependent, Node Upstream, DependencyKind Kind);
