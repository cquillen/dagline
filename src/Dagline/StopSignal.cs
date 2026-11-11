using System.Diagnostics.CodeAnalysis;

namespace Dagline;

/// <summary>
/// A request to stop a run, which the app creates, passes to <see cref="Runner.RunAsync"/>, and requests from wherever it decides:
/// a console's cancel event, a file appearing, a timer.
/// </summary>
/// <remarks>
/// <para>
/// Dagline itself never hooks Ctrl+C or a process signal. The app decides what asks for a stop and calls <see cref="Request"/>.
/// </para>
/// <para>
/// The first request is a stop: from then on the run starts nothing, signals <see cref="TaskContext.CancellationToken"/>
/// on every running task, and waits for the running tasks to end. Later requests are counted in <see cref="Requests"/>.
/// </para>
/// <para>
/// Once a run has ended it waits for its reporters to handle every event. Any request made during that wait,
/// the first or a later one, ends it and drops the reporters still busy, without changing the run outcome.
/// </para>
/// <para>
/// A signal is not tied to one run. A run reads the requests made up to its end and ignores later ones,
/// and a signal already requested when a run starts stops that run from its start: the run starts no task at all.
/// The same signal can therefore be passed to a second run after the first has ended, and that run stops from its start.
/// </para>
/// <para>
/// The class is not sealed so that a UI package can offer a signal hooked to the console. Its members are not virtual.
/// </para>
/// </remarks>
[SuppressMessage(
    "Reliability",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The sources have no timer and no wait handle is ever taken from them, so they hold nothing to free, and a signal an app forgets to dispose must still work.")]
public class StopSignal
{
    // Cancelled on the first request. It is never disposed, so cancelling it cannot throw.
    private readonly CancellationTokenSource _firstRequest = new();

    // Swapped for a fresh source on every request, then cancelled, so it answers whichever request comes next.
    private CancellationTokenSource _nextRequest = new();
    private int _requests;

    /// <summary><see langword="true"/> once <see cref="Request"/> has been called at least once.</summary>
    public bool IsRequested => Requests > 0;

    /// <summary>How many times <see cref="Request"/> has been called. It never goes down.</summary>
    public int Requests => Volatile.Read(ref _requests);

    /// <summary>A token cancelled on the first request, through which a run hears of it.</summary>
    internal CancellationToken FirstRequest => _firstRequest.Token;

    /// <summary>
    /// A token cancelled by the first request made after it was read, whichever request that is.
    /// Requests made before it was read do not cancel it.
    /// </summary>
    internal CancellationToken NextRequest => Volatile.Read(ref _nextRequest).Token;

    /// <summary>Requests a stop, or counts one more request.</summary>
    /// <remarks>
    /// It is safe to call from any thread, including a console's cancel handler, and from several threads at once:
    /// every call is counted. It never blocks and never throws. A run that hears of the request applies it on a thread
    /// of its own, so no work of the run happens on the caller's thread.
    /// </remarks>
    public void Request()
    {
        if (CountOneMore() == 1)
        {
            // Cancelled asynchronously, so a run's work in hearing of the stop never runs on the caller's thread.
            _ = _firstRequest.CancelAsync();
        }

        _ = Interlocked.Exchange(ref _nextRequest, new CancellationTokenSource()).CancelAsync();
    }

    // Saturates instead of wrapping, so the count never goes down and never reads as no request.
    private int CountOneMore()
    {
        var seen = Volatile.Read(ref _requests);
        while (seen < int.MaxValue)
        {
            var before = Interlocked.CompareExchange(ref _requests, seen + 1, seen);
            if (before == seen)
            {
                return seen + 1;
            }

            seen = before;
        }

        return seen;
    }
}
