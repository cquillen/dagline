# Dagline

Dagline runs a graph of tasks inside a command-line app and shows the operator what is happening. This glossary fixes the words used for that graph and for the work that moves through it.

## Language

### Graph

**Graph**:
The tree of nodes together with the dependencies between them. A graph does not change once a run starts.
_Avoid_: Pipeline, workflow, plan

**Node**:
A task or a group.
_Avoid_: Item, element, vertex

**Task**:
A unit of work that runs consumer code. A task is always a leaf of the tree.
_Avoid_: Job, step, activity

**Group**:
A named container of tasks and other groups. A group never runs code itself.
_Avoid_: Stage, phase, parent task

**Path**:
The identity of a node: its segments from the root joined by `/`, as in `shard-3/tenant-a/prepare`. The same path means the same work in every run.
_Avoid_: Id, key, name

**Segment**:
One part of a path, naming a node among its siblings. The consumer chooses every segment.
_Avoid_: Slug, label

### Dependencies

**Dependency**:
A directed link from a dependent node to an upstream node. It orders work and carries no data.
_Avoid_: Edge, link, input

**Requires**:
The kind of dependency that is met when the upstream has succeeded.
_Avoid_: Needs, depends on

**After**:
The kind of dependency that is met when the upstream has settled, whatever the outcome.
_Avoid_: Finally, always

**Settled**:
Said of a task whose status is succeeded, failed, blocked or cancelled: it will not change status again in this run. A group is settled once every task beneath it is.
_Avoid_: Done, finished, complete

### Scheduling

**Limit**:
A named ceiling on how many of the tasks that hold it may be running at once.
_Avoid_: Semaphore, lock, pool, throttle

**Slot**:
One unit of a limit's size, held by one task from the moment it starts until it settles.
_Avoid_: Permit, token, lease

**Cap**:
The most tasks that may be running at once in a run, whatever limits they hold.
_Avoid_: Max parallelism, degree of parallelism, global limit

**Held**:
Said of a ready task that cannot start because a limit it holds is full or the cap is reached.
_Avoid_: Queued, throttled, starved

**Declaration order**:
The order in which nodes were added to the graph, read from the top of the tree down. It decides both what the display shows first and which ready task is first in line to start.
_Avoid_: Priority, sort order

### Status

**Status**:
Where a node is in its life during a run. A task has exactly one status at a time, and a group's status is derived from the tasks beneath it.
_Avoid_: State, phase

**Pending**:
The status of a task that has a dependency not yet met and has not been blocked.
_Avoid_: Waiting, not started, queued

**Ready**:
The status of a task whose dependencies are all met and which has not started yet.
_Avoid_: Queued, scheduled, waiting

**Running**:
The status of a task whose work has started and not ended, including while it waits on an external system.
_Avoid_: Active, in progress

**Succeeded**:
The status of a task that finished its work without a failure, in this run or as an earlier success.
_Avoid_: Done, passed, completed, skipped

**Failed**:
The status of a task whose work ended in a failure, whether the task reported a problem or broke unexpectedly.
_Avoid_: Errored, crashed, faulted

**Blocked**:
The status of a task that will not run in this run because a node it requires failed or is itself blocked.
_Avoid_: Skipped, cancelled

**Cancelled**:
The status of a task that was running when the run was stopped and either ended because of it or was left behind when the run stopped waiting for it.
_Avoid_: Stopped, aborted, interrupted

**Roll-up**:
A group's status, counts, waiting and warnings, derived from the tasks beneath it; and the run's, derived from all its tasks.
_Avoid_: Aggregate, totals, tally

### Outcomes

**Outcome**:
What a settled task leaves behind: its status, a message, detail lines and warnings.
_Avoid_: Result, output, return value

**Warning**:
A one-line note a task attaches to its own outcome. A warning never changes the task's status and never blocks a dependent.
_Avoid_: Soft failure, issue, alert

**Reported failure**:
A failure the task declared itself, such as bad data it found or external work that ended in error.
_Avoid_: Validation error, soft failure, expected failure

**Unexpected failure**:
A failure the task did not declare: its code broke.
_Avoid_: Crash, fault, exception

### Runs

**Run**:
One execution of a graph by one process.
_Avoid_: Execution, session, job

**Step**:
Everything a run does in response to one thing happening: a start, a settle, a stop, a report. Its events share one time and come in a fixed order, causes before effects.
_Avoid_: Tick, cycle, batch

**Stop**:
A request, usually from the operator, that the run start nothing new and that its running tasks end. A second stop ends the wait.
_Avoid_: Cancel, abort, kill, shut down

**Grace period**:
How long a run waits after a stop for its running tasks to end.
_Avoid_: Timeout, drain time

**Run outcome**:
How a run ended: complete, incomplete or stopped.
_Avoid_: Result, exit status

**Complete**:
The run outcome when every task has succeeded, in this run or as an earlier success.
_Avoid_: Successful, passed, done

**Incomplete**:
The run outcome when the operator did not ask the run to stop, it went as far as it could, and at least one task failed or was blocked.
_Avoid_: Failed, partial

**Stopped**:
The run outcome when the operator asked the run to stop and at least one task had not succeeded.
_Avoid_: Cancelled, aborted, interrupted

**Progress**:
What a running task reports about how far it has got: a line of text, a count, or both. Progress is live only and is never carried into a later run.
_Avoid_: Sub-step, percent

**Waiting**:
Said of a running task that has told Dagline it is idle until an external system changes. Waiting is not a status: the task is running, and only a reporter treats it differently.
_Avoid_: Polling, sleeping, suspended, paused

**Working**:
Said of a running task that is not waiting.
_Avoid_: Active, busy, executing

**Checkpoint**:
A small piece of text a running task saves so that a later run continuing the same journal can carry on from it instead of starting over. Dagline keeps it until the task succeeds.
_Avoid_: Saved state, bookmark, handle, resume token

### Reporters

**Reporter**:
Something that presents a run to people while it happens. It receives the run's events and can read its snapshot, and nothing it does changes the run.
_Avoid_: Subscriber, listener, observer, sink

**Event**:
A record of one thing that happened in a run, with the time it happened. Every reporter receives a run's events in the same order.
_Avoid_: Notification, message, callback

**Snapshot**:
Everything Dagline knows about a run at one instant: the graph, and each node's status with the data attached to it.
_Avoid_: State, view, model

**Display**:
The reporter that draws a run on a terminal and keeps redrawing it as the run changes.
_Avoid_: Dashboard, TUI, live view, UI

**Summary**:
What a reporter shows once a run has ended: the run outcome, the count of tasks per status, and every failure, cancelled task and warning.
_Avoid_: Report, recap, results

### Journals

**Journal**:
The durable record of a first run and every run that continues it. A run starts a new journal, continues an existing one, or has none.
_Avoid_: State file, run record, session, log

**Continue**:
To start a run on an existing journal, so that its earlier successes are kept and its checkpoints are handed back.
_Avoid_: Resume, restart, rerun

**Earlier success**:
A task's success recorded by an earlier run of the same journal and kept by the run that continues it.
_Avoid_: Skipped, cached

**Finished**:
Said of a journal whose latest run was complete.
_Avoid_: Done, closed, complete

**Description**:
Free text the consumer gives a journal when creating it, so that an operator can tell journals apart. It never changes.
_Avoid_: Label, name, title
