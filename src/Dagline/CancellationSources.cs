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
}
