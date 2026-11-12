namespace Dagline.Spectre;

/// <summary>
/// Whether <see cref="SpectreReporter.Auto"/> gives the display, which draws the run on the terminal, or the plain-text
/// reporter, which prints it as lines of text.
/// </summary>
public enum DisplayMode
{
    /// <summary>
    /// The display when standard output is a terminal that accepts ANSI sequences, and the plain-text reporter otherwise.
    /// Standard input, and whether the process looks like it is in CI, are not looked at.
    /// </summary>
    Auto,

    /// <summary>
    /// The display, whatever the terminal: also on a console that is not a terminal, such as a captured one in a test.
    /// </summary>
    Force,

    /// <summary>The plain-text reporter, whatever the terminal.</summary>
    Never,
}
