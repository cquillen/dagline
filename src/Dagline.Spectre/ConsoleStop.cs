namespace Dagline.Spectre;

/// <summary>
/// A <see cref="StopSignal"/> hooked to the console's cancel key, so that Ctrl+C stops a run. Pass it to
/// <see cref="Runner.RunAsync"/> and dispose it when the run has ended.
/// </summary>
/// <remarks>
/// <para>
/// The first press of the cancel key requests a stop: the run starts nothing more and waits for its running tasks, for at
/// most its grace period. The second press requests again, which ends that wait at once. Both presses are swallowed, so the
/// process lives on and the run winds down and returns its result as usual.
/// </para>
/// <para>
/// From the third press on the signal swallows nothing and requests nothing, so the operating system ends the process as it
/// would with no hook at all. This is the way out of an app that hangs, even after a stop. A process ended this way leaves its
/// journal as a killed process does, and a display gets no chance to restore the terminal.
/// </para>
/// <para>
/// The signal hooks the console's cancel key event, <see cref="Console.CancelKeyPress"/>, and nothing else: no other key and no
/// other process signal. Of that event it handles Ctrl+C alone. Ctrl+Break, which raises the same event, is left as it is: it
/// is not counted, not swallowed and requests nothing, so it ends the process as it would with no hook. Presses are counted by the hook itself, so a <see cref="StopSignal.Request"/> the app makes on the same
/// signal does not use up one of the two presses that are swallowed. Every other member is that of any stop signal.
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
    private const int _swallowedPresses = 2;

    private int _presses;
    private int _disposed;

    private ConsoleStop()
    {
    }

    /// <summary>Makes a stop signal and hooks it to the console's cancel key.</summary>
    /// <returns>A signal that is not requested, hooked until it is disposed.</returns>
    /// <remarks>
    /// Each call makes a new signal with a hook of its own. Two hooked at once would each count every press and request on
    /// their own signal, so hook one at a time.
    /// </remarks>
    public static ConsoleStop Hook()
    {
        var stop = new ConsoleStop();
        Console.CancelKeyPress += stop.OnCancelKeyPress;
        return stop;
    }

    /// <summary>
    /// Unhooks the signal from the console's cancel key. A press after that is the app's own business again, and with nothing
    /// else hooked it ends the process.
    /// </summary>
    /// <remarks>
    /// The signal keeps every request made so far and can still be requested from code. Disposing a second time does nothing.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
        }
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs pressed)
    {
        if (pressed.SpecialKey != ConsoleSpecialKey.ControlC)
        {
            return;
        }

        if (Interlocked.Increment(ref _presses) > _swallowedPresses)
        {
            return;
        }

        pressed.Cancel = true;
        Request();
    }
}
