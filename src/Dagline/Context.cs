// PROTOTYPE (throwaway, stubs only). What a task sees and does.
namespace Dagline;

/// <summary>The seven statuses. They only move forward.</summary>
public enum NodeStatus { Pending, Ready, Running, Succeeded, Failed, Blocked, Cancelled }

/// <summary>Why a task is Failed.</summary>
public enum FailureKind { Reported, Unexpected }

/// <summary>Everything a running task reports goes through here. Members throw InvalidOperationException after Dagline has let go of the task.</summary>
public sealed class TaskContext
{
    public NodePath Path => throw new NotImplementedException();
    /// <summary>Signalled by a stop. Pass it to everything awaited.</summary>
    public CancellationToken CancellationToken => throw new NotImplementedException();
    /// <summary>The run's clock. Use it for any delay of your own.</summary>
    public TimeProvider TimeProvider => throw new NotImplementedException();

    // Progress: live only.
    public void Status(string? text) => throw new NotImplementedException();
    public void Count(long current, long? total = null) => throw new NotImplementedException();

    // Outcome: allowed whatever status the task ends in.
    public void Warn(string warning) => throw new NotImplementedException();
    public void Message(string message) => throw new NotImplementedException();
    public void Detail(params string[] lines) => throw new NotImplementedException();
    /// <summary>Records a reported failure (message plus detail lines) and returns the exception to throw: <c>throw ctx.Fail("3 bad entities", lines);</c></summary>
    public ReportedFailureException Fail(string message, params string[] detail) => throw new NotImplementedException();

    /// <summary>Status only, of a node this task depends on. Any other node throws ArgumentException.</summary>
    public NodeStatus StatusOf(Node dependency) => throw new NotImplementedException();

    // Waiting.
    /// <summary>Marks the task waiting until the returned scope is disposed.</summary>
    public IDisposable Waiting(string? text = null) => throw new NotImplementedException();
    /// <summary>Checks at once, then every interval on the run's clock until the check returns true. No deadline. A throwing check fails the task.</summary>
    public Task WaitUntil(TimeSpan interval, Func<CancellationToken, Task<bool>> check, string? text = null) => throw new NotImplementedException();

    // Checkpoint: at most 4 KB of UTF-8.
    /// <summary>Empty when there is none.</summary>
    public string Checkpoint => throw new NotImplementedException();
    /// <summary>On disk before the task returns. Over 4 KB throws ArgumentException.</summary>
    public Task SaveCheckpointAsync(string text, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task ClearCheckpointAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
}

/// <summary>Thrown by a task to end it as a reported failure. Make it with <see cref="TaskContext.Fail"/>.</summary>
public sealed class ReportedFailureException : Exception
{
    public IReadOnlyList<string> Detail => throw new NotImplementedException();
}
