using System.Collections.Concurrent;
using System.Diagnostics;

namespace Dagline.Prototype;

/// <summary>
/// The seam between the loop and whatever runs things. The design named three jobs (run the loop, start a body, pump a reporter).
/// The prototype needed two more: signalling the tasks' token, and saying how a completion the loop makes resumes its awaiter.
/// Both run consumer code (cancellation callbacks, the continuation of a body's await), which must not run on the loop in an app,
/// and must run on the test's thread under a manual host.
/// </summary>
internal interface IRunHost
{
    /// <summary>Runs the loop's next turn. Called when an input is posted while the loop waits, from whichever thread posts.</summary>
    void RunLoop(Action turn);

    /// <summary>Starts a task body.</summary>
    void StartBody(Func<Task> body);

    /// <summary>Runs one reporter's delivery until its queue is empty.</summary>
    void PumpReporter(Func<Task> deliver);

    /// <summary>Invented. Signals the tasks' token. The token must read as cancelled when this returns.</summary>
    void SignalTasks(CancellationTokenSource tasks);

    /// <summary>Invented. How a task the loop completes (a save's answer, the run's result) resumes whoever awaits it.</summary>
    TaskCreationOptions CompletionOptions { get; }
}

/// <summary>The host of an app: everything on the thread pool, nothing inline.</summary>
internal sealed class ThreadPoolHost : IRunHost
{
    public static readonly ThreadPoolHost Instance = new();

    public TaskCreationOptions CompletionOptions => TaskCreationOptions.RunContinuationsAsynchronously;

    public void RunLoop(Action turn) => ThreadPool.UnsafeQueueUserWorkItem(static turn => turn(), turn, preferLocal: false);

    public void StartBody(Func<Task> body) => _ = Task.Run(body);

    public void PumpReporter(Func<Task> deliver) => _ = Task.Run(deliver);

    // The token reads as cancelled at once; the callbacks registered on it run on the pool, never on the loop.
    public void SignalTasks(CancellationTokenSource tasks)
        => _ = tasks.CancelAsync().ContinueWith(
            static cancelled => _ = cancelled.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}

internal enum BodyMode
{
    /// <summary>Bodies start with a SynchronizationContext installed whose Post is the host's queue.</summary>
    SynchronizationContext,

    /// <summary>Bodies start as tasks on a TaskScheduler whose queue is the host's queue. No SynchronizationContext.</summary>
    TaskScheduler,

    /// <summary>Bodies run on the thread pool, as in an app. Only the loop and the reporters are the test's to pump.</summary>
    ThreadPool,
}

/// <summary>
/// A host that runs nothing until it is told to: the loop's turns, body starts and their captured continuations, and reporter
/// deliveries each sit in a queue, and a Pump call runs them on the calling thread.
/// </summary>
internal sealed class ManualHost : IRunHost
{
    private readonly ConcurrentQueue<Action> _loopTurns = new();
    private readonly ConcurrentQueue<Action> _bodyWork = new();
    private readonly ConcurrentQueue<Action> _reporterWork = new();
    private readonly SemaphoreSlim _posted = new(0);
    private readonly QueueContext _bodyContext;
    private readonly QueueContext _reporterContext;
    private readonly QueueScheduler _bodyScheduler;
    private readonly BodyMode _mode;

    public ManualHost(BodyMode mode = BodyMode.SynchronizationContext)
    {
        _mode = mode;
        _bodyContext = new QueueContext(this, _bodyWork);
        _reporterContext = new QueueContext(this, _reporterWork);
        _bodyScheduler = new QueueScheduler(this);
    }

    // Inline: under a manual host the loop runs on the test's thread, and so may whoever awaits what it completes.
    public TaskCreationOptions CompletionOptions => TaskCreationOptions.None;

    public bool IsIdle => _loopTurns.IsEmpty && _bodyWork.IsEmpty && _reporterWork.IsEmpty;

