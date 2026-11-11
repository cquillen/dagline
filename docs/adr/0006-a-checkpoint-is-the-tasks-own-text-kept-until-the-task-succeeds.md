# A checkpoint is the task's own text, kept until the task succeeds

A task that starts work in an external system must not start it a second time when a run is interrupted and its journal is continued. So a running task can save a checkpoint: one piece of text that Dagline stores and hands back when the task runs again in a later run continuing the same journal. Dagline never reads it, never shows it to a reporter, and discards it only when the task succeeds. Everything else about it is the task's to decide, because only the task knows what the external system needs.

Three properties are deliberate:

1. **It is opaque text.** The task encodes it however it likes, and it is typically the id of an external job. Saving again replaces it. It may be at most 4 KB as UTF-8, and a larger value is an error at the save call. Dagline stays out of serialisation, and the limit keeps the journal from becoming a data store.
2. **It is on disk before the save returns.** Whatever the task does next assumes the checkpoint will still be there after a crash, so a save that might be lost would defeat its purpose.
3. **It is kept until the task succeeds.** It survives a reported failure, an unexpected failure, a cancellation and a killed process. The task may replace or clear it at any time. After external work has failed, the author therefore chooses what a continuing run does: leave the checkpoint and the task re-attaches to the same external work, which suits work that was fixed and re-run in place, or clear it before reporting the failure and the task starts fresh work.

One window stays open. If the process dies after the task has started the external work and before the save, the continuing run starts it a second time. Dagline cannot close that window. The author can, where the external system allows, in one of two ways:

- **Save a self-chosen correlation id before starting the work.** The checkpoint then says only that a start may have happened, because the process can also die between the save and the start. When it runs again, the task must settle which it was, by asking the external system for that id or by starting the work in a way that is idempotent on it.
- **Look the work up by a predictable name before starting it.**

A lookup, by id or by name, closes the window only if finding nothing rules out a start the external system has already accepted. Where it cannot, the start itself has to be idempotent on that id or name.

A checkpoint proves the work started only when it was saved after the start. "Start it unless the checkpoint says it was started" is a few lines in the task and is documented as guidance, not provided as a shape.

## Considered options

- **Clearing the checkpoint when the task fails.** Rejected because it removes the option of re-attaching to external work that was fixed and re-run in place.
- **A declared kind of task with a "start" step whose result Dagline saves automatically.** Rejected because it adds a second kind of task to the graph and the scheduler, and it still cannot close the window above. It can be added later on top of the same checkpoint.
- **A typed or structured checkpoint that Dagline serialises.** Rejected because it brings serialisation and versioning into the core for a value that is usually one id.
- **A save that is buffered and written later.** Rejected for the reason given in the second property.

## Consequences

- Any task may save a checkpoint, not only one that waits on an external system.
- A checkpoint is private to the task. A task that wants the operator to see an external id puts it in its progress or its outcome.
- Each save costs a flush to disk, measured at roughly 1 to 5 ms on one SSD. A checkpoint is for milestones, not for progress.
- A stop leaves external work running. Dagline never cancels external work and never re-attaches to it: in a continuing run the task's own code re-attaches, from its checkpoint. A task that wants a stop to cancel it does so in its own code, within the grace period.
- A save made during the grace period succeeds. A save from a task that Dagline has let go of throws (ADR 0004).
- What the text means across versions of the consumer's code is the task's concern.
- Checkpoints are stored in the journal (ADR 0009). One whose path is no longer in the graph stays there untouched (ADR 0008), and a new journal has none (ADR 0007).
