namespace Dagline;

internal static class CancellationSources
{
    /// <summary>
    /// Cancels <paramref name="source"/> without waiting for its callbacks: the token reads as cancelled at once, while the
    /// callbacks registered on it run later on the thread pool. A fault from a callback that threw is observed and let go.
    /// </summary>
    public static void CancelLettingCallbackFaultsGo(this CancellationTokenSource source)
        => _ = source.CancelAsync().ContinueWith(
            static cancelled => _ = cancelled.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>
    /// Calls <paramref name="listener"/> on the thread pool once <paramref name="token"/> is cancelled, or soon when it already is.
    /// The registration's own callback only queues the call, so the listener never runs on the thread that cancels the token,
    /// nor inline in this method, and never throws into either.
    /// </summary>
    /// <returns>
    /// The registration. Release it with <see cref="CancellationTokenRegistration.Unregister"/>, which never waits for a callback
    /// that is running; a call already queued may still come.
    /// </returns>
    public static CancellationTokenRegistration OnCancelled(this CancellationToken token, Action listener)
        => token.UnsafeRegister(
            static heard => ThreadPool.UnsafeQueueUserWorkItem(static call => call(), (Action)heard!, preferLocal: false),
            listener);
}
