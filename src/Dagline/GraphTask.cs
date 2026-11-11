using System.Collections.Immutable;

namespace Dagline;

/// <summary>
/// A task of a built graph, as a run needs it. Nodes are numbered by their position in declaration order.
/// </summary>
/// <param name="Position">The position of this task among the nodes of the graph.</param>
/// <param name="Path">The identity of the task.</param>
/// <param name="Body">The work of the task.</param>
/// <param name="Dependencies">The dependencies declared on this task, as a snapshot shows them.</param>
/// <param name="Upstreams">Every task this one waits for.</param>
/// <param name="Dependents">The positions of the tasks that wait for this one, in declaration order.</param>
internal sealed record GraphTask(
    int Position,
    NodePath Path,
    TaskBody Body,
    IReadOnlyList<Dependency> Dependencies,
    ImmutableArray<Upstream> Upstreams,
    ImmutableArray<int> Dependents);

/// <summary>One task that another waits for, and what it must have done for the wait to be over.</summary>
/// <param name="Position">The position of the upstream task among the nodes of the graph.</param>
/// <param name="Kind">The kind of dependency.</param>
internal readonly record struct Upstream(int Position, DependencyKind Kind);
