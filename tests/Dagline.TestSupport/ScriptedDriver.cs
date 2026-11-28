using Dagline.Testing;

namespace Dagline.TestSupport;

/// <summary>
/// A run that a test pumps on its own thread: its loop's turns, its task bodies and its reporters' deliveries
/// each wait until the test pumps them, so a test makes each thing happen in the order it means, and runs once.
/// <c>var result = await ScriptedDriver.Start(graph, options).Finish();</c>
/// </summary>
/// <remarks>
/// It is the testing package's <see cref="ScriptedRun"/>, with what only Dagline's own tests may see of the run beside it, and
/// keeps the shape the scripted tests were written against: <see cref="Finish"/> pumps until nothing is queued and gives the
/// result as a task. The limits of a scripted run are <see cref="ScriptedRun"/>'s. A run whose options keep the default clock is
/// on the scripted run's own clock, which only <see cref="Scripted"/> moves.
/// </remarks>
public sealed class ScriptedDriver
{
    private readonly ScriptedRun _scripted;
    private readonly Run _run;

    private ScriptedDriver(ScriptedRun scripted)
    {
        _scripted = scripted;
        _run = scripted.Run;
    }

    /// <summary>The scripted run underneath, for its clock and its real-time pumps.</summary>
    public ScriptedRun Scripted => _scripted;

    /// <summary>The run, as an app holds it.</summary>
    public Run Run => _run;

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
    /// Starts a run of <paramref name="graph"/>. Nothing of the run has happened when this returns: its first turn
    /// waits for the test to pump it.
    /// </summary>
    /// <param name="graph">The graph to run.</param>
    /// <param name="options">The options of the run.</param>
    /// <param name="cancellationToken">The run's token: cancelling it is a stop, as <see cref="Stop"/> is.</param>
    /// <exception cref="ArgumentNullException"><paramref name="graph"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    public static ScriptedDriver Start(Graph graph, RunOptions options, CancellationToken cancellationToken = default)
    {
        return new ScriptedDriver(ScriptedRun.Start(graph, options, cancellationToken));
    }

    /// <summary>Asks the run to drain. It is applied when the test next pumps the loop.</summary>
    public void Drain() => _run.Drain();

    /// <summary>Asks the run to stop, as cancelling its token does. It is applied when the test next pumps the loop.</summary>
    public void Stop() => _run.Stop();

    /// <summary>Runs everything queued, and everything that queues meanwhile, until nothing is queued.</summary>
    /// <returns>How many items ran.</returns>
    public int Pump() => _scripted.Pump();

    /// <summary>Runs the loop's queued turns only.</summary>
    /// <returns>How many turns ran.</returns>
    public int PumpLoop() => _scripted.PumpLoop();

    /// <summary>Runs the queued body starts and body continuations only.</summary>
    /// <returns>How many items ran.</returns>
    public int PumpBodies() => _scripted.PumpBodies();

    /// <summary>Runs the queued reporter deliveries only.</summary>
    /// <returns>How many items ran.</returns>
    public int PumpReporters() => _scripted.PumpReporters();

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
