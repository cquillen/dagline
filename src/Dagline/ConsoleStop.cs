using System.Runtime.InteropServices;

namespace Dagline;

/// <summary>
/// The console's cancel key and SIGTERM, hooked to a <see cref="DrainSignal"/> and a <see cref="CancellationToken"/>, so that
/// Ctrl+C drains a run and a second Ctrl+C or a termination request from the operating system stops it. Pass <see cref="Drain"/>
/// and <see cref="Token"/> to <see cref="Runner.RunAsync"/> and dispose this when the run has ended.
/// </summary>
/// <remarks>
/// <para>
/// Dagline hooks nothing unless the app calls <see cref="Hook"/>. This type is the one place in the core package that touches
/// the console, and it only listens: it writes nothing.
/// </para>
/// <para>
/// The first Ctrl+C requests <see cref="Drain"/>: the run starts nothing more and lets its running tasks finish. The second
/// Ctrl+C cancels <see cref="Token"/>, which stops the run: its running tasks are signalled and it waits for them for at most
/// its grace period. A SIGTERM cancels <see cref="Token"/> whether or not a drain came first, since a process manager kills the
/// process soon after sending it. Each of these is swallowed, so the process lives on and the run winds down and returns its
/// result as usual.
/// </para>
/// <para>
/// Once the token is cancelled, a further Ctrl+C or SIGTERM requests nothing more and is not swallowed, so the operating system
/// ends the process as it would with no hook at all. This is the way out of an app that hangs, even after a stop. A process ended
/// this way leaves its journal as a killed process does, and a display gets no chance to restore the terminal.
/// </para>
/// <para>
/// The hook listens to the console's cancel key event, <see cref="Console.CancelKeyPress"/>, and to SIGTERM through
/// <see cref="PosixSignalRegistration"/>, and nothing else: no other key and no other process signal. There is no option to
/// leave SIGTERM unhooked. Of the cancel key event it handles Ctrl+C alone. Ctrl+Break, which raises the same event, is left
/// as it is: it is not counted, not swallowed and requests nothing, so it ends the process as it would with no hook. On
/// Windows, .NET raises SIGTERM for a shutdown or logoff, and Windows ends the process after a short while however the signal
/// is handled. On a platform where .NET cannot hook SIGTERM, the hook listens to Ctrl+C alone.
/// </para>
/// <para>
/// The hook counts its own Ctrl+C presses, so a <see cref="DrainSignal.Request"/> the app makes on <see cref="Drain"/> does not
/// make the next Ctrl+C a stop.
/// </para>
/// <para>
/// An app with a trigger of its own, such as a file appearing or a timer, needs no hook: it creates a plain
/// <see cref="DrainSignal"/> or a <see cref="CancellationTokenSource"/> and requests or cancels it itself.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// using var console = ConsoleStop.Hook();
/// var result = await Runner.RunAsync(graph, options, console.Drain, console.Token);
/// return result.Outcome.ToExitCode();
/// </code>
/// </example>
public sealed class ConsoleStop : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();

    private int _presses;
    private bool _disposed;
    private PosixSignalRegistration? _terminate;

    private ConsoleStop()
    {
        Token = _stop.Token;
    }

    /// <summary>The drain signal that the first Ctrl+C requests. Pass it to <see cref="Runner.RunAsync"/>.</summary>
    public DrainSignal Drain { get; } = new();

    /// <summary>
    /// The token that the second Ctrl+C, or a SIGTERM at any time, cancels. Pass it to <see cref="Runner.RunAsync"/>. It can still
    /// be read once this has been disposed, and is then never cancelled by the hook.
    /// </summary>
    public CancellationToken Token { get; }

    /// <summary>Hooks a new drain signal and a new token to the console's cancel key and to SIGTERM.</summary>
    /// <returns>A drain that is not requested and a token that is not cancelled, hooked until this is disposed.</returns>
    /// <remarks>
    /// Each call makes a new hook. Two hooked at once would each count every Ctrl+C and SIGTERM, so hook one at a time.
    /// </remarks>
    public static ConsoleStop Hook()
    {
        var stop = new ConsoleStop();
#pragma warning disable RS0030 // The one exception to the core's console ban: listening for the cancel key writes nothing.
        Console.CancelKeyPress += stop.OnCancelKeyPress;
#pragma warning restore RS0030
        stop._terminate = TryHookTerminate(stop.OnTerminate);
        return stop;
    }

    /// <summary>
    /// Unhooks the console's cancel key and SIGTERM, and disposes the source of <see cref="Token"/>. Either one after that is the
    /// app's own business again, and with nothing else hooked it ends the process.
    /// </summary>
    /// <remarks>
    /// <see cref="Drain"/> keeps its request, if it had one, and can still be requested from code. <see cref="Token"/> keeps its
    /// state. Disposing a second time does nothing.
    /// </remarks>
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
        _stop.Dispose();
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

    /// <summary>A console stop hooked to nothing, for the shared test support alone, which applies its presses itself.</summary>
    internal static ConsoleStop Unhooked() => new();

    /// <summary>Applies a Ctrl+C: the first drains, and the second stops.</summary>
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
                Drain.Request();
            }
            else
            {
                CancelTheToken();
            }

            return true;
        }
    }

    /// <summary>Applies a SIGTERM: cancels the token, whether or not a drain came first.</summary>
    /// <returns>Whether it is swallowed: whether it stopped, rather than finding the hook no longer listening.</returns>
    internal bool SigtermReceived()
    {
        lock (_gate)
        {
            if (!IsListening)
            {
                return false;
            }

            CancelTheToken();
            return true;
        }
    }

    // Read under the gate, so that a press racing Dispose never cancels a source that is disposed.
    private bool IsListening => !_disposed && !_stop.IsCancellationRequested;

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

    // Not inline: the callbacks registered on the token run on the thread pool, never on the console's handler thread.
    private void CancelTheToken() => _stop.CancelLettingCallbackFaultsGo();
}
