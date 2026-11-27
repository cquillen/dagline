namespace Dagline;

/// <summary>The facts of a drain that a run has applied, as <see cref="Snapshot.Drain"/> gives them.</summary>
public sealed record DrainState
{
    internal DrainState(DateTimeOffset requestedAt)
    {
        RequestedAt = requestedAt;
    }

    /// <summary>When the run applied the drain: the time of its <see cref="DrainRequested"/> event.</summary>
    public DateTimeOffset RequestedAt { get; }
}
