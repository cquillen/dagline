namespace Dagline;

internal static class OnThePool
{
    /// <summary>
    /// Queues a call of <paramref name="action"/> on the thread pool's global queue, never in place, so that it neither runs on the
    /// caller's thread nor throws into it. Nothing flows to it from the caller: no execution context, so no async-local state.
    /// </summary>
    public static void Call(Action action) => ThreadPool.UnsafeQueueUserWorkItem(static call => call(), action, preferLocal: false);
}
