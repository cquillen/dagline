using System.Diagnostics.CodeAnalysis;

namespace Dagline;

/// <summary>
/// Hands a run's events to its reporters. Each reporter has a queue of its own, so every reporter receives
/// the same events in the same order, and one that is slow or fails holds up neither the run nor another reporter.
/// A stop request made once the run has ended cuts short the wait for reporters still busy.
/// </summary>
[SuppressMessage(
    "Reliability",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The source has no timer and no wait handle is ever taken from it, and a reporter call the run has dropped may still hold its token.")]
internal sealed class ReporterDelivery
{
    private readonly ReporterQueue[] _queues;
    private readonly StopSignal? _signal;

    // Cancelled only by a stop request made once the run has ended: that is what ends the wait for reporters still busy.
    private readonly CancellationTokenSource _stopWaiting = new();

    public ReporterDelivery(IReadOnlyList<IReporter> reporters, StopSignal? signal)
    {
        _signal = signal;
        _queues = [.. reporters.Select(reporter => new ReporterQueue(reporter, _stopWaiting.Token))];
        Delivered = CollectErrorsAsync();
    }

    /// <summary>
    /// Completes once <see cref="Complete"/> has been called and every reporter that was not dropped has handled every event,
    /// with the error of each reporter that was dropped, in the order the reporters were given. A stop request made once
    /// <see cref="RunEnded"/> is published drops every reporter still busy, so it completes without waiting for their calls. It never faults.
    /// </summary>
    public Task<IReadOnlyList<ReporterError>> Delivered { get; }

    /// <summary>
    /// Queues an event for every reporter and returns at once: the caller never waits for a reporter.
    /// The run's last event, <see cref="RunEnded"/>, begins the wait for reporters, so from just before it is queued
    /// a stop request ends that wait. Requests made earlier do not.
    /// </summary>
    public void Publish(RunEvent runEvent, Snapshot snapshot)
    {
        if (runEvent is RunEnded)
        {
            HearTheNextRequest();
        }

        foreach (var queue in _queues)
        {
            queue.Publish(runEvent, snapshot);
        }
    }

    /// <summary>Says that no event will follow.</summary>
    public void Complete()
    {
        foreach (var queue in _queues)
        {
            queue.Complete();
        }
    }

    private void HearTheNextRequest()
    {
        if (_signal is null)
        {
            return;
        }

        var registration = _signal.NextRequest.UnsafeRegister(static delivery => ((ReporterDelivery)delivery!).StopWaiting(), this);

        // The signal outlives the run, so the registration goes once every reporter is done, lest the signal keep them.
        _ = Delivered.ContinueWith(
            static (_, heard) => ((CancellationTokenRegistration)heard!).Unregister(),
            registration,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void StopWaiting()
    {
        try
        {
            _stopWaiting.Cancel();
        }
        catch (AggregateException)
        {
            // A callback a reporter registered on its token threw. That is the reporter's affair, and it is dropped regardless.
        }
    }

    private async Task<IReadOnlyList<ReporterError>> CollectErrorsAsync()
    {
        var errors = await Task.WhenAll(_queues.Select(queue => queue.Delivered)).ConfigureAwait(false);
        return [.. errors.OfType<ReporterError>()];
    }
}
