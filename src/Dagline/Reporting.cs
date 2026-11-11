// PROTOTYPE (throwaway, stubs only). Reporters, events, snapshot, plain text.
namespace Dagline;

public interface IReporter
{
    /// <summary>Called in event order, one event at a time. The snapshot already includes the event. A throw drops the reporter.</summary>
    ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken);
}

public abstract record RunEvent(DateTimeOffset At);
public sealed record RunStarted(DateTimeOffset At) : RunEvent(At);
public sealed record TaskStatusChanged(DateTimeOffset At, NodePath Path, NodeStatus From, NodeStatus To) : RunEvent(At);
public sealed record WarningAdded(DateTimeOffset At, NodePath Path, string Warning) : RunEvent(At);
public sealed record WaitingChanged(DateTimeOffset At, NodePath Path, bool Waiting, string? Text) : RunEvent(At);
public sealed record GroupSettled(DateTimeOffset At, NodePath Path, NodeStatus Status) : RunEvent(At);

/// <summary>The first or second stop, as the run saw it.</summary>
public sealed record StopRequested(DateTimeOffset At, int Number) : RunEvent(At);
public sealed record RunEnded(DateTimeOffset At, RunOutcome Outcome) : RunEvent(At);

public sealed record NodeSnapshot
{
    public NodePath Path => throw new NotImplementedException();
    public string? Title => throw new NotImplementedException();
    public bool IsGroup => throw new NotImplementedException();
    public NodeStatus Status => throw new NotImplementedException();
    public bool IsEarlierSuccess => throw new NotImplementedException();
    public FailureKind? Failure => throw new NotImplementedException();
    public NodePath? BlockedBy => throw new NotImplementedException();
    public string? Message => throw new NotImplementedException();
    public IReadOnlyList<string> Detail => throw new NotImplementedException();
    public IReadOnlyList<string> Warnings => throw new NotImplementedException();
    public string? StatusText => throw new NotImplementedException();
    public long? Count => throw new NotImplementedException();
    public long? Total => throw new NotImplementedException();
    public DateTimeOffset? WaitingSince => throw new NotImplementedException();
    public DateTimeOffset? Started => throw new NotImplementedException();
    public DateTimeOffset? Ended => throw new NotImplementedException();
}

public sealed record BlockedGroup(NodePath Cause, IReadOnlyList<NodeSnapshot> Tasks);

/// <summary>Everything Dagline knows about the run at one instant. Immutable; nodes in declaration order.</summary>
public sealed class Snapshot
{
    public string? JournalDescription => throw new NotImplementedException();
    public RunOutcome? Outcome => throw new NotImplementedException();
    public bool Stopping => throw new NotImplementedException();
    public IReadOnlyList<NodeSnapshot> Nodes => throw new NotImplementedException();
    public NodeSnapshot this[NodePath path] => throw new NotImplementedException();

    // Summary queries.
    public IReadOnlyDictionary<NodeStatus, int> Counts => throw new NotImplementedException();
    public int EarlierSuccesses => throw new NotImplementedException();
    public IReadOnlyList<NodeSnapshot> Failed => throw new NotImplementedException();
    public IReadOnlyList<NodeSnapshot> Cancelled => throw new NotImplementedException();
    public IReadOnlyList<BlockedGroup> Blocked => throw new NotImplementedException();
    public IReadOnlyList<NodeSnapshot> NeverReached => throw new NotImplementedException();
    public int WarningCount => throw new NotImplementedException();
    public IReadOnlyList<NodeSnapshot> WithWarnings => throw new NotImplementedException();
    public int JournalPathsNotInGraph => throw new NotImplementedException();
    public int JournalPathsNotInGraphUnsucceeded => throw new NotImplementedException();
}

/// <summary>Line-per-event text for non-interactive output. Writes only to the writer it is given.</summary>
public sealed class PlainTextReporter : IReporter
{
    public PlainTextReporter(TextWriter writer) { }
    public ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken) => throw new NotImplementedException();
}
