# Reporters watch a run and cannot change it

A reporter presents a run while it happens. The terminal display is one and the plain-text reporter is another. Dagline gives a reporter two things: events, each a record of one thing that happened, and a snapshot of the whole run that it can read at any moment. Nothing a reporter does, or fails to do, changes a task, the run outcome or the journal.

The reason is that a run takes hours and has external work in flight. A display that is slow or broken must not be able to slow the run, end it or leave a gap in its record.

Three rules follow, and a reader could reasonably expect each to be otherwise:

1. **The run never waits for a reporter, and drops one that throws.** Reporters are given when the run starts and none joins later. Each has its own queue and receives events one at a time, in the order they happened, which is the same order for every reporter. A slow reporter falls behind and nothing else happens. A reporter that throws receives no more events, the run carries on, and the error is returned to the app with the run's result.
2. **The journal is not a reporter**, although most of what it records is also published as an event. Dagline writes the journal itself: where a change is one the journal records, the entry is written as part of making the change, and where that change is also an event, the event is published afterwards. The two sets differ (ADR 0009): a checkpoint save is recorded and is never an event, and a task becoming ready is an event and is never recorded. In a run that has a journal, a settle is therefore on disk before any reporter hears of it, for as long as the journal can be written. A reporter may fall behind or be dropped. The journal can do neither: a write that fails stops the run (ADR 0009), and from then on events are published for changes that are not recorded.
3. **Dagline keeps the snapshot.** Reporters do not build it from events. A group's status and its count of tasks per status follow rules (ADR 0003) that every reporter would otherwise have to implement again, and a display that redraws on a timer wants the whole run at one instant, not a list of changes.

The run waits for reporters once, at its end, until each one that has not been dropped has handled every event, so that the last lines are always printed. A stop during that wait ends it, and a reporter still busy is dropped like one that threw. A run that was stopped waits in the same way, because its last lines say what was cancelled and what was never reached. A stop made before the wait began does not end it. One more does.

## Considered options

- **Events only, with each reporter building its own picture.** Rejected because of the third rule: two reporters could disagree about the status of a group.
- **A snapshot only, read on a timer.** Rejected because a reporter that prints a line per change would have to compare snapshots, and would lose both the order of changes and any change that came and went between two reads.
- **The journal as one more reporter.** Rejected because a settle must be on disk before a dependent starts (ADR 0009), and no reporter is ever waited for.
- **Delivering each event on the thread that made the change.** Rejected because a slow terminal would slow every task, and a reporter that never returned would hang the run.
- **Skipping the wait at the end when the run was stopped.** Rejected because every stopped run would then lose its last lines, to guard against a reporter that never returns, which one more stop already deals with.
- **Stopping the run when a reporter throws.** Rejected because the work is unaffected and the journal still records it. The operator loses a display, not a run.
- **Catching the error and continuing to deliver to that reporter.** Rejected because a reporter that failed halfway through an event may be showing something wrong, and would most likely fail again on the next one.

## Consequences

- An event always means something happened in this run. Earlier successes are in the snapshot from the start and no event is published for them, so a continuing run with nothing left to do publishes two events: the run started and the run ended.
- An event carries everything needed to print a line about it. The snapshot may be ahead of the events a reporter has received so far, so a reporter should not read the snapshot to explain an event.
- Progress is never an event, and neither is the status of a group before it settles, which can move backwards. A reporter that shows either reads the snapshot on its own timer. The number of events in a run is therefore in proportion to its tasks and their kept warnings, and that is all a reporter that falls behind can cost in memory.
- A reporter's error is on the result the run returns and not on the event that says the run ended, because a reporter can still fail while handling that event.
- After a journal write has failed, events still describe what happens in the process, although none of it is recorded: a task that settles during the grace period that follows is reported to reporters and is not on disk. The event that says the run ended then carries the error in place of a run outcome.
- A reporter that needs to act on a run, as a retry key or a pause would, cannot be built as a reporter.
