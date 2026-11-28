namespace Dagline.TestSupport;

/// <summary>
/// A run on the new engine that a test pumps on its own thread: its loop's turns, its task bodies and its reporters' deliveries
/// each wait until the test pumps them, so a test makes each thing happen in the order it means, and runs once.
/// <c>var result = await ScriptedDriver.Start(graph, options).Finish();</c>
/// </summary>
/// <remarks>
/// Pumping until nothing is queued means only that the host's queues are empty. A body is exact only when everything it awaits is
/// completed by the test or by the run, or resumes on the context it started on: an await that leaves for the thread pool leaves
/// the pump. Until the new engine replaces the existing one, it is reachable only from here and from <see cref="NewEngine"/>.
/// </remarks>
public sealed class ScriptedDriver
{
    private readonly ManualHost _host;
    private readonly LoopRun _run;

    private ScriptedDriver(ManualHost host, LoopRun run)
    {
        _host = host;
        _run = run;
    }

    /// <summary>The run's current snapshot, as a reporter's source of snapshots gives it.</summary>
    public Snapshot Current => _run.Current;

    /// <summary>
    /// The run's result: completed once the run has ended and its reporters are done, or faulted with what faulted the run's loop.
    /// A test awaits it once it has pumped the run to its end, and the await then waits for nothing.
    /// </summary>
    public Task<RunResult> Result => _run.Result;

    /// <summary>Whether the run's inbox is closed: the run's loop has gone, so nothing posted to it is read.</summary>
    public bool InboxIsClosed => _run.Inbox.IsClosed;

    /// <summary>
    /// Starts a run of <paramref name="graph"/> on the new engine. Nothing of the run has happened when this returns: its first turn
    /// waits for the test to pump it.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="graph"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    public static ScriptedDriver Start(Graph graph, RunOptions options)
    {
        var host = new ManualHost();
        return new ScriptedDriver(host, LoopRun.Start(graph, options, host));
    }

    /// <summary>Runs everything queued, and everything that queues meanwhile, until nothing is queued.</summary>
    /// <returns>How many items ran.</returns>
    public int Pump() => _host.Pump();

    /// <summary>Runs the loop's queued turns only.</summary>
    /// <returns>How many turns ran.</returns>
    public int PumpLoop() => _host.PumpLoop();

    /// <summary>Runs the queued body starts and body continuations only.</summary>
    /// <returns>How many items ran.</returns>
    public int PumpBodies() => _host.PumpBodies();

    /// <summary>Runs the queued reporter deliveries only.</summary>
    /// <returns>How many items ran.</returns>
    public int PumpReporters() => _host.PumpReporters();

    /// <summary>Pumps until nothing is queued, and gives the result, which must then be complete.</summary>
    /// <exception cref="InvalidOperationException">
    /// Nothing is queued and the run has not ended: something the run waits for is outside the pump.
    /// </exception>
    public Task<RunResult> Finish()
    {
        Pump();
        return _run.Result.IsCompleted
            ? _run.Result
            : throw new InvalidOperationException("Nothing is queued and the run has not ended: something it waits for is outside the pump.");
    }
}
