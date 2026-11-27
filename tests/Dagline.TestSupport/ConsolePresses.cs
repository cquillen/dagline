namespace Dagline.TestSupport;

/// <summary>
/// Ctrl+C presses and SIGTERMs made in a test on a <see cref="ConsoleStop"/> that is hooked to nothing:
/// <c>using var console = ConsolePresses.Unhooked(); console.PressCtrlC();</c>.
/// </summary>
/// <remarks>
/// It is the public face of the internal test hook on <see cref="ConsoleStop"/>, which applies a press as the console's handlers do
/// once they have read it, and says whether it would be swallowed. No real console and no process signal are involved, so tests
/// running in parallel cannot press each other's hooks.
/// </remarks>
public static class ConsolePresses
{
    /// <summary>A console stop with a drain that is not requested and a token that is not cancelled, hooked to nothing.</summary>
    public static ConsoleStop Unhooked() => ConsoleStop.Unhooked();

    /// <summary>Applies a Ctrl+C as the hook would.</summary>
    /// <returns>Whether the hook would swallow it: whether it drained or stopped.</returns>
    public static bool PressCtrlC(this ConsoleStop console) => console.CtrlCPressed();

    /// <summary>Applies a SIGTERM as the hook would.</summary>
    /// <returns>Whether the hook would swallow it: whether it stopped.</returns>
    public static bool SendSigterm(this ConsoleStop console) => console.SigtermReceived();
}
