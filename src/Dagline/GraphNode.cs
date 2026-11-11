using System.Collections.Immutable;

namespace Dagline;

/// <summary>
/// A node of a built graph, as a run needs it. Nodes are numbered by their position in declaration order.
/// </summary>
/// <param name="Position">The position of this node among the nodes of the graph.</param>
/// <param name="Path">The identity of the node.</param>
/// <param name="Title">The title of the node, or <see langword="null"/> when it has none.</param>
/// <param name="Dependencies">The dependencies declared on this node, as a snapshot shows them.</param>
internal abstract record GraphNode(int Position, NodePath Path, string? Title, IReadOnlyList<Dependency> Dependencies);

/// <summary>A task of a built graph.</summary>
/// <param name="Position">The position of this task among the nodes of the graph.</param>
/// <param name="Path">The identity of the task.</param>
/// <param name="Title">The title of the task, or <see langword="null"/> when it has none.</param>
/// <param name="Dependencies">The dependencies declared on this task, as a snapshot shows them.</param>
/// <param name="Body">The work of the task.</param>
/// <param name="Upstreams">Every task this one waits for.</param>
/// <param name="Dependents">The positions of the tasks that wait for this one, in declaration order.</param>
internal sealed record GraphTask(
    int Position,
    NodePath Path,
    string? Title,
    IReadOnlyList<Dependency> Dependencies,
    TaskBody Body,
    ImmutableArray<Upstream> Upstreams,
    ImmutableArray<int> Dependents)
    : GraphNode(Position, Path, Title, Dependencies);

/// <summary>A group of a built graph.</summary>
/// <param name="Position">The position of this group among the nodes of the graph.</param>
/// <param name="Path">The identity of the group.</param>
/// <param name="Title">The title of the group, or <see langword="null"/> when it has none.</param>
/// <param name="Dependencies">The dependencies declared on this group, as a snapshot shows them.</param>
internal sealed record GraphGroup(int Position, NodePath Path, string? Title, IReadOnlyList<Dependency> Dependencies)
    : GraphNode(Position, Path, Title, Dependencies);

/// <summary>One task that another waits for, and what it must have done for the wait to be over.</summary>
/// <param name="Position">The position of the upstream task among the nodes of the graph.</param>
/// <param name="Kind">The kind of dependency.</param>
internal readonly record struct Upstream(int Position, DependencyKind Kind);
