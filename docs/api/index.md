# API reference

Generated from the XML doc comments in the source.

- **Dagline**: the core engine. Building a graph (`GraphBuilder`), running it (`Runner`, `RunOptions`, `StopSignal`), what a task body is given (`TaskContext`), journals (`Journal`, `Journals`), and what a reporter receives (`IReporter`, the events, `Snapshot`), with the plain-text reporter (`PlainTextReporter`). It references no UI or console library.
- **Dagline.Spectre**: the terminal display, built on Spectre.Console. `SpectreReporter.Auto` gives the display on a terminal and the plain-text reporter elsewhere, `SpectreOptions` and `DisplayMode` say what an app may choose about it, and `ConsoleStop` turns Ctrl+C into a stop. See [The terminal display](../articles/terminal-ui.md).
