// PROTOTYPE (throwaway, stubs only). Starting a run, stopping it, and what comes back.
namespace Dagline;

public enum RunOutcome { Complete, Incomplete, Stopped }

public static class RunOutcomeExtensions
{
    /// <summary>The default mapping: Complete 0, Incomplete 1, Stopped 2. The app owns the exit code.</summary>
    public static int ToExitCode(this RunOutcome outcome) => throw new NotImplementedException();
}

/// <summary>The app's handle for stopping a run. First Request is a stop; a second ends the grace period.</summary>
public class StopSignal
{
    public bool IsRequested => throw new NotImplementedException();
    public int Requests => throw new NotImplementedException();
    public void Request() => throw new NotImplementedException();
}

public sealed record RunOptions
{
    /// <summary>Most tasks running at once. Default 16.</summary>
    public int Cap { get; init; } = 16;
    /// <summary>Wait after a stop. Default 30 s; TimeSpan.Zero and Timeout.InfiniteTimeSpan allowed.</summary>
    public TimeSpan GracePeriod { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Sizes of the named limits. A limit named in the graph but missing here is a RunConfigurationException.</summary>
    public IReadOnlyDictionary<string, int> Limits { get; init; } = new Dictionary<string, int>();
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    /// <summary>Required: the app always chooses.</summary>
    public required Journal Journal { get; init; }
    public IReadOnlyList<IReporter> Reporters { get; init; } = [];
}

public sealed record RunResult
{
    public RunOutcome Outcome => throw new NotImplementedException();
    public Snapshot Snapshot => throw new NotImplementedException();
    public IReadOnlyList<ReporterError> ReporterErrors => throw new NotImplementedException();
}

public sealed record ReporterError(IReporter Reporter, RunEvent? Event, Exception Exception);

/// <summary>Entry point. Throws a DaglineException before any run exists, and JournalWriteException when a journal write fails mid-run.</summary>
public static class Runner
{
    public static Task<RunResult> RunAsync(Graph graph, RunOptions options, StopSignal? stop = null) => throw new NotImplementedException();
}

public class DaglineException : Exception { }

/// <summary>Build found problems. Lists every one.</summary>
public sealed class GraphException : DaglineException
{
    public IReadOnlyList<GraphProblem> Problems => throw new NotImplementedException();
}

public sealed record GraphProblem(NodePath? Path, string Message);

/// <summary>A named limit without a size, a cap below one, and similar.</summary>
public sealed class RunConfigurationException : DaglineException { }
