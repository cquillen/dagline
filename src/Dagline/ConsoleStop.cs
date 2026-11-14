using System.Runtime.InteropServices;

namespace Dagline;

/// <summary>
/// A <see cref="StopSignal"/> hooked to the console's cancel key and to SIGTERM, so that Ctrl+C or a termination request from
/// the operating system stops a run. Pass it to <see cref="Runner.RunAsync"/> and dispose it when the run has ended.
/// </summary>
/// <remarks>
/// <para>
/// Dagline hooks nothing unless the app calls <see cref="Hook"/>. This type is the one place in the core package that touches
/// the console, and it only listens: it writes nothing.
/// </para>
/// <para>
/// The hook counts Ctrl+C presses and SIGTERM signals together. The first requests a stop: the run starts nothing more and
/// waits for its running tasks, for at most its grace period. The second requests again, which ends that wait at once. Both
/// are swallowed, so the process lives on and the run winds down and returns its result as usual. A second SIGTERM after a
/// first therefore ends the grace period as a second Ctrl+C does, and a Ctrl+C after a SIGTERM counts as the second.
/// </para>
/// <para>
/// From the third on the signal swallows nothing and requests nothing, so the operating system ends the process as it would
/// with no hook at all. This is the way out of an app that hangs, even after a stop. A process ended this way leaves its
/// journal as a killed process does, and a display gets no chance to restore the terminal.
/// </para>
/// <para>
/// The signal hooks the console's cancel key event, <see cref="Console.CancelKeyPress"/>, and SIGTERM through
/// <see cref="PosixSignalRegistration"/>, and nothing else: no other key and no other process signal. There is no option to
/// leave SIGTERM unhooked. Of the cancel key event it handles Ctrl+C alone. Ctrl+Break, which raises the same event, is left
/// as it is: it is not counted, not swallowed and requests nothing, so it ends the process as it would with no hook. On
/// Windows, .NET raises SIGTERM for a shutdown or logoff, and Windows ends the process after a short while however the signal
/// is handled. On a platform where .NET cannot hook SIGTERM, the signal hooks Ctrl+C alone.
/// </para>
/// <para>
/// Ctrl+C and SIGTERM are counted by the hook itself, so a <see cref="StopSignal.Request"/> the app makes on the same signal
/// does not use up one of the two that are swallowed. Every other member is that of any stop signal.
/// </para>
/// <para>
/// An app with a stop trigger of its own, such as a file appearing or a timer, needs no hook: it creates a plain
/// <see cref="StopSignal"/> and calls <see cref="StopSignal.Request"/> itself.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// using var stop = ConsoleStop.Hook();
/// var result = await Runner.RunAsync(graph, options, stop);
/// return result.Outcome.ToExitCode();
/// </code>
/// </example>
public sealed class ConsoleStop : StopSignal, IDisposable
{
    private const int _swallowed = 2;

    private int _received;
    private int _disposed;
    private PosixSignalRegistration? _terminate;

    private ConsoleStop()
    {
    }

    /// <summary>Makes a stop signal and hooks it to the console's cancel key and to SIGTERM.</summary>
    /// <returns>A signal that is not requested, hooked until it is disposed.</returns>
    /// <remarks>
    /// Each call makes a new signal with a hook of its own. Two hooked at once would each count every Ctrl+C and SIGTERM and
    /// request on their own signal, so hook one at a time.
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
    /// Unhooks the signal from the console's cancel key and from SIGTERM. Either one after that is the app's own business
    /// again, and with nothing else hooked it ends the process.
    /// </summary>
    /// <remarks>
    /// The signal keeps every request made so far and can still be requested from code. Disposing a second time does nothing.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
#pragma warning disable RS0030 // Unhooks what Hook hooked.
            Console.CancelKeyPress -= OnCancelKeyPress;
#pragma warning restore RS0030
            _terminate?.Dispose();
        }
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

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs pressed)
    {
        if (pressed.SpecialKey == ConsoleSpecialKey.ControlC && TryRequest())
        {
            pressed.Cancel = true;
        }
    }

    private void OnTerminate(PosixSignalContext received)
    {
        if (TryRequest())
        {
            received.Cancel = true;
        }
    }

    private bool TryRequest()
    {
        if (Interlocked.Increment(ref _received) > _swallowed)
        {
            return false;
        }

        Request();
        return true;
    }
}
