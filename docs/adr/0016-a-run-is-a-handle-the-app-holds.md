# A run is a handle the app holds

Starting a run returns the run. The app holds it and asks it things: to drain, to stop, for its current snapshot, and for its result when it has ended. Running a graph to its end in one call stays as a convenience over the same handle.

Before this, a run could only be awaited. A drain was requested on a `DrainSignal` the app made beforehand and passed in (ADR 0014), and the only way to read a run's snapshot while it ran was to write a reporter and keep the source its first event carried. The signal was a second object whose one job was to carry one request to a run that might not exist yet, or might have ended, and the run had to register on it and let go of it at the right moments. With one loop owning the run (ADR 0015), each of these is a post to the run's inbox, and the run is the natural thing to post through.

A reporter is still handed a read-only source of snapshots and never the run. A reporter that held the run could drain or stop it, and reporters watch a run and cannot change it (ADR 0010).

## Considered options

- **A signal object passed to the run**, as before. Rejected for the reasons above.
- **Handing reporters the run.** Rejected: it breaks ADR 0010.
- **A handle only, with no call that runs a graph to its end.** Rejected: most apps want one line, and a test of one graph wants no more.

## Consequences

- A stop can be asked for in two ways that are one request: cancelling the token given when the run starts, as a .NET caller expects (ADR 0014), or asking the run. Whichever comes first applies the stop, and the other then changes nothing.
- A drain or a stop asked for after the run has ended changes nothing.
- A drain asked for before the run exists has nowhere to go. `ConsoleStop` therefore hooks a run that exists. Before that, Ctrl+C ends the process as the operating system would, and nothing has started that a drain could let finish.
- Where the run is in its life is read from its snapshot, which already says whether a drain or a stop has been applied and whether the run has ended. The run offers no second answer to keep in step with it.
- A run holds no resource and is not disposed.
