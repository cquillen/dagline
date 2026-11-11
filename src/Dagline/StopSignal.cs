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
/// Once a run has ended it waits for its reporters to handle every event. Any request the run itself did not apply,
/// the first or a later one, ends that wait and drops the reporters still busy, without changing the run outcome.
/// Every request is therefore either applied by the run or ends the wait for its reporters, however close to the end
/// of the run it is made.
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
public class StopSignal
{
    private readonly Lock _gate = new();
    private readonly List<Listener> _listeners = [];
    private int _requests;

    /// <summary><see langword="true"/> once <see cref="Request"/> has been called at least once.</summary>
    public bool IsRequested => Requests > 0;

    /// <summary>How many times <see cref="Request"/> has been called. It never goes down.</summary>
    public int Requests => Volatile.Read(ref _requests);

    /// <summary>Requests a stop, or counts one more request.</summary>
    /// <remarks>
    /// It is safe to call from any thread, including a console's cancel handler, and from several threads at once:
    /// every call is counted. It never blocks and never throws: a run that hears of the request applies it on a thread
    /// of its own, so no work of the run happens on the caller's thread.
    /// </remarks>
    public void Request()
    {
        Listener[] listeners;
        lock (_gate)
        {
            // Saturates instead of wrapping, so the count never goes down and never reads as no request.
            if (_requests < int.MaxValue)
            {
                Volatile.Write(ref _requests, _requests + 1);
            }

            listeners = [.. _listeners];
        }

        foreach (var listener in listeners)
        {
            listener.Call();
        }
    }

    /// <summary>
    /// Calls <paramref name="listener"/> on the thread pool after every request made from now on, and once more at once
    /// when a request has already been made, so that a listener registered late misses nothing. A listener reads
    /// <see cref="Requests"/> to learn what has been asked, and may be called more than once for the same requests.
    /// </summary>
    /// <returns>The registration. Disposing it stops the calls, though a call already queued may still come.</returns>
    internal IDisposable OnRequest(Action listener)
    {
        var registered = new Listener(this, listener);
        bool requested;
        lock (_gate)
        {
            _listeners.Add(registered);
            requested = _requests > 0;
        }

        if (requested)
        {
            registered.Call();
        }

        return registered;
    }

    private void Remove(Listener listener)
    {
        lock (_gate)
        {
            _listeners.Remove(listener);
        }
    }

    private sealed class Listener(StopSignal signal, Action listener) : IDisposable
    {
        // Queued, never called in place, so a listener neither runs on the requester's thread nor throws into it.
        public void Call() => ThreadPool.UnsafeQueueUserWorkItem(static heard => heard(), listener, preferLocal: false);

        public void Dispose() => signal.Remove(this);
    }
}