    public void RunLoop(Action turn) => Enqueue(_loopTurns, turn);

    public void StartBody(Func<Task> body)
    {
        switch (_mode)
        {
            case BodyMode.SynchronizationContext:
                Enqueue(_bodyWork, () => _ = body());
                break;
            case BodyMode.TaskScheduler:
                _ = Task.Factory.StartNew(body, CancellationToken.None, TaskCreationOptions.None, _bodyScheduler);
                break;
            default:
                _ = Task.Run(body);
                break;
        }
    }

    public void PumpReporter(Func<Task> deliver) => Enqueue(_reporterWork, () => _ = deliver());

    // Inline, so the callbacks registered on the token, and whatever they resume, run on the pumping thread.
    public void SignalTasks(CancellationTokenSource tasks)
    {
        try
        {
            tasks.Cancel();
        }
        catch (AggregateException)
        {
            // A callback a body registered threw. That is the body's affair, and the stop goes on.
        }
    }

    /// <summary>Runs the loop's queued turns. One turn applies every input in the inbox.</summary>
    public int PumpLoop() => Run(_loopTurns, context: null);

    /// <summary>Runs queued body starts and the body continuations that came back to the host.</summary>
    public int PumpBodies() => Run(_bodyWork, _mode is BodyMode.SynchronizationContext ? _bodyContext : null);

    /// <summary>Runs queued reporter deliveries.</summary>
    public int PumpReporters() => Run(_reporterWork, _reporterContext);

    /// <summary>
    /// Runs everything queued, and everything that queues, until all three queues are empty. "Idle" means only that: work that has
    /// left the host (a continuation on the thread pool, I/O in flight, a timer of a clock the host does not own) is invisible.
    /// </summary>
    /// <returns>How many items ran.</returns>
    public int PumpUntilIdle()
    {
        var total = 0;
        while (true)
        {
            var ran = PumpLoop() + PumpBodies() + PumpReporters();
            if (ran == 0)
            {
                return total;
            }

            total += ran;
        }
    }

    /// <summary>
    /// Pumps until <paramref name="condition"/> holds. When the queues are empty and it does not hold yet, blocks the calling thread
    /// until something is queued from another thread, for at most <paramref name="timeout"/> of real time in all.
    /// </summary>
    /// <returns>Whether the condition came to hold.</returns>
    public bool PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            while (_posted.Wait(0))
            {
            }

            PumpUntilIdle();
            if (condition())
            {
                return true;
            }

            var left = timeout - watch.Elapsed;
            if (left <= TimeSpan.Zero || !_posted.Wait(left))
            {
                return condition();
            }
        }
    }

    /// <summary>
    /// The same, without blocking a thread while nothing is queued. After its first real wait the pumping goes on from whichever
    /// thread-pool thread the wait resumed on: "the test's thread" then means "inside this call", one thread at a time.
    /// </summary>
    public async Task<bool> PumpUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            while (_posted.Wait(0))
            {
            }

            PumpUntilIdle();
            if (condition())
            {
                return true;
            }

            var left = timeout - watch.Elapsed;
            if (left <= TimeSpan.Zero || !await _posted.WaitAsync(left).ConfigureAwait(false))
            {
                return condition();
            }
        }
    }

    private void Enqueue(ConcurrentQueue<Action> queue, Action work)
    {
        queue.Enqueue(work);
        _posted.Release();
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

    private sealed class QueueContext(ManualHost host, ConcurrentQueue<Action> queue) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => host.Enqueue(queue, () => d(state));

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        public override SynchronizationContext CreateCopy() => this;
    }

    private sealed class QueueScheduler(ManualHost host) : TaskScheduler
    {
        protected override void QueueTask(Task task) => host.Enqueue(host._bodyWork, () => TryExecuteTask(task));

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        protected override IEnumerable<Task> GetScheduledTasks() => [];
    }
}
