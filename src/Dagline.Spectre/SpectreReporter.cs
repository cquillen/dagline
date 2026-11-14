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
    /// The reporter chooses between the display and the plain-text reporter when it handles <see cref="RunStarted"/>, not when this
    /// method is called, and keeps that choice for the rest of the run. With <see cref="DisplayMode.Auto"/> it chooses the display
    /// when standard output is not redirected and the console accepts ANSI sequences, and the plain-text reporter otherwise.
    /// Standard input, whether the process looks like it is in CI, and the size of the console are not looked at.
    /// <see cref="DisplayMode.Force"/> always chooses the display, also on a console that is not a terminal, and
    /// <see cref="DisplayMode.Never"/> always chooses plain text, which then draws nothing on the console.
    /// The plain-text reporter is a <see cref="PlainTextReporter"/> on <paramref name="plainText"/>, or on standard output as it is
    /// when the run starts, with the options' clock.
    /// </para>
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
    /// A count of zero is left out. Under the header come a stop banner once a stop has been applied, a row per group, the tasks
    /// that are working, the tasks that are waiting on external systems, and the problems so far, sharing the lines left. Below
    /// 60 columns or 15 rows the frame is the header alone. The terminal display article of the documentation describes every pane.
    /// </para>
    /// <para>
    /// When it handles <see cref="RunEnded"/> the display stops its timer, so nothing is drawn after it, puts the cursor below the
    /// last frame, leaves the alternate screen, shows the cursor, and prints the summary on the ordinary screen through the same
    /// console: a rule, the run outcome with the duration and the journal's id and run number, the count of tasks per status
    /// with the earlier successes in brackets and the warnings, then every failure, cancelled task and warning of this run, and
    /// what a later run that continues the journal has left. No exit code is printed: the app chooses its own.
    /// </para>
    /// <para>
    /// What the display draws and prints, like the plain-text output, is for people and may change in any release: a program must
    /// not parse it.
    /// </para>
    /// <para>
    /// The display's drawing is serialised: a frame from the timer is never drawn while an event is being handled.
    /// While the display is up, the app's own logging must not go to the console.
    /// </para>
    /// <para>
    /// An exception while the display draws never leaves the reporter, and the run is not affected by it. The display stops
    /// drawing, restores the terminal (leaving the alternate screen and showing the cursor, as far as the console still allows),
    /// writes one line on the plain-text writer, <c>display failed, output continues as plain text:</c> followed by the
    /// exception's type and message, and from then on hands every event to a plain-text reporter on that writer. That reporter
    /// is first handed the <see cref="RunStarted"/> it missed, so its <c>run started</c> line follows the failure line, and then
    /// the event the display was drawing when it failed, if that was a later one. Earlier events are not replayed. If the writer
    /// or that reporter then throws, the run drops the reporter as it drops any reporter that throws, and the error is on
    /// <see cref="RunResult.ReporterErrors"/>; when that happens on a frame of the timer, it is thrown from the next event.
    /// </para>
    /// <para>
    /// The display also restores the terminal when the token it was handed is cancelled, because a stop ended the run's wait for
    /// reporters while it was still busy. A process that is killed leaves the terminal as it was.
    /// </para>
    /// </remarks>
    public static IReporter Auto(TextWriter? plainText = null, SpectreOptions? options = null)
        => new AutoReporter(plainText, options ?? new SpectreOptions());
}
