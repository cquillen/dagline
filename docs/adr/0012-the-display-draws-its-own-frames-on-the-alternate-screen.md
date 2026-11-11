# The display draws its own frames on the alternate screen

The display is the reporter in `Dagline.Spectre` that draws a run on a terminal. It does not use Spectre.Console's live display. Four times a second it reads the snapshot and builds a frame: a list of styled lines already cut to the width and height of the terminal. Spectre.Console turns each line into styled text, and the display writes the lines itself, on the terminal's alternate screen. When the run ends it leaves the alternate screen and prints the summary on the ordinary one.

The reason is that a run takes hours, and the display must not be able to hang it or be wrecked by one stray line. Spectre.Console's live display deadlocks when another thread writes to the console during a refresh, clears the screen and the scrollback when the window shrinks below it, and throws when output is redirected. It also cannot scroll, so its caller already has to choose which lines to show. Choosing the lines is most of the work. Once that is done, placing them is small.

A prototype drew the same faked run both ways. In a real terminal neither flickered and the two could not be told apart by eye, so the choice rests on what happens at the edges and not on how the display looks.

Five rules follow, and a reader could reasonably expect each to be otherwise:

1. **A frame depends only on the snapshot and the size of the terminal.** Nothing wraps and nothing scrolls. Where more is happening than fits, the frame says how much it left out.
2. **The display takes the alternate screen.** It is not a region at the foot of the scrollback. A resize is then an ordinary repaint from the top-left corner, and no half-drawn frame is ever left behind. A terminal that ignores the request, as GNU Screen does unless it is configured otherwise, still shows the display, because every frame is drawn from the top-left corner. The frame then covers what was on the screen, and the last frame stays above the summary. The display does not check for the alternate screen first.
3. **The display never fails the way other reporters do.** A reporter that throws is dropped (ADR 0010), which would leave the operator with a blank terminal for hours. If drawing fails, the display restores the terminal, prints one line saying so, and carries on as the plain-text reporter.
4. **The display is used only when standard output is a terminal that accepts ANSI sequences.** Otherwise the entry point uses the plain-text reporter. Standard input is not looked at, because the display reads no keys. The app can force either.
5. **Stray console output is not intercepted.** Writing to the console while the display is up is unsupported. A line that arrives anyway spoils the screen until the display next repaints all of it, which it does every few seconds whether or not anything changed.

## Considered options

- **Spectre.Console's live display.** Rejected for the reasons above. The deadlock decides it: the same stray line that would smudge our own frame until the next repaint can hang a run that has hours of external work in flight.
- **A live region below permanent lines in the scrollback.** Rejected because the region cannot be found again after a resize has reflowed the lines above it, and because a prototype of it filled the scrollback with warnings.
- **Capturing the console's output streams while the display is up.** Rejected because a library would be replacing streams that belong to the app, and output that bypasses them would still get through.
- **Writing ANSI sequences without Spectre.Console.** Rejected because styling, colour depth and the width of text in cells are work Spectre.Console has already done.
- **Using the display only where the alternate screen is known to work.** Rejected because the only way to know is to ask the terminal and read its reply from standard input, which the display never reads and which may be redirected. Guessing from the environment instead would take the display away from operators who run inside a terminal multiplexer, which is where a run of hours is most likely to be.
- **A full terminal UI framework.** Rejected because the display is passive: it has no input, focus or widgets to manage.
- **Leaving a dropped display dropped.** Rejected under the third rule.

## Consequences

- While a run is in progress nothing is written to the scrollback, and the operator cannot scroll back through the run. A process that is killed outright leaves nothing on the screen. The journal is the record, not the terminal.
- The app's own logging has to go to a file while the display is up.
- A frame can be tested as lines of text for a given snapshot and size, with no terminal.
- `Dagline.Spectre` depends on Spectre.Console for styling only. A breaking change to its live display does not reach us.
- The cursor handling is ours to keep right: hiding and restoring the cursor, entering and leaving the alternate screen on every way out, noticing a resize, and putting the cursor below the last frame before leaving, so that the summary follows the frame where the alternate screen was ignored.
