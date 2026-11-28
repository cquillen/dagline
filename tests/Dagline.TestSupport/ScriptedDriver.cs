namespace Dagline.TestSupport;

/// <summary>
/// A run that a test pumps on its own thread: its loop's turns, its task bodies and its reporters' deliveries
/// each wait until the test pumps them, so a test makes each thing happen in the order it means, and runs once.
/// <c>var result = await ScriptedDriver.Start(graph, options).Finish();</c>
/// </summary>
/// <remarks>
/// Pumping until nothing is queued means only that the host's queues are empty. A body is exact only when everything it awaits is
/// completed by the test or by the run, or resumes on the context it started on: an await that leaves for the thread pool leaves
/// the pump. It stays here until the testing package has a scripted run of its own (#155).
/// </remarks>
public sealed class ScriptedDriver
{
    private readonly ManualHost _host;
    private readonly Run _run;

    private ScriptedDriver(ManualHost host, Run run)
    {
        _host = host;
        _run = run;
    }

    /// <summary>The run, as an app holds it.</summary>
    public Run Run => _run;

    /// <summary>The run's current snapshot, as a reporter's source of snapshots gives it.</summary>
    public Snapshot Current => _run.Current;

    /// <summary>
    /// The run's result: completed once the run has ended and its reporters are done, or faulted with what faulted the run's loop.
    /// A test awaits it once it has pumped the run to its end, and the await then waits for nothing. Unlike <see cref="Run.Result"/>,
    /// it gives an unrecorded run's result rather than throwing, until #153 makes unrecorded a run outcome.
    /// </summary>
    public Task<RunResult> Result => _run.ResultEvenIfUnrecorded;

    /// <summary>Whether the run's inbox is closed: the run's loop has gone, so nothing posted to it is read.</summary>
    public bool InboxIsClosed => _run.Inbox.IsClosed;

    /// <summary>
    /// Starts a run of <paramref name="graph"/>. Nothing of the run has happened when this returns: its first turn
    /// waits for the test to pump it.
    /// </summary>
    /// <param name="graph">The graph to run.</param>
    /// <param name="options">The options of the run.</param>
    /// <param name="cancellationToken">The run's token: cancelling it is a stop, as <see cref="Stop"/> is.</param>
    /// <exception cref="ArgumentNullException"><paramref name="graph"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    public static ScriptedDriver Start(Graph graph, RunOptions options, CancellationToken cancellationToken = default)
    {
        var host = new ManualHost();
        return new ScriptedDriver(host, Run.Start(graph, options, host, cancellationToken));
    }

    /// <summary>Asks the run to drain. It is applied when the test next pumps the loop.</summary>
    public void Drain() => _run.Drain();

    /// <summary>Asks the run to stop, as cancelling its token does. It is applied when the test next pumps the loop.</summary>
    public void Stop() => _run.Stop();

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
        return Result.IsCompleted
            ? Result
            : throw new InvalidOperationException("Nothing is queued and the run has not ended: something it waits for is outside the pump.");
    }
}
