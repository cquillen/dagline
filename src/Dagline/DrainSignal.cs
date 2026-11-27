namespace Dagline;

/// <summary>
/// A request to drain a run: to start nothing new and let the running tasks finish as they would. The app creates one, passes it
/// to <see cref="Runner.RunAsync"/>, and requests it from wherever it decides: a console's cancel key, a file appearing, a timer.
/// </summary>
/// <remarks>
/// <para>
/// Once a run has applied the drain, which publishes <see cref="DrainRequested"/>, it starts nothing, <em>after</em> tasks included.
/// It does not signal <see cref="TaskContext.CancellationToken"/> and sets no time limit: each running task finishes however it
/// would, and the run ends once none is running. Tasks are not told of the drain. To have the running tasks end now, the app
/// cancels the token it passed to the run, which is a stop, and which may follow a drain.
/// </para>
/// <para>
/// Requesting a signal that is already requested does nothing, and so does requesting it once the run has applied a stop. A drain
/// never drops a reporter at the end of the run.
/// </para>
/// <para>
/// A signal is not tied to one run. A run reads the request until its end and ignores it after, and a signal already requested
/// when a run starts drains that run from its start: the run starts no task at all. The same signal can therefore be passed to
/// a second run after the first has ended, and that run is drained from its start.
/// </para>
/// <para>
/// Dagline hooks no Ctrl+C and no process signal unless the app calls <see cref="ConsoleStop.Hook"/>, whose
/// <see cref="ConsoleStop.Drain"/> is a signal of this kind requested by the first Ctrl+C. A signal made with
/// <see langword="new"/> holds no resource and is not disposable.
/// </para>
/// </remarks>
public sealed class DrainSignal
{
    private readonly Lock _gate = new();
    private readonly List<Listener> _listeners = [];
    private volatile bool _requested;

    /// <summary><see langword="true"/> once <see cref="Request"/> has been called. It never goes back.</summary>
    public bool IsRequested => _requested;

    /// <summary>Requests the drain. Once it has been requested, this does nothing.</summary>
    /// <remarks>
    /// It is safe to call from any thread, including a console's cancel handler, and from several threads at once.
    /// It never blocks and never throws: a run that hears of the request applies it on a thread of its own, so no work of the
    /// run happens on the caller's thread.
    /// </remarks>
    public void Request()
    {
        Listener[] listeners;
        lock (_gate)
        {
            if (_requested)
            {
                return;
            }

            _requested = true;
            listeners = [.. _listeners];
            _listeners.Clear();
        }

        foreach (var listener in listeners)
        {
            listener.Call();
        }
    }

    /// <summary>
    /// Calls <paramref name="listener"/> on the thread pool once the signal is requested, or at once when it already has been,
    /// so that a listener registered late misses nothing.
    /// </summary>
    /// <returns>The registration. Disposing it stops the call, though a call already queued may still come.</returns>
    internal IDisposable OnRequest(Action listener)
    {
        var registered = new Listener(this, listener);
        bool requested;
        lock (_gate)
        {
            requested = _requested;
            if (!requested)
            {
                _listeners.Add(registered);
            }
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

    private sealed class Listener(DrainSignal signal, Action listener) : IDisposable
    {
        public void Call() => OnThePool.Call(listener);

        public void Dispose() => signal.Remove(this);
    }
}
