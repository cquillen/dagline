# API reference

Generated from the XML doc comments in the source.

- **Dagline**: the core engine. Building a graph (`GraphBuilder`), running it (`Runner`, `RunOptions`, `StopSignal`), what a task body is given (`TaskContext`), journals (`Journal`, `Journals`), and what a reporter receives (`IReporter`, the events, `Snapshot`), with the plain-text reporter (`PlainTextReporter`). It references no UI or console library.
- **Dagline.Spectre**: the terminal display, built on Spectre.Console. `SpectreReporter.Auto` gives the display on a terminal and the plain-text reporter elsewhere, `SpectreOptions` and `DisplayMode` say what an app may choose about it, and `ConsoleStop` turns Ctrl+C into a stop. See [The terminal display](../articles/terminal-ui.md).
- **Dagline.Testing**: for test projects only. `TaskHarness` runs one task body as a real run and returns its `TaskOutcome`, can run it again on its own journal, and can stop it at its wait. `InstantClock` is a clock on which every delay ends at once and time jumps forward by the delay, with a guard that throws `ClockGuardException` once too much fake time has passed. It references `Dagline` alone, and no test framework. See [Testing your tasks](../articles/testing-your-tasks.md).
