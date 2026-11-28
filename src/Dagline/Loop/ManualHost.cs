using System.Collections.Concurrent;

namespace Dagline;

/// <summary>
/// A host that runs nothing until it is told to: the loop's turns, the task bodies with whatever their awaits resume, and the
/// reporters' deliveries each wait in a queue of their own, and each pump runs one queue, or all of them, on the calling thread.
/// A test pumps it to make a whole run a script on the test's own thread.
/// </summary>
/// <remarks>
/// <para>
/// A body and a delivery run under a synchronization context whose posts go back to their own queue, so an await in a body that
/// resumes on its context, such as <see cref="Task.Yield"/>, or on a task the test completes, stays on the pump.
/// An await that resumes elsewhere, such as one with <c>ConfigureAwait(false)</c> on work that completes on another thread, leaves
/// the pump: only pumping until a condition holds, in real time, can follow it, and that is for the test's side to do.
/// </para>
/// <para>
/// It signals a token inline, so the callbacks registered on it, and whatever they resume, run on the pumping thread; and the
/// tasks the loop completes resume their awaiters inline, on the same thread.
/// </para>
/// <para>Queuing is safe from any thread. Pumping is for one thread at a time.</para>
/// </remarks>
internal sealed class ManualHost : IRunHost
{
    private readonly ConcurrentQueue<Action> _loopTurns = new();
    private readonly ConcurrentQueue<Action> _bodyWork = new();
    private readonly ConcurrentQueue<Action> _reporterWork = new();
    private readonly QueueContext _bodyContext;
    private readonly QueueContext _reporterContext;

    public ManualHost()
    {
        _bodyContext = new QueueContext(_bodyWork);
        _reporterContext = new QueueContext(_reporterWork);
    }

    public TaskCreationOptions CompletionOptions => TaskCreationOptions.None;

    /// <summary>Whether nothing is queued: no turn of the loop, no body work and no delivery.</summary>
    public bool IsIdle => _loopTurns.IsEmpty && _bodyWork.IsEmpty && _reporterWork.IsEmpty;

    public void RunLoop(Action turn) => _loopTurns.Enqueue(turn);

    public void StartBody(Func<Task> body) => _bodyWork.Enqueue(() => _ = body());

    public void DeliverToReporter(Func<Task> delivery) => _reporterWork.Enqueue(() => _ = delivery());

    public void Signal(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (AggregateException)
        {
            // A callback a body registered on its token threw. That is the body's affair, and the run goes on.
        }
    }

    /// <summary>Runs the loop's queued turns, and any queued while they run. One turn applies every input in the inbox.</summary>
    /// <returns>How many turns ran.</returns>
    public int PumpLoop() => Run(_loopTurns, context: null);

    /// <summary>Runs the queued body starts and the continuations of bodies that came back to the host, and any queued meanwhile.</summary>
    /// <returns>How many items ran.</returns>
    public int PumpBodies() => Run(_bodyWork, _bodyContext);

    /// <summary>Runs the queued reporter deliveries, and any queued meanwhile.</summary>
    /// <returns>How many items ran.</returns>
    public int PumpReporters() => Run(_reporterWork, _reporterContext);

    /// <summary>
    /// Runs everything queued, and everything that queues while it runs, until all three queues are empty. Empty means only that:
    /// work that has left the host, such as a continuation on the thread pool or a timer of a clock the host does not own, is not seen.
    /// </summary>
    /// <returns>How many items ran.</returns>
    public int Pump()
    {
        var total = 0;
        while (PumpLoop() + PumpBodies() + PumpReporters() is var ran and > 0)
        {
            total += ran;
        }

        return total;
    }

    private static int Run(ConcurrentQueue<Action> queue, SynchronizationContext? context)
    {
        var ran = 0;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            while (queue.TryDequeue(out var work))
            {
                work();
                ran++;
            }
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        return ran;
    }

    /// <summary>A context whose posts go to one of the host's queues, so that what resumes on it waits for that queue's pump.</summary>
    private sealed class QueueContext(ConcurrentQueue<Action> queue) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => queue.Enqueue(() => d(state));

        // A send waits for the callback, and nothing runs until the test pumps: a send from the pumping thread runs it in place.
        public override void Send(SendOrPostCallback d, object? state) => d(state);

        public override SynchronizationContext CreateCopy() => this;
    }
}
