using Spectre.Console;

namespace Dagline.Spectre;

/// <summary>
/// What an app may choose about the reporter that <see cref="SpectreReporter.Auto"/> gives. Every property has a default,
/// and the display itself has nothing to configure: its layout is fixed.
/// </summary>
public sealed record SpectreOptions
{
    /// <summary>
    /// Whether to give the display or the plain-text reporter. The default, <see cref="DisplayMode.Auto"/>, decides from the terminal.
    /// </summary>
    public DisplayMode Display { get; init; } = DisplayMode.Auto;

    /// <summary>
    /// <see langword="true"/> to draw with ASCII glyphs in place of the Unicode ones, for a terminal or font that lacks them.
    /// The default is <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// With it set, succeeded, failed, running, ready, waiting, pending, blocked, cancelled, warning and a filled bar cell are
    /// drawn as <c>+ x * o ~ . / # ! =</c> in place of <c>✓ ✗ ● ○ ◌ · ⊘ ■ ▲ ━</c>; rules and empty bar cells are <c>-</c>, text
    /// that was cut ends in <c>...</c>, and facts on one line are separated by <c>|</c>. Neither a frame nor the summary then
    /// writes a character outside ASCII of its own. Text that comes from the graph and the tasks, such as titles, status text,
    /// messages and warnings, is drawn as it was given.
    /// </remarks>
    public bool Ascii { get; init; }

    /// <summary>
    /// The console the display draws on and prints the summary to, or <see langword="null"/> for one made for standard output.
    /// </summary>
    /// <remarks>
    /// The display reads the console's size for every frame, writes every line through it, and uses its control sequences only
    /// when it accepts them. A test passes a console of a fixed size, such as Spectre.Console's test console, to capture what the
    /// display draws.
    /// </remarks>
    public IAnsiConsole? Console { get; init; }

    /// <summary>
    /// The clock the reporter redraws and measures elapsed time on, or <see langword="null"/> for <see cref="TimeProvider.System"/>.
    /// A test passes the same fake clock as it gives the run, and advances it to draw the next frame.
    /// </summary>
    public TimeProvider? TimeProvider { get; init; }
}
