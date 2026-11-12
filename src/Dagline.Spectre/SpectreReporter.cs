using Spectre.Console;

namespace Dagline.Spectre;

/// <summary>The entry point of the package: the one call that gives an app the right reporter for where it runs.</summary>
public static class SpectreReporter
{
    /// <summary>
    /// Gives a reporter that draws the run with the display on a terminal, or prints it as plain text elsewhere.
    /// Give it to the run in <see cref="RunOptions.Reporters"/>.
    /// </summary>
    /// <param name="plainText">
    /// Where the plain-text reporter writes, or <see langword="null"/> for standard output. The reporter never closes it.
    /// </param>
    /// <param name="options">What the app chooses, or <see langword="null"/> for the defaults of <see cref="SpectreOptions"/>.</param>
    /// <returns>A reporter for one run at a time.</returns>
    /// <remarks>
    /// <para>
    /// The display takes the terminal's alternate screen when it handles <see cref="RunStarted"/>: it hides the cursor and draws a
    /// first frame, then draws a new frame four times a second on a timer of the options' clock, reading the run as it is now from
    /// the <see cref="ISnapshotSource"/> that <see cref="RunStarted"/> carries. A change that no event carries, such as progress,
    /// therefore shows in the next frame. A frame is a list of lines already cut to the console's width, never more lines than its
    /// height, and the size is read again for every frame, so a resized console gets a frame at its new size. Nothing wraps and
    /// nothing scrolls.
    /// </para>
    /// <para>
    /// The frame begins with a header of three lines for the whole run. The first gives the journal's description, its id and
    /// which run of it this is (or that there is no journal), the percentage of tasks settled and the time elapsed. The second is
    /// one bar for the whole run, split by status. The third counts the tasks per status, with the running tasks that are waiting
    /// apart from those that are working and the earlier successes in brackets after the succeeded, then the warnings, and at the
    /// right the cap in use over its size, how many limits are full out of how many, and how many ready tasks are held.
    /// A count of zero is left out.
    /// </para>
    /// <para>
    /// When it handles <see cref="RunEnded"/> the display stops its timer, so nothing is drawn after it, puts the cursor below the
    /// last frame, leaves the alternate screen, shows the cursor, and prints the summary on the ordinary screen through the same
    /// console: a rule, the run outcome with the duration and the journal's id and run number, and the count of tasks per status
    /// with the earlier successes in brackets and the warnings. No exit code is printed: the app chooses its own.
    /// </para>
    /// <para>
    /// The display's drawing is serialised: a frame from the timer is never drawn while an event is being handled.
    /// While the display is up, the app's own logging must not go to the console.
    /// </para>
    /// </remarks>
    public static IReporter Auto(TextWriter? plainText = null, SpectreOptions? options = null)
    {
        options ??= new SpectreOptions();
        var console = options.Console ?? AnsiConsole.Create(new AnsiConsoleSettings());
        return new Display(new Terminal(console), options.TimeProvider ?? TimeProvider.System, Glyphs.Unicode);
    }
}
