# A journal spans runs, and the consumer chooses which one to continue

A run is one execution of a graph by one process. Work that takes hours is interrupted, and a later run takes it up again. What lasts between those runs is a journal: the durable record of a first run and every run that continues it. It holds the earlier successes and the checkpoints, and nothing else connects one run to the next.

A run starts in one of three ways: on a new journal, continuing a given journal, or with no journal at all. Dagline never chooses between them and never chooses a journal, not even when exactly one is unfinished. The consumer's app decides, by a prompt, by command-line flags or by a rule of its own.

An earlier design said that resuming is "the same command run again". That stops being a rule as soon as two unfinished journals exist, which is ordinary: several operators run different parts of the same work side by side, and an operator may leave a journal behind and start over. Whichever journal Dagline picked, a wrong pick either repeats hours of finished work or continues work the operator did not mean. So the choice is policy, and policy stays with the app.

What Dagline provides:

1. **Start a run on a new journal.** A new journal has no earlier successes and no checkpoints.
2. **Start a run that continues a given journal.**
3. **Start a run with no journal.** This is an explicit choice, never a fallback. Nothing is recorded, the run cannot be continued, and a saved checkpoint lives in memory until the run ends.
4. **List the journals in a directory**, each with enough summary to choose from: its id, creation time and description; how many runs it has had; for the latest run its start and end time, user, machine and how it ended (complete, incomplete, stopped, in use, or died when it has no end and no live holder); task counts by last recorded status; and whether the journal is finished.
5. **Delete a journal by id.** This is refused while the journal is in use.

How a journal is identified and held:

- **The consumer names a directory, and each journal is one file in it.** There is no default directory, because a default relative to the working directory would make continuing depend on where the command was launched.
- **Dagline generates the id when it creates the journal:** the UTC creation time and a short random suffix, as in `20270105-091233-k4f`. It sorts by age, can be typed, and is also the file name.
- **A description is optional free text given at creation.** It never changes, and it is how operators working in the same directory tell their journals apart.
- **A journal is finished when its latest run was complete, and unfinished otherwise.** There is no marker for a journal that was given up on. Giving up on one is deleting it.
- **A run holds its journal exclusively for its whole life.** A second process that tries to continue the same journal gets an error before any run exists, and the listing shows the journal as in use.

## Considered options

- **Continuing the latest unfinished journal automatically.** Rejected for the reason above: with two unfinished journals it guesses, and a wrong guess is expensive.
- **A name chosen by the consumer as the identity.** Rejected because two operators would collide on it, and starting afresh would mean deleting or renaming first.
- **Naming the work instead of the record**, as a campaign or an effort. Rejected because between runs nothing exists except the record, so the record is the thing to name.
- **Calling the whole thing the run and each process an attempt.** Rejected because a graph is fixed for one process only (ADR 0001), and every earlier decision already uses run in that sense.
- **A graph name recorded in the journal and checked on continue**, to catch a journal continued with a different graph. Deferred. The directory is the separation, and a check for zero matching paths would misfire when a list that shapes the graph changes entirely.
- **Journals that expire by age.** Rejected because how long a record is worth keeping is the app's policy. The listing gives it the times it needs to prune.

## Consequences

- An app with more than one kind of graph gives each its own directory. Continuing a journal with an unrelated graph is not an error: every task runs as new and the two records share one file. The counts of unmatched paths (ADR 0008) are the visible symptom.
- Continuing a finished journal is allowed and has no special case. With an unchanged graph every task is an earlier success and the run is complete at once. If the graph gained paths, those run, and the journal is finished or not by the new run's outcome.
- The listing needs no graph. It reports what the journal says.
- There is no API for the per-task detail of a past journal in the first release. The file is readable text (ADR 0009).
- Dagline never deletes a journal by itself.
- The lock is enforced by the operating system on Windows and is advisory on Linux. That is enough, because only Dagline processes need to respect it.
- A copied or moved journal is not guarded against. Continuing is meant for the same machine.
