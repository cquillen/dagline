# Compatibility

Dagline is before 1.0. It runs real work and is tested on every change, but its public API may still change between minor releases. This page says what you may rely on from one release to the next, and what you must not.

## The public API before 1.0

- **A patch release never breaks you.** Upgrading from 0.1.0 to any 0.1.x needs no change to your code and no rebuild: every public type and member keeps its name, its shape and its meaning.
- **A minor release may.** A name or a shape may change from 0.1 to 0.2, and from each minor release to the next. Every such change is listed under its release in the [changelog](https://github.com/cquillen/dagline/blob/main/CHANGELOG.md), with what to write instead.

## What may change in any release
- **The plain-text output.** What <xref:Dagline.PlainTextReporter> prints is for people to read: its wording, its layout and the lines it chooses to print may change in any release, 1.0 and later included. Do not parse it. A program that needs to know what happened in a run reads the events and the snapshot through a [reporter](reporters.md) of its own, or the <xref:Dagline.RunResult> that `RunAsync` returns.
- **The display.** The same holds for what <xref:Dagline.Spectre.SpectreReporter> draws on the terminal, and for the summary it leaves.

## Native AOT and trimming

`Dagline` and `Dagline.Testing` are marked compatible with native AOT and trimming. Every build of them runs the .NET trim, AOT and single-file analyzers, and they report nothing, so an app that publishes with `PublishAot` or `PublishTrimmed` gets no warning from either package. Dagline reads and writes journals without reflection.

`Dagline.Spectre` is not marked: whether it trims cleanly is up to Spectre.Console, which it draws with. Check Spectre.Console's own guidance before you publish an app that uses the display with native AOT.

## What is stable

### Exit codes

<xref:Dagline.RunOutcomeExtensions.ToExitCode*> maps a run outcome to an exit code, and the mapping does not change:

| Run outcome | Exit code |
| --- | --- |
| Complete | 0 |
| Incomplete | 1 |
| Stopped | 2 |

A script that calls an app which returns `ToExitCode()` can rely on these three codes in every release. Dagline never sets the exit code of your process itself: the mapping is yours to use or not. See [Exit codes](running-and-stopping.md#exit-codes).

### Journals

The first line of every [journal](journals.md) names the format version it was written in. Two promises follow from it:

- **A later Dagline reads every journal format an earlier release wrote.** An upgrade never strands work in progress: a journal written by this release can be listed, continued and deleted by every later one, its earlier successes and its checkpoints included.
- **An older Dagline refuses a newer format.** A journal written in a format version that a release does not know is never half read: continuing it throws <xref:Dagline.JournalFormatException> before any task starts, with the version in `Version`, and listing the directory puts it in <xref:Dagline.JournalListing.Unreadable> with that exception, beside the journals it can read.

Continuing a journal with a later release may leave it in that release's format, so do not go back to an earlier release while a journal holds work you want to keep.

Every release so far writes format version 1. The tests of each release continue and list a version 1 journal, checked in exactly as 0.1.0 wrote it, so a change that would break this promise fails the build.

The promise is about what Dagline can read, not about the layout of the file. The file is readable text, so that you can look into it when something went wrong, but your code should read a journal through <xref:Dagline.Journals.ListAsync*> and continue it through <xref:Dagline.Journal.Continue*>, not by parsing its lines.
