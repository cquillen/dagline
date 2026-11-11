using System.Collections.Immutable;

namespace Dagline;

/// <summary>
/// A node of a built graph, as a run needs it. Nodes are numbered by their position in declaration order.
/// </summary>
/// <param name="Position">The position of this node among the nodes of the graph.</param>
/// <param name="Path">The identity of the node.</param>
/// <param name="Title">The title of the node, or <see langword="null"/> when it has none.</param>
/// <param name="Dependencies">The dependencies declared on this node, as a snapshot shows them.</param>
/// <param name="Limits">The limits named on this node, as a snapshot shows them.</param>
/// <param name="Dependents">The positions of the tasks that depend on this node, in declaration order.</param>
internal abstract record GraphNode(
    int Position,
    NodePath Path,
    string? Title,
    IReadOnlyList<Dependency> Dependencies,
    IReadOnlyList<string> Limits,
    ImmutableArray<int> Dependents);

/// <summary>A task of a built graph.</summary>
/// <param name="Position">The position of this task among the nodes of the graph.</param>
/// <param name="Path">The identity of the task.</param>
/// <param name="Title">The title of the task, or <see langword="null"/> when it has none.</param>
/// <param name="Dependencies">The dependencies declared on this task, as a snapshot shows them.</param>
/// <param name="Limits">The limits named on this task, as a snapshot shows them.</param>
/// <param name="Dependents">The positions of the tasks that depend on this task, in declaration order.</param>
/// <param name="Body">The work of the task.</param>
/// <param name="Groups">The positions of the groups that hold this task, the nearest first.</param>
/// <param name="Upstreams">Every node this task depends on, whether the dependency was declared on it or on a group that holds it.</param>
/// <param name="HeldLimits">
/// Every limit this task holds, whether it was named on the task or on a group that holds it,
/// each as its position among <see cref="Graph.LimitNames"/>.
/// </param>
internal sealed record GraphTask(
    int Position,
    NodePath Path,
    string? Title,
    IReadOnlyList<Dependency> Dependencies,
    IReadOnlyList<string> Limits,
    ImmutableArray<int> Dependents,
    TaskBody Body,
    ImmutableArray<int> Groups,
    ImmutableArray<Upstream> Upstreams,
    ImmutableArray<int> HeldLimits)
    : GraphNode(Position, Path, Title, Dependencies, Limits, Dependents)
{
    /// <summary>Whether this task depends on the node at a position, by either kind of dependency.</summary>
    public bool IsUpstream(int position) => Upstreams.Any(upstream => upstream.Position == position);
}

/// <summary>A group of a built graph.</summary>
/// <param name="Position">The position of this group among the nodes of the graph.</param>
/// <param name="Path">The identity of the group.</param>
/// <param name="Title">The title of the group, or <see langword="null"/> when it has none.</param>
/// <param name="Dependencies">The dependencies declared on this group, as a snapshot shows them.</param>
/// <param name="Limits">The limits named on this group, as a snapshot shows them.</param>
/// <param name="Dependents">The positions of the tasks that depend on this group, and so on every task beneath it, in declaration order.</param>
/// <param name="Depth">How many groups hold this one: zero for a group at the root.</param>
/// <param name="TaskCount">How many tasks are beneath this group, at any depth.</param>
internal sealed record GraphGroup(
    int Position,
    NodePath Path,
    string? Title,
    IReadOnlyList<Dependency> Dependencies,
    IReadOnlyList<string> Limits,
    ImmutableArray<int> Dependents,
    int Depth,
    int TaskCount)
    : GraphNode(Position, Path, Title, Dependencies, Limits, Dependents);

/// <summary>
/// One node that a task depends on, and what it must have done for the dependency to be met.
/// A group has succeeded once every task beneath it has, and has settled once every task beneath it has.
/// </summary>
/// <param name="Position">The position of the upstream node among the nodes of the graph.</param>
/// <param name="Kind">The kind of dependency.</param>
internal readonly record struct Upstream(int Position, DependencyKind Kind);
