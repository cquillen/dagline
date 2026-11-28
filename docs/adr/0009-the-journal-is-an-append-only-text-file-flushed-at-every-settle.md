# The journal is an append-only text file, flushed at every settle

A journal is one UTF-8 text file with one JSON entry per line. Entries are only ever appended, and every run of the journal writes to the same file in order. The first line carries a format version, and an unknown version is an error before any run exists.

The promise is this: the entry for a settled task is flushed to disk before any of its dependents starts and before the run ends, and a checkpoint is flushed before its save returns (ADR 0006). So after a killed process, an operating system crash or a power cut, no recorded success and no saved checkpoint is lost. A task recorded as succeeded is an earlier success in the next run, and runs again only if that run sets it aside (ADR 0008). The promise holds as far as the storage device honours a flush.

A run may choose a weaker promise for its journal: each entry is handed to the operating system at the same moments, and is not flushed to the disk. No recorded success and no saved checkpoint is then lost to a killed process, though one may be lost to an operating system crash or a power cut. The choice is made for each run, on its journal choice, as a limit's size is (ADR 0005), and flushing to the disk is the default.

Two more flushes complete the promise. A new journal's place in its directory is made durable before its first run starts any task, which on Linux takes a flush of the directory as well as of the file. And a run flushes the set-asides it makes before it starts any task, so that a success once set aside stays aside even when the process dies before anything settles.

Entries written together share one flush. A failure that blocks a thousand dependents costs one flush, not a thousand. The entry that says a task started is written but not flushed by itself, so a crash can lose it, which costs only history.

The process can die in the middle of a write. The final line of the file may therefore be cut short. It is dropped when the journal is opened and trimmed away before anything is appended.

What is recorded:

- **For the journal:** the format version, its id, its creation time and its description.
- **For each run:** its start time, the operating system user and the machine name, the cap and the limit sizes, the count of journal paths not found in the graph and how many of those have not succeeded (ADR 0008), each earlier success it set aside (ADR 0008), and its end time and run outcome. A run with no end entry is either still going, and then holds the journal (ADR 0007), or one that ended without recording it: its process died, or a failed write ended it.
- **For each task:** its start time; its settle time with the full outcome (status, message, detail lines, warnings and, for an unexpected failure, the error's type, message and stack trace, all within the outcome's volume limit); and each save or clearing of its checkpoint, with the time.

Progress, waiting, pending and ready are never recorded, and neither is the status of a group, which is derived. Every time is UTC.

When a write fails during a run, because the disk is full or the file is gone, Dagline treats it as a stop: nothing new starts, running tasks are signalled, and the run waits the grace period (ADR 0004). The result of a write is known before anything that follows it is decided (ADR 0015), so a settle whose write fails is still published and its dependents still become ready, but none of them starts. The same holds for the entry that says a task started: a body starts only once that entry is written, and when the write fails the task stays ready. The run then ends with the run outcome unrecorded, which takes precedence over the other three. A run that can no longer record must not keep starting work, and what it did is not reliably complete, incomplete or stopped. The one exception is the entry for the run's end: when that write alone fails, every settle is on disk, and the run keeps the outcome it reached, with the error beside it in its result. A checkpoint save that fails throws to the task that called it, and the run throws nothing. Such a run usually cannot record its end either, so the listing shows it as died, the same as a killed process (ADR 0007).

## Considered options

- **Replacing the whole file on every change.** Rejected because each settle would rewrite everything, because the replace fails on Windows while another program has the file open, and because a file that is replaced cannot also be held as the lock.
- **An embedded database.** Rejected because it passes native packages on to every consumer and makes the record a binary file, for a few thousand entries that are only ever appended and read in order.
- **Writing without flushing, or flushing only when the run ends, as the only behaviour.** Rejected because a crash of the machine could then lose a success, and the task would run again after its checkpoint had been discarded. Writing without flushing to the disk is offered as a choice, as above.
- **A stable, documented format.** Deferred. The format is documented as readable, and it may change between early releases.

## Consequences

- Each settle costs a flush, measured at roughly 1 to 5 ms on one SSD: seconds over a run of a few thousand tasks.
- The journal is plain text that anyone with access to the directory can read. Outcomes and checkpoints must never hold secrets. A checkpoint that was cleared or discarded is no longer handed back, but its text remains in the earlier lines of the file.
- The file only grows. A few thousand tasks over a handful of runs come to a few megabytes, and the first release does not compact it.
- The lock is a separate file beside the journal, so that the journal stays readable while a run holds it.
- Reading a journal means reading it from the top and folding the entries for each path. The status comes from the latest settle or set-aside. The checkpoint comes from the latest save and stands until it is cleared or the task succeeds, whatever else is recorded for the path in between.
- A journal written by a newer format than the running Dagline understands cannot be continued or summarised.
