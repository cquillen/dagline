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
/// on every running task, and waits for the running tasks to end, for at most <see cref="RunOptions.GracePeriod"/>.
/// The second request ends that wait at once. Later requests are counted in <see cref="Requests"/> and change nothing.
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
    // Cancelled on the first and the second request. They are never disposed, so cancelling them cannot throw.
    private readonly CancellationTokenSource _firstRequest = new();
    private readonly CancellationTokenSource _secondRequest = new();
    private int _requests;

    /// <summary><see langword="true"/> once <see cref="Request"/> has been called at least once.</summary>
    public bool IsRequested => Requests > 0;

    /// <summary>How many times <see cref="Request"/> has been called. It never goes down.</summary>
    public int Requests => Volatile.Read(ref _requests);

    /// <summary>A token cancelled on the first request, through which a run hears of it.</summary>
    internal CancellationToken FirstRequest => _firstRequest.Token;

    /// <summary>A token cancelled on the second request, through which a run hears that it should stop waiting.</summary>
    internal CancellationToken SecondRequest => _secondRequest.Token;

    /// <summary>Requests a stop, or counts one more request.</summary>
    /// <remarks>
    /// It is safe to call from any thread, including a console's cancel handler, and from several threads at once:
    /// every call is counted. It never blocks and never throws. A run that hears of the request applies it on a thread
    /// of its own, so no work of the run happens on the caller's thread.
    /// </remarks>
    public void Request()
    {
        // Cancelled asynchronously, so a run's work in hearing of a request never runs on the caller's thread.
        _ = CountOneMore() switch
        {
            1 => _firstRequest.CancelAsync(),
            2 => _secondRequest.CancelAsync(),
            _ => Task.CompletedTask,
        };
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
