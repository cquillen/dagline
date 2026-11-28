using System.Runtime.InteropServices;

namespace Dagline;

/// <summary>
/// The console's cancel key and SIGTERM, hooked to a <see cref="Run"/>, so that Ctrl+C drains the run and a second Ctrl+C or a
/// termination request from the operating system stops it. Hook it once the run has started, and dispose it when the run has ended.
/// </summary>
/// <remarks>
/// <para>
/// Dagline hooks nothing unless the app calls <see cref="Hook"/>. This type is the one place in the core package that touches
/// the console, and it only listens: it writes nothing. It hooks a run that exists: before that, Ctrl+C ends the process as the
/// operating system would, and nothing has started that a drain could let finish.
/// </para>
/// <para>
/// The first Ctrl+C asks the run to drain, as <see cref="Run.Drain"/> does: the run starts nothing more and lets its running tasks
/// finish. The second Ctrl+C asks it to stop, as <see cref="Run.Stop"/> does: its running tasks are signalled and it waits for them
/// for at most its grace period. A SIGTERM stops the run whether or not a drain came first, since a process manager kills the
/// process soon after sending it. Each of these is swallowed, so the process lives on and the run returns its result as usual.
/// Each only posts to the run, so none of the run's work happens on the console's handler thread.
/// </para>
/// <para>
/// Once the hook has stopped the run, a further Ctrl+C or SIGTERM asks nothing more and is not swallowed, so the operating system
/// ends the process as it would with no hook at all. This is the way out of an app that hangs, even after a stop. A process ended
/// this way leaves its journal as a killed process does, and a display gets no chance to restore the terminal.
/// </para>
/// <para>
/// The hook listens to the console's cancel key event, <see cref="Console.CancelKeyPress"/>, and to SIGTERM through
/// <see cref="PosixSignalRegistration"/>, and nothing else: no other key and no other process signal. There is no option to
/// leave SIGTERM unhooked. Of the cancel key event it handles Ctrl+C alone. Ctrl+Break, which raises the same event, is left
/// as it is: it is not counted, not swallowed and asks nothing, so it ends the process as it would with no hook. On
/// Windows, .NET raises SIGTERM for a shutdown or logoff, and Windows ends the process after a short while however the signal
/// is handled. On a platform where .NET cannot hook SIGTERM, the hook listens to Ctrl+C alone.
/// </para>
/// <para>
/// The hook counts its own Ctrl+C presses, so a <see cref="Run.Drain"/> the app makes itself does not make the next Ctrl+C a
/// stop, and a stop the app makes itself, by <see cref="Run.Stop"/> or the run's token, does not make the next Ctrl+C go
/// unswallowed.
/// </para>
/// <para>
/// An app with a trigger of its own, such as a file appearing or a timer, needs no hook: it calls <see cref="Run.Drain"/> or
/// <see cref="Run.Stop"/> itself, or cancels the token it gave the run.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var run = Runner.Start(graph, options);
/// using var console = ConsoleStop.Hook(run);
/// var result = await run.Result;
/// return result.Outcome.ToExitCode();
/// </code>
/// </example>
public sealed class ConsoleStop : IDisposable
{
    private readonly Run _run;
    private readonly Lock _gate = new();

    private int _presses;
    private bool _stopped;
    private bool _disposed;
    private PosixSignalRegistration? _terminate;

    private ConsoleStop(Run run)
    {
        _run = run;
    }

    /// <summary>Hooks the console's cancel key and SIGTERM to <paramref name="run"/>.</summary>
    /// <param name="run">The run that the first Ctrl+C drains and that a second Ctrl+C or a SIGTERM stops.</param>
    /// <returns>The hook, hooked until it is disposed.</returns>
    /// <remarks>
    /// Each call makes a new hook. Two hooked at once would each count every Ctrl+C and SIGTERM, so hook one at a time. A hook
    /// left in place once its run has ended still swallows the presses it would act on, which then change nothing.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="run"/> is <see langword="null"/>.</exception>
    public static ConsoleStop Hook(Run run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var stop = new ConsoleStop(run);
#pragma warning disable RS0030 // The one exception to the core's console ban: listening for the cancel key writes nothing.
        Console.CancelKeyPress += stop.OnCancelKeyPress;
#pragma warning restore RS0030
        stop._terminate = TryHookTerminate(stop.OnTerminate);
        return stop;
    }

    /// <summary>
    /// Unhooks the console's cancel key and SIGTERM. Either one after that is the app's own business again, and with nothing else
    /// hooked it ends the process. The run is not changed. Disposing a second time does nothing.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

#pragma warning disable RS0030 // Unhooks what Hook hooked.
        Console.CancelKeyPress -= OnCancelKeyPress;
#pragma warning restore RS0030
        _terminate?.Dispose();
    }

    private static PosixSignalRegistration? TryHookTerminate(Action<PosixSignalContext> handler)
    {
        try
        {
            return PosixSignalRegistration.Create(PosixSignal.SIGTERM, handler);
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// A console stop of <paramref name="run"/> hooked to nothing, for the shared test support alone, which applies its presses itself.
    /// </summary>
    internal static ConsoleStop Unhooked(Run run) => new(run);

    /// <summary>Applies a Ctrl+C: the first drains the run, and the second stops it.</summary>
    /// <returns>Whether it is swallowed: whether it drained or stopped, rather than finding the hook no longer listening.</returns>
    internal bool CtrlCPressed()
    {
        lock (_gate)
        {
            if (!IsListening)
            {
                return false;
            }

            if (++_presses == 1)
            {
                _run.Drain();
            }
            else
            {
                StopTheRun();
            }

            return true;
        }
    }

    /// <summary>Applies a SIGTERM: stops the run, whether or not a drain came first.</summary>
    /// <returns>Whether it is swallowed: whether it stopped, rather than finding the hook no longer listening.</returns>
    internal bool SigtermReceived()
    {
        lock (_gate)
        {
            if (!IsListening)
            {
                return false;
            }

            StopTheRun();
            return true;
        }
    }

    // Read under the gate, so that a press racing Dispose is either applied or left to the operating system, never both.
    private bool IsListening => !_disposed && !_stopped;

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs pressed)
    {
        if (pressed.SpecialKey == ConsoleSpecialKey.ControlC && CtrlCPressed())
        {
            pressed.Cancel = true;
        }
    }

    private void OnTerminate(PosixSignalContext received)
    {
        if (SigtermReceived())
        {
            received.Cancel = true;
        }
    }

    private void StopTheRun()
    {
        _stopped = true;
        _run.Stop();
    }
}
