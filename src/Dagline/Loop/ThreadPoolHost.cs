namespace Dagline;

/// <summary>
/// The host of a run in an app: it does each thing the loop asks on the thread pool, so no callback of a task or a reporter runs
/// on the loop, and the loop never runs on the thread of a task or a reporter that posts to it.
/// </summary>
internal sealed class ThreadPoolHost : IRunHost
{
    public static ThreadPoolHost Instance { get; } = new();

    private ThreadPoolHost()
    {
    }

    public TaskCreationOptions CompletionOptions => TaskCreationOptions.RunContinuationsAsynchronously;

    public void RunLoop(Action turn) => OnThePool.Call(turn);

    public void StartBody(Func<Task> body) => _ = Task.Run(body);

    public void DeliverToReporter(Func<Task> delivery) => _ = Task.Run(delivery);

    public void Signal(CancellationTokenSource source) => source.CancelLettingCallbackFaultsGo();
}
