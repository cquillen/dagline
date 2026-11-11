// PROTOTYPE (throwaway, stubs only). The Spectre package.
using Spectre.Console;

namespace Dagline.Spectre;

public enum DisplayMode
{
    /// <summary>The display when standard output is a terminal that accepts ANSI sequences; otherwise plain text.</summary>
    Auto,
    /// <summary>The display regardless of the terminal (used by tests over a captured console, or a terminal we cannot detect).</summary>
    Force,
    /// <summary>Always plain text.</summary>
    Never,
}

public sealed record SpectreOptions
{
    public DisplayMode Display { get; init; } = DisplayMode.Auto;
    /// <summary>Use the ASCII glyph set instead of Unicode.</summary>
    public bool Ascii { get; init; }
    /// <summary>Where to draw. Default: the process's standard output. A test passes a console of fixed size.</summary>
    public IAnsiConsole? Console { get; init; }
}

public static class SpectreReporter
{
    /// <summary>Picks the display or a <see cref="PlainTextReporter"/> writing to <paramref name="plainText"/> (default: standard output). The display prints the summary itself.</summary>
    public static IReporter Auto(TextWriter? plainText = null, SpectreOptions? options = null) => throw new NotImplementedException();
}

/// <summary>Ctrl+C hook: the first press requests a stop, the second ends the grace period. Dispose to unhook.</summary>
public sealed class ConsoleStop : StopSignal, IDisposable
{
    public static ConsoleStop Hook() => throw new NotImplementedException();
    public void Dispose() { }
}
