using Spectre.Console;

namespace Dagline.Spectre;

/// <summary>
/// What an app may choose about the reporter that <see cref="SpectreReporter.Create"/> gives. Every property has a default,
/// and the display itself has nothing to configure: its layout is fixed.
/// </summary>
public sealed record SpectreOptions
{
    /// <summary>
    /// Where plain text is written, or <see langword="null"/> for standard output as it is when the run starts.
    /// The reporter never closes it.
    /// </summary>
    /// <remarks>
    /// The plain-text reporter writes every line here when it is chosen in place of the display. When the display is chosen,
    /// it writes here only if drawing fails: one line saying so, then the rest of the run as plain text. Standard output is
    /// read when the reporter handles <see cref="RunStarted"/>, not when the options are made, so a writer the app sets with
    /// <see cref="System.Console.SetOut(TextWriter)"/> before the run starts is the one used.
    /// </remarks>
    public TextWriter? PlainText { get; init; }

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
    /// that was cut ends in <c>...</c>, and facts on one line are separated by <c>|</c>. Text that comes from the graph and the
    /// tasks, such as the journal's description, titles, status text, waiting text, messages, warnings, detail lines and the
    /// error a run ended with, is drawn with each character outside ASCII as <c>?</c>. Neither a frame nor the summary then
    /// writes any character outside ASCII.
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
