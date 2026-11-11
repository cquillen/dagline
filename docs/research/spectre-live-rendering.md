# Spectre.Console live rendering: capabilities and limits

Research for [issue #4](https://github.com/cquillen/dagline/issues/4). Written 2026-11-10. This file lives on a throwaway branch and is never merged.

**Question.** What can Spectre.Console's live rendering do, and where does it break, for a passive (no keyboard input) display of a few thousand nested tasks that updates for hours in a terminal about 40 lines tall, with a plain-text fallback when output is not interactive?

**Version examined.** Spectre.Console `0.57.2` (tag commit [`bbbb572`](https://github.com/spectreconsole/spectre.console/tree/0.57.2)), the version pinned in `Directory.Packages.props` and the latest release.

## How to read this

Each claim carries a tag saying how it was established:

| Tag | Meaning |
| --- | --- |
| **[source]** | Read in the library source at tag `0.57.2`. Links go to the exact lines. |
| **[run]** | Observed by running a small program against the `0.57.2` NuGet packages on .NET 10.0.15, Windows. See [Experiments](#experiments). |
| **[issue]** | Reported in the Spectre.Console issue tracker. State (open or closed) is given. |
| **[docs]** | Stated in the official docs at spectreconsole.net. |
| **[unverified]** | An inference or a claim that could not be checked. Collected under [Not verified](#not-verified). |

All experiments were headless: output went to an in-memory writer or a file, never to a real terminal. Nothing in this document was seen on a screen. Where a finding is about what the operator would see, it rests on the bytes the library emits, not on observation.

## Hard limits, most decision-relevant first

1. **Calling `Refresh()` or `UpdateTarget()` on a `LiveDisplay` while another thread writes to the same console deadlocks.** Reproduced on 0.57.2 in 5 of 6 trials, including at a realistic rate (one refresh per 100 ms, one log line per 5 ms). Open since 2021 ([#435](https://github.com/spectreconsole/spectre.console/issues/435)). `Progress` does not have this problem. **[run] [source] [issue]**
2. **`LiveDisplay` has no non-interactive fallback and does not check whether the terminal is interactive.** With stdout redirected on Windows it throws `IOException: The handle is invalid` on start. Only `Progress` and `Status` fall back. The caller must make the decision itself. **[run] [source] [issue]**
3. **There is no scrolling and no viewport.** Content taller than the terminal is cut at the top or the bottom, with or without an ellipsis line. Nothing lets you show "lines 200 to 240". **[source] [issue]**
4. **Every refresh re-renders the whole target, however little of it is visible.** A 3,000-node `Tree` cost about 60 ms and 21 MB of allocation per refresh, against 0.3 ms for 38 pre-selected rows. There is no partial update, by design. **[run] [issue]**
5. **When a live display that overflowed ends without `AutoClear`, the entire target is written to the terminal uncropped.** For a 3,011-line tree that was 3,011 lines of output at exit. **[run] [source]**
6. **Widgets are not safe to mutate while a refresh is rendering them.** Adding nodes to a `Tree` from another thread during refreshes produced a spurious `CircularTreeException`. **[run]**
7. **Spectre.Console installs no Ctrl+C handler.** The cursor is hidden when a live display starts and is shown again only in a `finally` block. **[source]**
8. **Other output during a live display works only if it goes through the same `IAnsiConsole`, and even then it leaves stray characters in some cases** ([#1146](https://github.com/spectreconsole/spectre.console/issues/1146), open). **[run] [issue]**

## Live display, Progress, Tree, Table and Layout: what composes with what

There are two different kinds of thing, and they compose differently.

**Renderables** (`Tree`, `Table`, `Grid`, `Rows`, `Columns`, `Panel`, `Padder`, `Layout`, `Markup`, `Text`, `Rule`, `BarChart`, `BreakdownChart`) all implement `IRenderable` and nest freely. A tree node holds any renderable, a table cell holds any renderable, a layout region holds any renderable. **[source]** ([`TreeNode`](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console/Widgets/TreeNode.cs#L24), [docs: rendering model](https://spectreconsole.net/console/explanation/understanding-rendering-model/))

**Live runners** (`LiveDisplay`, `Progress`, `Status`, and prompts) are not renderables. Each takes exclusive ownership of the console for the duration of a callback. Only one can run at a time on a console: starting a second throws `InvalidOperationException: Trying to run one or more interactive functions concurrently...`. **[run] [source]** ([`DefaultExclusivityMode`](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console/Internal/DefaultExclusivityMode.cs#L15-L18))

What follows from that:

- **`LiveDisplay` shows any one renderable**, so a `Tree`, a `Table`, or a `Layout` holding several widgets all work. **[source] [docs]**
- **`Progress` cannot be placed inside a `LiveDisplay`, a `Layout`, or a `Tree`.** It is a runner, and it draws a flat grid with one row per task. Nesting is not modelled; indentation in the description text is the only way to suggest it. **[source]** ([`DefaultProgressRenderer.Update`](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console/Live/Progress/Renderers/DefaultProgressRenderer.cs#L62-L117))
- **`Progress` can be wrapped, not embedded.** `Progress.UseRenderHook` receives the progress grid and the task list on each refresh and returns the renderable to draw, so the grid can be put inside a panel or a layout with other content beside it. **[source]** ([`Progress.RenderHook`](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console/Live/Progress/Progress.cs#L14))
- **The progress bar widget is internal.** `ProgressBar` is not public, so a `LiveDisplay` cannot put a real progress bar in a tree node or table cell. A bar there has to be drawn as text, as a custom `IRenderable`, or with `BreakdownChart` or `BarChart`. **[source]** ([`ProgressBar`](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console/Widgets/ProgressBar.cs#L3))
- **Spinners are available as data.** `Spinner.Frames` and `Spinner.Interval` are public, so a `LiveDisplay` can animate one by picking a frame from elapsed time. Only `Progress` animates them for you. **[source]**
- **A tree node's label cannot be changed after the node is created.** `TreeNode.Renderable` is internal and get-only. To change a label, replace the node, rebuild the tree, or give the node a custom renderable that reads mutable state. `TreeNode.Expanded` is settable, and a collapsed node does not render its children. **[source]** ([`TreeNode`](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console/Widgets/TreeNode.cs#L8-L18))
- **`Table` supports in-place edits**: `UpdateCell`, `InsertRow`, `RemoveRow`. **[source]**
- **`Layout` always takes the full terminal height** unless a parent gives it a height, and it silently cuts each region's content at the bottom, with no ellipsis. A layout of header, 100-row body and footer in a 20-line console rendered exactly 20 lines, with the body stopping at row 17. `Panel` with a fixed `Height` cuts the same way. **[run] [source]** ([`Layout.Render`](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console/Widgets/Layout/Layout.cs#L212), [crop](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console/Widgets/Layout/Layout.cs#L278))
- **A layout cannot be partly redrawn.** The maintainer on [#1345](https://github.com/spectreconsole/spectre.console/issues/1345) (open): "It is not possible to partially update a layout. This is by design and nothing I see that we will add support for since it would require a complete rewrite of most of the library." **[issue]**

## Content taller than the terminal

`LiveDisplay` compares the rendered height with `Profile.Height` and applies two settings. **[source]** ([`LiveRenderable.Render`](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console/Live/LiveRenderable.cs#L110-L155))

| `Overflow` | Effect |
| --- | --- |
| `Ellipsis` (default) | Cut to the terminal height; one line is replaced by a yellow `…`. |
| `Crop` | Cut to the terminal height, with no marker. |
| `Visible` | Write every line. |

| `Cropping` | Effect |
| --- | --- |
| `Top` (default) | Lines are removed from the **top**, so the **end** of the content stays visible. |
| `Bottom` | Lines are removed from the bottom, so the start stays visible. |

Observed with a 3,011-line tree in an 80x40 console **[run]**:

| Setting | Lines per frame | First line shown | Last line shown |
| --- | --- | --- | --- |
| Ellipsis, Top (the default) | 40 | `…` | last leaf of the tree |
| Ellipsis, Bottom | 40 | root | `…` |
| Crop, Top | 40 | a leaf near the end | last leaf |
| Crop, Bottom | 40 | root | a leaf near the start |
| Visible | 3,011 | root | last leaf |

Consequences:

- **The default shows the tail of the content, not the head.** A tree taller than the terminal loses its root and its first groups.
- **There is no scroll offset.** No API selects a window in the middle. A request for one is open and unanswered ([#1559](https://github.com/spectreconsole/spectre.console/issues/1559)). Choosing which lines to show is the caller's job, done before handing a renderable to Spectre. **[source] [issue]**
- **`Visible` breaks in-place updating once the frame is taller than the terminal.** The renderer moves the cursor up by frame height minus one and overwrites ([`PositionCursor`](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console/Live/LiveRenderable.cs#L62-L70)). A terminal cannot move the cursor above the top of its window, so each frame is appended below the previous one. This is the mechanism behind [#316](https://github.com/spectreconsole/spectre.console/issues/316) (closed in 2021), where the maintainer wrote that "there is no way for Spectre.Console to know what tasks currently is being visible". **[source] [issue]**, on-screen effect **[unverified]**
- **The region never shrinks.** After a 30-line frame, a 5-line frame is still drawn 30 lines tall, padded with blanks, until the terminal itself gets smaller than the region. **[run] [source]** ([`Inflate`](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console/Live/LiveRenderable.cs#L157))
- **Ending an overflowed display dumps everything.** If the last frame overflowed and `AutoClear` is off, the whole target is rewritten with `Visible`: 3,011 lines in the test above. With `AutoClear(true)` the region is erased instead and nothing is left. A final frame that fits does not trigger the dump. **[run] [source]** ([`Completed`](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console/Live/LiveDisplayRenderer.cs#L18-L41))
- **`Progress` has no overflow setting.** It always uses Ellipsis with Top. `HideCompleted(true)` removes finished rows, which is the only built-in way to keep it short. **[source]** ([renderer](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console/Live/Progress/Renderers/DefaultProgressRenderer.cs#L21))

## The refresh model, flicker, and thread-safety

### When a frame is drawn

- **`LiveDisplay` has no timer and no background thread.** A frame is drawn when the callback calls `ctx.Refresh()` or `ctx.UpdateTarget()`, when anything else is written to the same console, and once when the callback returns. The initial target is not drawn until the first of these: in a test that started with "frame 0" and then updated twice, "frame 0" never appeared in the output. **[source] [run]** ([`LiveDisplayContext`](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console/Live/LiveDisplayContext.cs#L25-L43))
- **The docs say otherwise, and are wrong for `LiveDisplay`.** The rendering-model page says "A background thread triggers refreshes (default: every 100ms)". That is true only of `Progress` and `Status`. **[docs] [source]**
- **`Progress` refreshes itself** on a background thread every `RefreshRate` (100 ms by default) unless `AutoRefresh(false)`. **[source]** ([`Progress`](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console/Live/Progress/Progress.cs#L47), [thread](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console/Live/Progress/ProgressRefreshThread.cs#L43))

So a `LiveDisplay` that shows elapsed times or a spinner, or that should react to a resize, needs its own timer.

### What a frame costs

Each refresh renders the whole target to segments, then cuts to the terminal height, then writes the whole region. There is no diff against the previous frame. **[source]** ([render then crop](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console/Live/LiveRenderable.cs#L104-L110))

Measured per refresh after warm-up, 120x40 console, output discarded so terminal I/O is excluded **[run]**:

| Target | Time | Allocated |
| --- | --- | --- |
| `Rows`, 38 lines | 0.27 ms | 0.15 MB |
| `Tree`, 111 nodes | 0.86 ms | 0.4 MB |
| `Tree`, 1,011 nodes | 17 ms | 6.7 MB |
| `Tree`, 3,011 nodes | 60 ms | 21 MB |
| `Table`, 100 rows | 4.2 ms | 2.4 MB |
| `Table`, 3,000 rows | 142 ms | 70 MB |
| `Rows`, 3,000 lines | 19 ms | 7.9 MB |

These are one machine's numbers; the ratios are the point. At ten refreshes a second, a 3,000-node tree would spend about 60% of one core and allocate about 200 MB a second to draw 40 lines. Handing Spectre only the lines that will be shown costs under a millisecond.

### Flicker

- **The design avoids clearing.** The renderer returns the cursor to the start of the region and overwrites, padding lines to the previous frame's width, rather than erasing first. **[source] [docs]** ([docs: flicker prevention](https://spectreconsole.net/console/explanation/understanding-rendering-model/))
- **A regression showed how fragile this is.** Version 0.48 added a per-line erase to fix stray characters; it caused "blatant flickering" ([#1466](https://github.com/spectreconsole/spectre.console/issues/1466), [#1521](https://github.com/spectreconsole/spectre.console/issues/1521)) and was reverted in 0.49. **[issue]**
- **Frames are not buffered or made atomic.** The writer makes several separate writes per styled segment straight to the output stream, and the library never emits the synchronized-output escape sequence. **[source]** ([`AnsiWriter.Write`](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console.Ansi/AnsiWriter.cs#L74-L109)) Whether this is visible as tearing on a 40-line frame is **[unverified]**.
- No flicker was observed first-hand, because nothing was run in a real terminal. **[unverified]**

### Thread-safety

The docs carry the same caution on every live page: "Live display is not thread safe. Using it together with other interactive components such as prompts, progress displays, or status displays is not supported." **[docs]** ([Live Display](https://spectreconsole.net/console/live/live-display/)) The source and the experiments make that concrete:

- **Single writes are serialised.** `IAnsiConsole.Write` takes a render lock, so two threads writing do not interleave within one call. **[source]** ([`AnsiConsoleFacade.Write`](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console/Internal/Backends/AnsiConsoleFacade.cs#L35-L41))
- **`LiveDisplay` deadlocks against a concurrent writer.** `Refresh()` takes the context lock and then the render lock ([lines 39-41](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console/Live/LiveDisplayContext.cs#L39-L41)). Any other `console.Write` takes the render lock and then, inside the render hook, the context lock ([line 45](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console/Live/LiveDisplayRenderer.cs#L45)). Opposite order, so two threads can each hold one and wait for the other. Results **[run]**:

  | Scenario | Trial 1 | Trial 2 | Trial 3 |
  | --- | --- | --- | --- |
  | Both threads in tight loops | deadlock at once | deadlock after 3 updates | deadlock after 4 updates |
  | Refresh every 100 ms, log line every 5 ms | deadlock after 121 updates | deadlock after 2 updates | ran 20 s clean |
  | `Progress`, both threads in tight loops | 2,559 refreshes and 34,060 log writes in 5 s, no deadlock | | |

  The same lock order is used when the display ends, so a writer thread that is still running at exit carries the same risk. **[source]**
- **`Progress` is safe against concurrent writers** because it releases its own lock before writing. **[source] [run]** ([`ProgressContext.Refresh`](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console/Live/Progress/ProgressContext.cs#L190-L194)) `ProgressTask` members take a per-task lock, so tasks can be advanced from many threads. **[source]**
- **Renderables are rendered on whichever thread calls `Refresh()`, with no protection against concurrent mutation.** `TreeNode.Nodes` is a plain `List<TreeNode>`. With one thread adding and removing nodes while another refreshed, 8 of about 800 refreshes threw `CircularTreeException: Cycle detected in tree - unable to render.` There was no cycle. `Table`'s row collection locks each operation; it was not tested under concurrency. **[run] [source]**

## Other console output while a live display is active

**Through the same `IAnsiConsole`: supported, with defects.** Every write passes through the live display's render hook, which moves the cursor to the top of the region, lets the new output through, then redraws the region below it. The effect is that the output appears above the region and scrolls up. **[source]** ([`Process`](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console/Live/LiveDisplayRenderer.cs#L43-L57)) Each such write redraws the whole region, so it costs a full frame.

Known defects:

- **Stray characters.** The output is written over the old top line of the region without erasing it. The bytes for one `MarkupLine("log")` over a region whose first line was `LIVE-LINE-ONE-IS-LONG` were `\r ESC[2A log \r\n` followed by the region. Replayed through a minimal terminal model, the log row reads `logE-LINE-ONE-IS-LONG`. **[run]** This is [#1146](https://github.com/spectreconsole/spectre.console/issues/1146), open since 2023; its fix is the change that was reverted for flicker.
  - It does not happen when the region's first line is blank and the log line is narrower than the terminal. `Progress` puts a blank line at the top by default, which is why short log lines look right with it. `ExcludeVerticalPadding()` (new in 0.56) removes that blank line. **[run] [source]**
  - It does happen for a log line wider than the terminal even with the blank line: the wrapped part lands on a region row. **[run]**
- **Output vanishes when the region fills the terminal.** If the region is as tall as the terminal, which is always the case for a root `Layout` and for any cropped overflow, a line written above it is scrolled out of view immediately. [#1935](https://github.com/spectreconsole/spectre.console/issues/1935), open. **[issue] [source]**
- **Partial lines are overwritten.** `Write` without a newline, followed later by the rest of the line, loses the first part. [#583](https://github.com/spectreconsole/spectre.console/issues/583), open since 2021. **[issue]**
- **A target without a trailing line break misplaces output.** [#1107](https://github.com/spectreconsole/spectre.console/issues/1107) and [#2076](https://github.com/spectreconsole/spectre.console/issues/2076), both closed with the workaround of ending the target with a newline. **[issue]**
- **From another thread, with `LiveDisplay`: deadlock**, as above.

**Bypassing Spectre: not supported.** `System.Console.WriteLine`, a logging provider that writes to the console directly, or a child process sharing the terminal all write at the cursor, which sits on the last line of the region. Spectre does not see the write and its next cursor move is off by however many lines were added. **[source]**, on-screen effect **[unverified]**

The maintainers' own suggestion for extra information during a live display is to draw it as part of the live content rather than writing lines: "I'd almost be inclined to suggest using the `RenderHook` ... as the mechanism for including extra info while a progress is running rather than write line" ([#1466](https://github.com/spectreconsole/spectre.console/issues/1466)). **[issue]**

## Non-interactive or redirected output, and terminal resize

### Detection

Two separate capabilities on `console.Profile.Capabilities` matter. **[source]**

- **`Ansi`**: false when the console writes to stdout and stdout is redirected (likewise stderr). Otherwise, on Windows it tries to enable virtual-terminal processing on the console handle; elsewhere it matches `TERM` against a list of known terminals. ([`AnsiDetector`](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console.Ansi/AnsiDetector.cs#L46-L59))
- **`Interactive`**: true only when **none** of stdin, stdout and stderr is redirected. ([`InteractionDetector`](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console/InteractionDetector.cs#L22-L29)) So `app 2> errors.log` in a real terminal, or a run with stdin closed, is not interactive even though stdout is a terminal.
- **CI detection** then overrides: fourteen CI systems are recognised by environment variable and set `Interactive = false`. GitHub Actions and Azure Pipelines also force `Ansi = true`. ([docs: capabilities reference](https://spectreconsole.net/console/reference/capabilities-reference/), [GitHub enricher](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console/Enrichment/CI/GitHubEnricher.cs#L17-L22); the docs table omits the Azure `Ansi` override that the [source](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console/Enrichment/CI/AzurePipelinesEnricher.cs#L15) has.)
- Both can be forced through `AnsiConsoleSettings` (`Ansi`, `Interactive`) when creating a console. **[source] [docs]**

Both redirect rules are recent: they arrived in 0.55 ([#2042](https://github.com/spectreconsole/spectre.console/pull/2042), [#2047](https://github.com/spectreconsole/spectre.console/pull/2047)). Before that, redirected output could still receive escape sequences.

### What each runner does

- **`Progress`** checks `Interactive && Ansi`. If either is false it uses a fallback that prints plain `name: NN%` lines when a task crosses 25, 50, 75, 95, 96, 97, 98, 99 or 100 percent, and reprints every task after 30 seconds of silence. **[source] [run]** ([`CreateRenderer`](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console/Live/Progress/Progress.cs#L162-L176), [fallback](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console/Live/Progress/Renderers/FallbackProgressRenderer.cs)) The fallback cannot be customised: the property that would replace it is internal, and the maintainer has declined new API for it before 1.0, advising instead: "check `AnsiConsole.Profile.Capabilities.Interactive` and, when it's `false`, write your own progress lines" ([#1292](https://github.com/spectreconsole/spectre.console/issues/1292), 2026-09-28). **[issue]**
- **`LiveDisplay` checks nothing.** **[source]** What happens then depends on the path:
  - **Stdout redirected, Windows: it throws.** `AnsiConsole.Live(...).Start(...)` with stdout redirected to a file threw `System.IO.IOException: The handle is invalid` from `Console.CursorVisible`, reached through `LiveDisplayRenderer.Started`. `Progress` in the same process ran fine. In that run stdin and stderr were redirected as well; the failing call is on the stdout handle, and [#1393](https://github.com/spectreconsole/spectre.console/issues/1393) reports the same exception for a plain `app.exe > out.txt`. **[run] [issue]** That issue has been open since 2023; [#2242](https://github.com/spectreconsole/spectre.console/issues/2242) (open, 2026-11-04) is the same failure in the library's own test suite.
  - **No ANSI, no throw: every refresh appends a full frame.** Without ANSI the cursor codes are dropped, so nothing is overwritten. With `TestConsole`, which drops them the same way, three updates produced three frames one after another. **[run] [source]** ([legacy backend](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console/Internal/Backends/Legacy/LegacyConsoleBackend.cs#L35-L38)) Whether this, rather than an exception, is what happens with redirected stdout on Linux and macOS is **[unverified]**.
  - **GitHub Actions and Azure Pipelines: cursor codes go into the log**, because `Ansi` is forced on there and `LiveDisplay` does not look at `Interactive`. **[source]**, not run **[unverified]**

The practical rule: start a `LiveDisplay` only when `Capabilities.Interactive && Capabilities.Ansi` are both true, and otherwise use a separate plain-text reporter that never touches the live API.

### Size and resize

- **Size is read on every render**, from `Console.BufferWidth` and `Console.WindowHeight`, unless `Profile.Width` or `Profile.Height` was set explicitly. When the query fails or returns 0 the defaults are 80 and 24. **[source]** ([`ConsoleHelper`](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console/Internal/ConsoleHelper.cs), [`Profile`](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console/Profile.cs#L75))
- **There is no resize event.** The library does not listen for one; the new size is picked up by the next refresh, whenever that is. **[source]**
- **Shrinking below the current region clears the screen and the scrollback.** When the terminal becomes smaller than the last frame, the next frame starts with `ESC[2J ESC[3J ESC[H`. The operator's earlier terminal history is erased. Added by [#1840](https://github.com/spectreconsole/spectre.console/pull/1840) in 2025. **[run] [source]** ([lines 49-60](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console/Live/LiveRenderable.cs#L49-L60))
- **Growing does nothing special**: the next frame is drawn in place at the new size. **[run]**
- What the terminal shows between a resize and the next refresh, when it reflows lines the library believed were one row each, is **[unverified]**. A commenter on [#703](https://github.com/spectreconsole/spectre.console/issues/703) describes updating during a resize as undefined behaviour in most terminals.
- A negative height reported by the runtime on Unix with redirected output is not guarded in 0.57.2. A fix is on `main` ([`38d66e5`](https://github.com/spectreconsole/spectre.console/commit/38d66e5582273f9bc0c6c2f0a9eb079fc904ac0e)) and unreleased. It matters only if a live display is started on redirected output. **[source]**

## Ctrl+C and restoring the cursor and terminal state

- **What a live display changes.** It hides the cursor at start (`ESC[?25l`) and moves the cursor. It does not use the alternate screen, raw input mode, or mouse reporting. On Windows, capability detection turns on virtual-terminal processing for the console handle and does not turn it back off. **[source]** ([`Started`](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console/Live/LiveDisplayRenderer.cs#L13-L16), [console mode](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console.Ansi/AnsiDetector.cs#L161))
- **What restores it.** `Completed` runs in a `finally` around the callback and shows the cursor (`ESC[?25h`). An exception thrown inside the callback still restores the cursor and then propagates. **[run] [source]** ([`finally`](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console/Live/LiveDisplay.cs#L109-L112))
- **Spectre.Console does not handle Ctrl+C.** A search of the `Spectre.Console` and `Spectre.Console.Ansi` sources at the tag finds no `CancelKeyPress`, no `PosixSignalRegistration`, and no process-exit hook. **[source]**
- **So an unhandled Ctrl+C is expected to leave the cursor hidden.** .NET's default is that Ctrl+C terminates the process: "By default, the Cancel property is `false`, which causes program execution to terminate when the event handler exits" ([`Console.CancelKeyPress`](https://learn.microsoft.com/en-us/dotnet/api/system.console.cancelkeypress?view=net-10.0)). If the process ends that way the callback never returns and `Completed` never runs. This could not be tested headless and is **[unverified]**.
- **Graceful stop is therefore the application's job**: handle `Console.CancelKeyPress`, set `Cancel = true`, signal cancellation, and let the live callback return normally so the `finally` runs. The handler "is executed on a thread pool thread" (same page), so it must only signal, not write to the console, given the deadlock above.
- **The same class of bug exists inside the library**: [#2168](https://github.com/spectreconsole/spectre.console/issues/2168) (open, reported against 0.57.2) is a cursor left hidden after a prompt is cancelled. **[issue]**
- **Alternate screen.** `AnsiConsole.AlternateScreen(Action)` exists and is synchronous only; an async version is an open pull request ([#2220](https://github.com/spectreconsole/spectre.console/pull/2220)). It was not explored further. **[source]**

## Testing support (`Spectre.Console.Testing`)

`Spectre.Console.Testing` `0.57.2` is on NuGet with the same target frameworks as the main package. It is not yet in this repo's `Directory.Packages.props`.

- **`TestConsole`** is an `IAnsiConsole` that writes to a string. Defaults: 80x24, `Ansi = true`, `Interactive = false`, Unicode on, true colour, CI detection off. `Output` is the full text and `Lines` splits it. Fluent setters: `Width`, `Height`, `Size`, `Interactive()`, `SupportsAnsi`, `SupportsUnicode`, `Colors`, `EmitAnsiSequences()`. **[source] [run]** ([`TestConsole`](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console.Testing/TestConsole.cs#L49-L76), [docs](https://spectreconsole.net/console/how-to/testing-console-output/))
- **It is not a terminal emulator.** By default it drops cursor codes, so a live display's `Output` is every frame concatenated, not the final screen. With `EmitAnsiSequences()` the raw escape codes are included instead. Neither gives "what is on screen now". **[run]**
- **A static renderable is easy to test.** `console.Write(renderable)` then assert on `Lines`. That favours building the frame as a pure function of state and testing that function, leaving `LiveDisplay` as a thin shell.
- **`Progress` under `TestConsole` uses the fallback renderer** unless `.Interactive()` is called, because the default is non-interactive. **[source]**
- **Time.** `Progress` takes a `TimeProvider` (since 0.55), which makes elapsed and remaining time deterministic. Its auto-refresh thread still waits in real time, so tests should use `AutoRefresh(false)` and call `Refresh()` themselves. `LiveDisplay` has no time dependency. **[source]** ([constructor](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console/Live/Progress/Progress.cs#L58))
- **Three things `TestConsole` will not catch**, because it swaps in no-op parts ([lines 54 and 65](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console.Testing/TestConsole.cs#L54-L65)):
  - Two live runners at once: its exclusivity mode never throws. **[run]**
  - The redirected-output `IOException`: its cursor is a no-op. **[source]**
  - The deadlock, unless `EmitAnsiSequences()` is on: only then do writes go through the locking console. **[source]**
- **The static `AnsiConsole.Console` is process-wide.** Tests that run in parallel need an injected `IAnsiConsole` per test rather than the static. **[source]**

## Version, .NET 10 compatibility, and announced breaking changes

- **Pinned version is current.** `Directory.Packages.props` pins `0.57.2`, released 2026-07-02, which is the latest release. NuGet has only `0.57.3-alpha.0.x` previews after it. `main` has about a dozen non-dependency commits since. Two touch this area: a validation of `ProgressTask`'s maximum value and the console-size fallback noted above. Neither changes any behaviour described here. **[source]** ([releases](https://github.com/spectreconsole/spectre.console/releases))
- **.NET 10 is a first-class target.** The package ships `net10.0`, `net9.0`, `net8.0` and `netstandard2.0` assemblies and is marked AOT-compatible on the modern targets. All experiments here ran on .NET 10.0.15. **[source] [run]** ([csproj](https://github.com/spectreconsole/spectre.console/blob/0.57.2/src/Spectre.Console/Spectre.Console.csproj#L4-L7))
- **New transitive dependency.** Since 0.55 the package depends on `Spectre.Console.Ansi` at the same version. **[source]**
- **Still pre-1.0, and it has broken things recently.** 0.55 (2026-04-03) made `Style` a struct, moved links to a new `Link` type, removed obsolete members, and changed behaviour for redirected output. 0.54 moved `Spectre.Console.Cli` to its own repository. **[docs]** ([0.55 release notes](https://spectreconsole.net/blog/2025-11-13-spectre-console-0.55-released/))
- **Announced direction: freeze, then 1.0.** The [0.56 notes](https://spectreconsole.net/blog/2026-06-06-spectre-console-0.56-released/) say the library "is in stabilization mode i.e. fixing bugs and hardening things". The maintainer wrote on 2026-09-28 that "we're closing in on a 1.0 release, we're holding off on new features and API until after that" ([#1292](https://github.com/spectreconsole/spectre.console/issues/1292)). No date is given. **[docs] [issue]**
- **No announced breaking change to the live APIs was found.** The only deprecation in the area is `Capabilities.Legacy`, marked "will be removed in a future version". **[source]**
- **The limits above are unlikely to move soon.** A feature freeze means no viewport, no customisable fallback and no partial layout updates before 1.0; several of the defects cited have been open for years.
- **`Spectre.Tui`** is a separate, newer library from the same organisation with its own render loop for full-screen applications. Its README says it "is currently under construction and may change at any time". It is not part of Spectre.Console. **[docs]** ([repository](https://github.com/spectreconsole/spectre.tui))

## What this means for sketching the layout

This section is interpretation, not finding.

- **Design to a line budget.** The display gets one terminal-height of lines and no scrolling. The sketch has to say which few dozen of a few thousand tasks earn a line: for example totals, what is running, what failed, and collapsed groups for the rest.
- **Select before rendering.** Build a small renderable from a snapshot of state on each tick. Do not hand Spectre the full tree and rely on cropping: it is slow, shows the wrong end by default, and dumps everything at exit.
- **One thread owns the console.** A single render loop on a timer takes a snapshot, builds the frame, and calls `UpdateTarget`. Task threads never touch widgets or the console. This addresses the deadlock, the mutation races and the need for a timer together.
- **Decide where log lines go.** Either they are part of the frame (a few most-recent lines in a region of the layout), or they are written above a live region that is deliberately shorter than the terminal and starts with a blank line. A full-height `Layout` leaves nowhere for lines above it.
- **`LiveDisplay` or `Progress`.** `Progress` gives bars, spinners, a fallback and safe concurrent writes, but only a flat list with fixed overflow. `LiveDisplay` gives any composition, but no bars, no fallback and the hazards above. A nested view points to `LiveDisplay` with text-drawn bars.
- **The fallback is a separate component**, chosen up front from `Interactive && Ansi`, not a mode of the live display.
- **Plan the last frame.** Either `AutoClear(true)` followed by a printed summary, or a final compact frame that fits.

## Not verified

- **Anything visual.** Flicker, tearing, the look of stray characters, behaviour during a resize, and differences between Windows Terminal, the classic Windows console, and Linux or macOS terminals. Nothing was run in a real terminal.
- **Ctrl+C leaving the cursor hidden.** Derived from the source and the .NET documentation; a Ctrl+C could not be sent in a headless run.
- **`LiveDisplay` with redirected output on Linux and macOS.** The exception was reproduced on Windows only.
- **Cursor codes in GitHub Actions and Azure Pipelines logs.** From the source only.
- **Direct `System.Console` or logger output corrupting the region.** Reasoned from the source; not run.
- **The suggested patterns.** A single render thread, a blank first line for log output, and handling `CancelKeyPress` follow from the findings but were not built or tested.
- **`Table` under concurrent mutation**, and `Status` beyond reading its source (it is a one-task `Progress`).
- **Wide and combining characters.** Width calculation issues exist in the tracker and were not examined.
- **When 1.0 ships and what it changes.** Only the maintainer statements quoted above are known.
- **The timings** are from one machine with output discarded. Treat them as ratios.

## Experiments

Run against `Spectre.Console` and `Spectre.Console.Testing` `0.57.2` from NuGet, .NET 10.0.15, Windows 10, Release build. Consoles were created with `AnsiConsole.Create` over a `StringWriter` or `TextWriter.Null`, with `Ansi` and `Interactive` forced on and the size set explicitly, except where noted. The essential code for the four most important results follows.

**Deadlock (limit 1).**

```csharp
var console = AnsiConsole.Create(new AnsiConsoleSettings
{
    Ansi = AnsiSupport.Yes,
    Interactive = InteractionSupport.Yes,
    Out = new AnsiConsoleOutput(TextWriter.Null),
});

console.Live(new Markup("live\n")).Start(ctx =>
{
    new Thread(() => { while (true) { console.WriteLine("plain"); Thread.Sleep(5); } })
        { IsBackground = true }.Start();

    while (true) { ctx.UpdateTarget(new Markup("live\n")); Thread.Sleep(100); }
});
// Both threads stop making progress, usually within seconds.
```

**Redirected output (limit 2).** Run with stdout redirected to a file.

```csharp
AnsiConsole.Progress().Start(ctx => ctx.AddTask("alpha").Increment(100)); // fine
AnsiConsole.Live(new Markup("frame\n")).Start(ctx => ctx.Refresh());
// System.IO.IOException: The handle is invalid.
//   at System.ConsolePal.set_CursorVisible(Boolean value)
//   at Spectre.Console.LegacyConsoleCursor.Show(Boolean show)
//   at Spectre.Console.LiveDisplayRenderer.Started()
```

**Cost per refresh (limit 4).**

```csharp
var tree = new Tree("run");
for (var g = 0; g < 10; g++)
{
    var group = tree.AddNode($"group-{g}");
    for (var i = 0; i < 10; i++)
    {
        var item = group.AddNode($"item-{g}-{i}");
        for (var t = 0; t < 29; t++) item.AddNode($"[green]task-{g}-{i}-{t}[/] running");
    }
}

console.Live(tree).Overflow(VerticalOverflow.Crop).Start(ctx =>
{
    var sw = Stopwatch.StartNew();
    for (var n = 0; n < 30; n++) ctx.Refresh();
    Console.Error.WriteLine(sw.Elapsed.TotalMilliseconds / 30); // about 60 ms for 3,011 nodes
});
```

**Stray characters (limit 8).** Width 40.

```csharp
var writer = new StringWriter();
// console created over `writer` as above
console.Live(new Text("LIVE-LINE-ONE-IS-LONG\nlive-line-two\n")).Start(ctx =>
{
    ctx.Refresh();
    console.WriteLine("log");
});
// writer contains: ... \r ESC[2A log \r\n LIVE-LINE-ONE-IS-LONG \r\n live-line-two ...
// "log" lands on the row that held LIVE-LINE-ONE-IS-LONG, and nothing erases the rest of it.
```

The remaining results (overflow table, region not shrinking, layout height, exclusivity exception, exception path, resize bytes, tree mutation race, `TestConsole` behaviour, fallback output) came from the same program using the public API in the same way.

## Sources

- Spectre.Console source at tag `0.57.2`: <https://github.com/spectreconsole/spectre.console/tree/0.57.2>
- Spectre.Console issues and pull requests: <https://github.com/spectreconsole/spectre.console/issues>
- Official docs: <https://spectreconsole.net/console/live/live-display/>, <https://spectreconsole.net/console/live/progress/>, <https://spectreconsole.net/console/explanation/understanding-rendering-model/>, <https://spectreconsole.net/console/reference/capabilities-reference/>, <https://spectreconsole.net/console/how-to/testing-console-output/>. Read from the docs source repository at commit [`70844af`](https://github.com/spectreconsole/website/tree/70844af98eaac05620b2944a17ee99ffae46972e).
- Release notes: <https://github.com/spectreconsole/spectre.console/releases> and <https://spectreconsole.net/blog/>
- NuGet version list: <https://api.nuget.org/v3-flatcontainer/spectre.console/index.json>
- .NET API reference, `Console.CancelKeyPress`: <https://learn.microsoft.com/en-us/dotnet/api/system.console.cancelkeypress?view=net-10.0>
