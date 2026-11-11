using System.Collections.Immutable;

namespace Dagline;

/// <summary>
/// Everything Dagline knows about a run at one instant. A snapshot is immutable and consistent:
/// it never changes after it is made, and everything in it is as of the same instant.
/// </summary>
public sealed class Snapshot
{
    internal Snapshot(DateTimeOffset started, DateTimeOffset? ended, ImmutableArray<NodeSnapshot> nodes)
    {
        Started = started;
        Ended = ended;
        Nodes = nodes;
    }

    /// <summary>When the run started, read from the run's <see cref="RunOptions.TimeProvider"/>.</summary>
    public DateTimeOffset Started { get; }

    /// <summary>When the run ended, or <see langword="null"/> while it has not.</summary>
    public DateTimeOffset? Ended { get; }

    /// <summary>Every node of the graph, in declaration order.</summary>
    public IReadOnlyList<NodeSnapshot> Nodes { get; }
}
