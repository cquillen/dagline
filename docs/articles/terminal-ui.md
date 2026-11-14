# The terminal display

The `Dagline.Spectre` package draws a run on the terminal while it happens. The **display** fills the screen and redraws four times a second: how far the whole run has got, a strip of cells for each group, the tasks that are working, the tasks that are waiting on external systems, and the problems so far. When the run ends the display goes away and leaves the **summary** on the ordinary screen. Where output is not a terminal, the same call prints the run as plain text instead.

The core `Dagline` package has no console or UI dependency. The display is a [reporter](reporters.md) like any other, built on [Spectre.Console](https://spectreconsole.net/), which it uses only to style lines and measure text.

## Using it

Add the package and give the run the reporter that <xref:Dagline.Spectre.SpectreReporter.Create*> returns:

```csharp
using Dagline;
using Dagline.Spectre;

using var stop = ConsoleStop.Hook();

var result = await Runner.RunAsync(graph, new RunOptions
{
    Journal = Journal.None,
    Reporters = [SpectreReporter.Create()],
}, stop);

return result.Outcome.ToExitCode();
```

There is nothing to configure: the layout is fixed, and the display needs nothing from your app beyond the run. It prints the summary itself, so your app prints nothing at the end of a run. <xref:Dagline.Spectre.ConsoleStop> turns Ctrl+C into a stop: see [Stopping](#stopping). [Getting started](getting-started.md) builds a whole app around these lines.

Give one reporter to one run at a time.

## The frame

Each redraw is one **frame**: a list of lines already cut to the terminal's width, and never more lines than its height. Nothing wraps and nothing scrolls, so the screen never jumps. A frame mid-run, 100 columns wide:

```text
Dagline  no journal                                                              40%  elapsed 2m 40s
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━────────────────────
✓ 2  ● 3  ◌ 3  · 3  ✗ 1  ⊘ 3  ▲ 2                                                           cap 6/16
── Groups ──────────────────────────────────────────────────────────────────────────────────────────
✓ prepare 1/1
● shard-1 ●● 0/2
✗ shard-2 ✗⊘⊘ 0/3 ✗1
● shard-3 ✓● 1/2 ▲2
◌ shard-1 ◌· 0/2
◌ shard-2 ◌· 0/2
◌ shard-3 ◌· 0/2
⊘ finish  ⊘ 0/1
── Working (3) ─────────────────────────────────────────────────────────────────────────────────────
● validate/shard-1/totals       ━━━━━━━───────── 1,928 of 4,087  summing entities             2m 40s
● validate/shard-1/duplicates                                                                 2m 40s
● validate/shard-3/cross-check  ━━━━━━────────── 2,602 of 6,068  cross-checking               2m 40s
── Waiting on external systems (3) longest 2m 40s ──────────────────────────────────────────────────
◌ process/shard-1/extract       job 48067 running                                             2m 40s
◌ process/shard-2/extract       job 48074 running                                             2m 40s
◌ process/shard-3/extract       job 48013 running                                             2m 40s
── Problems (1 failed, 2 warnings) 3 tasks blocked behind 1 failure ────────────────────────────────
✗ validate/shard-2/totals                                                       ⊘ blocks 3  04:12:40
    412 of 3,464 entities failed the totals check
▲ validate/shard-3/totals  entity 78225 has no owner on record; default applied
▲ validate/shard-3/totals  entity 48290 has no owner on record; default applied
```

From top to bottom: the header, the stop banner once a stop has been asked for, the groups, then the working, waiting and problems panes. A pane with nothing to show takes no lines. Each status has a glyph and a colour, the same in the frame and the summary:

| Glyph | Status |
| --- | --- |
| `✓` | succeeded (an earlier success is a dimmer tick) |
| `●` | running and working |
| `◌` | running and waiting on an external system |
| `○` | ready |
| `·` | pending |
| `✗` | failed |
| `⊘` | blocked |
| `■` | cancelled |
| `▲` | warning |

Numbers are grouped, as in `1,928`. Durations are rounded down: `45s`, `20m 44s`, `1h 15m`. Times of day are UTC, as `04:12:40`.

### The header

Three lines about the whole run.

- **The first** names the run: the journal's description, its id and which run of it this is, as in `Dagline  all shards  journal 20261111T041200Z-a3f9c2 · run 2`, or `no journal`. Two displays side by side can be told apart by it. At the right are the percentage of tasks settled, rounded down, and the time elapsed since the run started. There is no estimate of the time remaining, since the run cannot know it.
- **The second** is one bar for the whole run, split by status: earlier successes first and dimmer, then the successes of this run, the failed, blocked and cancelled tasks, the working and the waiting ones. The rest of the bar, the tasks not yet started, is empty.
- **The third** counts the tasks per status: succeeded, working, waiting, ready, pending, failed, blocked, cancelled, then the warnings. Earlier successes follow the succeeded in brackets, as in `✓ 1,560 (1,543 earlier)`, so you can see how much this run did itself. At the right are the running tasks over the [max running tasks](running-and-stopping.md#sizing-the-max-running-tasks-and-limits) (`cap 55/96`), how many limits are full out of how many (`1 of 11 limits full`), and how many ready tasks are [held](../../GLOSSARY.md#scheduling) (`6 ready, held`), so a busy run can be told from a throttled one.

A count of zero is left out, and so are full limits and held tasks when there are none. The running tasks over the max running tasks are always shown.

### Groups

One row per group two levels down, plus one row for each top-level node that has no group beneath it, in declaration order. When those rows do not fit, there is one row per top-level node instead. When even those do not fit, the pane keeps to the lines it has, and its last line says how many rows it left out, as in `and 4 more rows`.

A row is the node's status glyph, its title or else the last segment of its path, then one cell per child in the glyph and colour of that child's status, then succeeded over total (`1,087/1,104`) and the failures (`✗2`) and warnings (`▲3,173`) beneath it when there are any. A group's glyph is its derived status, drawn as waiting when every task running beneath it is waiting. A row with more children than it has room for shows a bar split by status in place of the cells, so a large graph still fits on one screen.

### Working

The tasks that are running and not waiting, in declaration order, with their number in the title. Each line has the task's path, a progress bar and its count (`1,928 of 4,087`, or the count alone when it has no total, and neither when it has reported no count), its status text, and at the right how long it has been running. When they do not all fit, the last line says `and 12 more working`. When tasks are running and all of them are waiting, the pane is its title line alone, which says so, as in `Working (0) all 7 running tasks are waiting`.

### Waiting on external systems

Shown only while a task is [waiting](waiting-on-external-systems.md). The title has how many are waiting and the longest wait. The pane lists up to five tasks, the longest wait first: each with its path, its status text or else the text of its wait, and how long it has waited. When there are more, the last line says how many and how long the newest has waited, as in `and 49 more, the newest waiting 25s`. Eighty waiting tasks therefore never push three working ones off the screen.

### Problems

The title counts the failed and cancelled tasks and the warnings, and how many tasks are blocked behind how many failures. Below it:

- **Failures**, newest first, two lines each: the path with how many tasks it blocks and when it failed, then its message on one line. The latest and the most damaging problems are in front of you.
- **Cancelled tasks**, newest first, with their message and when they were cancelled.
- **The latest three warnings**, one line each with the path of the task. Every other warning is only counted, in the title and on the groups rows, so warnings never flood the screen.

The latest warnings come from the `WarningAdded` events the display has handled, since a snapshot does not say when a warning was added. So only kept warnings appear there (a warning past the number a task keeps raises no event), and they can lag a moment behind the counts, which come from the snapshot.

When the pane is cut, it shows whole entries, never a failure without its message, and ends with how many of each kind it left out: `and 4 more failures, 1 more cancelled task, 2 more warnings`.

### Sharing the space

The header, the stop banner and the groups come first. The lines left are shared between the three panes below:

1. Each pane that has something to show gets two lines first, its title and one more, working first, then waiting, then problems.
2. Problems then grow to a third of the lines left, and never past it.
3. Waiting grows to all it wants, which is at most five tasks.
4. Working grows to all it wants.

Lines that no pane can use stay blank, so problems never take more than a third, or two lines when a third is fewer. No pane is given fewer lines than it needs to draw: when only one line is left for a pane that needs two, it gets none, and the line goes to a pane after it. A pane given fewer than two lines draws nothing, except the working pane when every running task is waiting, which needs only its title. A pane given two lines and more than it can show draws its title and the line that says how many it left out.

### Width

Text that does not fit is cut with an ellipsis, `…`. A path keeps its start, and the time at the end of a line is always kept. In the header's first line the title gives way, so the percentage and the elapsed time stay even in a terminal too narrow for the title. Status text and the text of a wait are drawn as the task gave them, except that a line break is drawn as a space, in the frame and in the summary, as the plain-text reporter does.

### Small terminals

Below 60 columns or 15 rows, the frame is the header alone, cut to fit, so a narrow split still shows how far the run has got. The stop banner is not shown then. Size never switches the display to plain text, and the size is read again for every frame, so a resized terminal gets a frame at its new size.

## Stopping

<xref:Dagline.Spectre.ConsoleStop.Hook*> gives a <xref:Dagline.StopSignal> hooked to the console's cancel key. Pass it to the run and dispose it when the run has ended, as `using var` does:

- **The first Ctrl+C** requests a stop: the run starts nothing more and waits for its running tasks to end, for at most its [grace period](running-and-stopping.md#the-grace-period).
- **The second Ctrl+C** requests again, which ends that wait at once.
- Both are swallowed, so your process lives on, the run winds down and `RunAsync` returns its result.
- **From the third Ctrl+C on** nothing is swallowed, so the operating system ends the process. That is the way out of an app that hangs, even after a stop.

Disposing unhooks the key, and a press after that is your app's own business again. The signal hooks nothing but the console's cancel key event, and the core package still hooks nothing at all. Of that event it handles Ctrl+C alone: Ctrl+Break is not counted and not swallowed, so it ends the process as it would with no hook. The two presses are counted by the hook itself, so a `Request` your app makes on the same signal does not use one up. The display does not depend on the hook: any stop signal shows the banner.

### The stop banner

Once a stop has been applied, one line appears under the header:

```text
 STOPPING  waiting for 11 running tasks to end · 26s left · Ctrl+C again to stop waiting
```

It says how many running tasks the run is waiting for and the time left until the deadline, which counts down as the display redraws. With an unlimited grace period the time is left out. When Dagline itself stopped the run, because its [journal could not be written](running-and-stopping.md#when-a-journal-write-fails), the banner says so, so that you do not think you pressed something:

```text
 STOPPING  the journal could not be written · waiting for 3 running tasks to end · 26s left · Ctrl+C to stop waiting
```

After a stop that Dagline asked for, your first request already ends the wait, so the banner says `Ctrl+C` and not `Ctrl+C again`.

On a terminal too narrow for the whole banner, what the run is waiting for is cut, and what Ctrl+C does is always kept at the end of the line.

## The summary

When the run ends, the display stops drawing, leaves the alternate screen, shows the cursor and prints the summary on the ordinary screen through the same console. It stays in your scrollback. It has the same content as the [plain-text summary](reporters.md#the-summary), in the display's own wording, glyphs and colours, each part after a blank line and left out when it has nothing to show. The run of the frame above ends with:

```text
────────────────────────────────────────────────────────────────────────────────────────────────────
Run incomplete  2m 40s
✓ 11 succeeded  ✗ 1 failed  ⊘ 3 blocked  ▲ 2 warnings

Failures (1)
✗ validate/shard-2/totals  reported failure · 04:12:40 · ran 40s
    412 of 3,464 entities failed the totals check
    ⊘ blocked 3 tasks: validate/shard-2/cross-check, validate/shard-2/sign-off, finish (1)

Warnings (2 on 1 task)
▲ validate/shard-3/totals  2
    entity 48290 has no owner on record; default applied
    entity 78225 has no owner on record; default applied
```

A run on a journal also names the journal on the outcome line, as in `Run stopped  5s · journal 20261111T041200Z-a3f9c2 · run 1`, and ends with what a later run has left: `A run that continues journal 20261111T041200Z-a3f9c2 has 1 task left to run.`

In order:

1. A rule, then the run outcome (`Run complete`, `Run incomplete` or `Run stopped`), the duration and the journal's id and run number. A run whose journal could no longer be written reads `Run ended with an error`, with the error's message beneath.
2. The count per status, with earlier successes in brackets, and the warnings.
3. How many tasks a stopped run never reached.
4. **Failures**, in declaration order: the kind, when the task failed, how long it ran and how far it got; its message; its detail lines, and how many more were counted but not kept; and what it blocked, named by the roots of the cascade, with the number of tasks beneath a root that is a group, as in `finish (1)`.
5. **Cancelled** tasks, each with its message if it has one, such as `still running when the run stopped waiting` for a task the run let go of.
6. **Warnings**: the total and how many tasks have any, then each task with warnings added in this run, its kept warnings beneath it and how many more were not kept, and one line for the warnings on earlier successes, which an earlier run's summary listed.
7. For a run with a journal: how many tasks a run that continues it has left, the earlier successes set aside to run again, and how many journal paths are not in the graph.

There is no exit code in the summary, so it never disagrees with the code your app returns.

## Plain text instead

`SpectreReporter.Create` decides once, when it handles <xref:Dagline.RunStarted>, and keeps that choice for the run:

- With `DisplayMode.Auto`, the default, it chooses the display when standard output is not redirected and the console accepts ANSI sequences, and the plain-text reporter otherwise. Standard input is not looked at, so piping input into your app keeps the display, and neither is whether the process looks like it runs in CI, nor the size of the terminal.
- `DisplayMode.Force` always chooses the display, even on a console that is not a terminal.
- `DisplayMode.Never` always chooses plain text, and nothing is drawn on the console.

The plain-text reporter is a <xref:Dagline.PlainTextReporter> on the writer you set as <xref:Dagline.Spectre.SpectreOptions.PlainText>, or on standard output as it is when the run starts, and prints the run as timestamped lines: see [the plain-text reporter](reporters.md#the-plain-text-reporter). The reporter never closes the writer. If the display is chosen and drawing fails, the rest of the run goes to the same writer as plain text, after one line saying so.

```csharp
using var log = File.CreateText("run.log");
var reporter = SpectreReporter.Create(new SpectreOptions { PlainText = log });
```

Give an operator with an unusual terminal a flag to fall back on:

```csharp
var plain = args.Contains("--plain");
var reporter = SpectreReporter.Create(new SpectreOptions
{
    Display = plain ? DisplayMode.Never : DisplayMode.Auto,
});
```

### ASCII glyphs

For a terminal or font that lacks the Unicode glyphs, set `Ascii`:

```csharp
var reporter = SpectreReporter.Create(new SpectreOptions { Ascii = true });
```

The glyphs for succeeded, failed, running, ready, waiting, pending, blocked, cancelled, warning and a filled bar cell become `+ x * o ~ . / # ! =`, in place of `✓ ✗ ● ○ ◌ · ⊘ ■ ▲ ━`. Rules and empty bar cells are `-`, cut text ends in `...`, and facts on one line are separated by `|`. Text from your graph and tasks, such as the journal's description, titles, status text, waiting text, messages, warnings, detail lines and the error a run ended with, is drawn with each character outside ASCII as `?`, so `café` becomes `caf?`. Neither a frame nor the summary then writes any character outside ASCII.

### In your own tests

<xref:Dagline.Spectre.SpectreOptions> takes the console to draw on and the clock to redraw on. Pass Spectre.Console's test console of a fixed size with `DisplayMode.Force`, and the same fake clock as the run: advancing the clock by 250 milliseconds draws the next frame, with no waiting on real time.

## Your app's own output

> [!WARNING]
> While the display is up, your app's own logging must go to a file, and not to the console.

The display owns the terminal from the moment it handles `RunStarted` until it has printed the summary. It does not intercept other writes to the console, and it cannot tell that they happened:

- A line your app, a library or a logger writes to standard output or standard error lands on the alternate screen, in the middle of a frame. The next frame, at most a quarter of a second later, draws over it, so it is gone before anyone can read it.
- It is not in your scrollback either, because nothing on the alternate screen is.
- A write that comes while a frame is being drawn can land between the display's own control sequences and spoil that frame until the next one.

So, while a run with the display is in progress:

- Send your logging to a file. A logging framework's file target, or a `TextWriter` you open on a file, keeps every line.
- Do not pass `Console.Out` or `Console.Error` to a logger or to a second reporter.
- What you print before `RunAsync` starts, and after it returns, is safe: the display is not up then.
- If your app must log to the console, choose plain text with `DisplayMode.Never`, and give the plain-text reporter and your logging one synchronized writer, as [the writer and the clock](reporters.md#the-writer-and-the-clock) describes.

## The terminal while a run is in progress

The display draws on the terminal's alternate screen, the one full-screen programs such as editors use. That has two consequences.

- **The run is not in your scrollback while it is in progress.** Each frame replaces the one before, and none of them is kept. What stays when the run has ended is the summary, printed on the ordinary screen, with whatever was there before the run above it.
- **A killed process can leave the terminal unrestored.** The display restores the terminal on every way out it controls: the end of the run, a drawing failure, and a stop that ends the run's wait for reporters while the display is still busy. A process that is killed, by the third Ctrl+C or from outside, gives it no chance, so the terminal can be left on the alternate screen with the cursor hidden. On Unix, `tput rmcup; tput cnorm` or `reset` brings it back. On any terminal, writing the sequences `ESC [ ? 1049 l` and `ESC [ ? 25 h` (without the spaces) leaves the alternate screen and shows the cursor, as PowerShell's `Write-Host "$([char]27)[?1049l$([char]27)[?25h"` does. A journal is not harmed by a killed process: see [After a crash](journals.md#after-a-crash).

Every frame repaints every line, so a stray line written to the console is gone within a quarter of a second, and a resize repaints cleanly at the next frame. The display does not check that the terminal honours the alternate screen: on one that does not, the frames are drawn on the ordinary screen.

## When drawing fails

The console belongs to your app and can fail: a terminal that has gone away, a console that throws. An exception while the display draws never leaves the reporter, and the run is not affected by it:

1. The display stops drawing and restores the terminal, as far as the console still allows.
2. It writes one line on the plain-text writer: `display failed, output continues as plain text:` followed by the exception's type and message.
3. From then on it hands every event to a plain-text reporter on that writer. That reporter is first handed the `RunStarted` it missed, so its `run started` line follows the failure line, and then the event the display was drawing when it failed, if that was a later one. Earlier events are not replayed.

Nothing is on <xref:Dagline.RunResult.ReporterErrors>, since the reporter carried on. If the plain-text writer then throws too, the run drops the reporter as it drops any reporter that throws, and the error is on `ReporterErrors`.
