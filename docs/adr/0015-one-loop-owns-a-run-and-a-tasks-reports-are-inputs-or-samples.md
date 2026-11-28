# One loop owns a run, and a task's reports are inputs or samples

A run is owned by one loop from its start until its result is returned. Everything else that has something to tell the run posts an input to the loop's inbox and returns at once: a task body that ends, a warning, a wait, a checkpoint save, a drain, a stop, the grace period running out. The loop takes one input at a time, asks the step core what it means, and carries out the effects the core returns, in the order the core gives them. Nothing outside the loop reads or changes the run's state.

Before this, the run was a lock that every thread entered. A task body's end settled the task, wrote and flushed the journal, built snapshots and started dependents on the body's own thread. A stop and a drain hopped to the thread pool so that this work stayed off the canceller's thread. The order of a step's effects, the hand-off of the run's token to the wait for reporters, and where the run was in its life were each kept right by doing things in a careful order under the lock. Every fix to a race in the engine had been to that order, and none to a scheduling decision.

Four rules follow.

1. **The inbox is the only way in.** Posting never blocks and runs no engine code, so a cancellation callback, a timer and a console handler each post from their own thread with no hop. The run's life is the control flow of the loop: read, decide, carry out, repeat, wait for reporters, return.
2. **The core decides the effects and their order.** An input gives an ordered list from a closed set: record journal entries, publish an event with its snapshot, start a task body, signal the tasks, start the grace period, answer a checkpoint save, end the run. A record is always the last effect of its list, and the result of the write is the next input. So the core decides what follows a write, the end of the run and the answer to a save included, only once it knows whether the write landed (ADR 0009). The entry that says a task started is such a write: a body starts only once it is written. A step that settles one task and starts another therefore makes two writes, the second not flushed.
3. **A report is an input or a sample.** What becomes an event or reaches the journal is an input, applied in the order the task made its calls: a warning that is kept, a wait, the message, a detail line that is kept, a reported failure, a checkpoint save. What only the latest value of matters is a sample: the task overwrites its progress, and counts its warnings and detail lines beyond those kept, in a place of its own that is read by whoever builds a snapshot.
4. **The loop's host is a seam.** The loop asks its host for five things: to run it when the inbox fills, to start a task body, to hand events to reporters, to signal a token, and to resume whoever awaits a checkpoint save or the run's result. In an app the host does each on the thread pool, so no callback of a task or a reporter runs on the loop. A test can supply one that does each on the test's own thread when the test says so, which makes a whole run a script. The loop itself awaits only its inbox: the reporters being done, and the reporter wait running out, reach it as inputs.

## Considered options

- **Keeping the lock.** Rejected: the engine then has no thread and no straight-line life of its own, every caller waits on the disk and on the app's clock, and the order of effects stays a matter of care.
- **A lock for decisions and a queue for effects.** It keeps a report visible when the call returns. Rejected: two mechanisms where one is enough, and the snapshot would show a settle before it is on disk.
- **A task per node that awaits its upstreams, with a semaphore per limit.** Rejected: it cannot start ready tasks in declaration order, take several limits at once or not at all (ADR 0005), block dependents in the step of a failure (ADR 0003), or give one order of events (ADR 0010).
- **Posting every report, progress included.** Rejected: a task may report progress millions of times, and only the latest report is ever read.
- **A task-owned record for every report that is not an event**, the message and detail lines included. Rejected: a second home for a task's outcome, where "kept is an input, counted is a sample" draws one line.

## Consequences

- A warning or a wait shows in the run's current snapshot once the loop has applied it, not when the call that made it returns. Progress shows on the next read. A task has no way to learn that a report has been applied, and needs none.
- A checkpoint save completes when its entry is on disk, and no other caller ever waits for a write.
- The snapshot handed with an event shows every running task's progress as sampled when the event is published, so it is still everything as of one instant.
- A step keeps one time: the events decided after a write carry the time of the happening that began the step. That time is read when the loop takes the input, not when the task made its call, so one reader of the clock keeps the times of events in the order of the events. A warning or a wait made under load carries a time later than its call by however long it queued.
- A step's events are published as its writes land, not all at once: an event decided before a write is published before it.
- The snapshot of an event is built as that event is published, and never for every event of a step beforehand: a step that makes twenty thousand tasks ready would otherwise hold twenty thousand snapshots.
- A token already cancelled when the run starts reaches the core with the start, so the stop is applied after the tasks become ready and before any starts.
- The inbox is not bounded. What a task keeps is bounded for each task, but waits and messages are not: a task that begins a wait or sets its message in a tight loop can post faster than the loop applies, and progress is the tool for that.
- Inputs already queued may share one flush, with no change to what is durable before what. The first version writes as each step asks.
- A test of a stop, a drain, the grace period or a limit states the order of happenings it means and is run once. Repeating a run to sample orders is no longer how a race is tested. The order of a step's effects is tested on the core directly, which has no thread, no clock and no await.
- `Dagline.Testing` offers a run that a consumer's test scripts. It is exact for a body that awaits only what the test, the test's clock or the run completes. For any other body it can only pump until a condition holds, waiting in real time.
