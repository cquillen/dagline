// PROTOTYPE (throwaway, stubs only). The Dagline.Testing package.
namespace Dagline.Testing;

/// <summary>What the final snapshot holds for the one task.</summary>
public sealed record TaskOutcome(
    NodeStatus Status,
    FailureKind? Failure,
    string? Message,
    IReadOnlyList<string> Detail,
    IReadOnlyList<string> Warnings,
    bool Waiting);

/// <summary>Runs one task body as a one-task run on its own temporary journal. Dispose deletes the journal.</summary>
public sealed class TaskHarness : IAsyncDisposable
{
    public static TaskHarness For(TaskBody body, TimeProvider? clock = null) => throw new NotImplementedException();

    /// <summary>A run. A second call continues the same journal, so checkpoints are handed back.</summary>
    public Task<TaskOutcome> RunAsync() => throw new NotImplementedException();

    /// <summary>Runs until the task is waiting, then stops. The way to end a first run.</summary>
    public Task<TaskOutcome> RunUntilWaitingThenStopAsync() => throw new NotImplementedException();

    public ValueTask DisposeAsync() => throw new NotImplementedException();
}

/// <summary>Every delay ends at once and the time jumps forward by the delay. Fails the test, with ClockGuardException, after a set amount of fake time.</summary>
public sealed class InstantClock : TimeProvider
{
    public InstantClock(TimeSpan? guard = null) { }
}

public sealed class ClockGuardException : Exception { }
