namespace Dagline;

/// <summary>A reporter that writes a run as lines of text.</summary>
public sealed class PlainTextReporter : IReporter
{
    private readonly Lock _gate = new();
    private readonly TextWriter _writer;
    private BlockedRun? _blocked;

    /// <summary>Makes a reporter that writes to <paramref name="writer"/>.</summary>
    /// <param name="writer">Where the lines go.</param>
    /// <param name="timeProvider">The clock.</param>
    public PlainTextReporter(TextWriter writer, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
    }

    /// <inheritdoc />
    public ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runEvent);
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_gate)
        {
            Handle(runEvent, snapshot);
        }

        return ValueTask.CompletedTask;
    }

    private void Handle(RunEvent runEvent, Snapshot snapshot)
    {
        var blockedBy = runEvent is TaskStatusChanged { To: NodeStatus.Blocked } blocked ? blocked.Node.BlockedBy : null;
        if (blockedBy is { } sameCause && _blocked?.Cause == sameCause)
        {
            _blocked = _blocked.Value with { Tasks = _blocked.Value.Tasks + 1 };
            return;
        }

        var blockedLine = TakeBlockedLine();
        if (blockedBy is { } cause)
        {
            _blocked = new BlockedRun(runEvent.At, cause, Tasks: 1);
        }

        Print([.. blockedLine, .. PlainTextLines.About(runEvent, snapshot)]);
    }

    /// <summary>The line for the blocked events counted so far, which are counted no further. Empty when there are none.</summary>
    private string[] TakeBlockedLine()
    {
        if (_blocked is not { } run)
        {
            return [];
        }

        _blocked = null;
        return [PlainTextLines.Blocked(run.At, run.Tasks, run.Cause)];
    }

    private void Print(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        // One call for all the lines of one event, so that a writer the app also writes to cannot put a line of its own among them.
        _writer.Write(string.Concat(lines.Select(line => line + _writer.NewLine)));
        _writer.Flush();
    }

    /// <summary>Consecutive blocked events with one cause, which are printed as one line.</summary>
    private readonly record struct BlockedRun(DateTimeOffset At, NodePath Cause, int Tasks);
}
