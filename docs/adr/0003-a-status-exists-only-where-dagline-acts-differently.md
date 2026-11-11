# A status exists only where Dagline acts differently

A task has one of seven statuses: pending, ready, running, succeeded, failed, blocked or cancelled. A distinction earns a status only when Dagline itself acts on it: in deciding whether a dependency is met, what may start, how a group or a run rolls up, or what runs again on resume. Everything else about a task is data attached to it.

The reason is that the status set is public API. Consumers and reporters switch on it, so every extra status is one more case in each of them, and removing one later breaks them all.

Three candidates are data and not statuses:

- **Waiting on an external system** is *running*. Nothing that reads a status treats a waiting task differently from a running one.
- **Succeeded in an earlier run** is *succeeded*, with the fact that it happened earlier attached. A *requires* dependency, a group and the run outcome all have to treat it as a success anyway.
- **A reported failure and an unexpected failure** are both *failed*. Both block dependents and both run again on resume. The outcome records which kind it was.

Two distinctions did earn a status:

- **Ready**, apart from pending. A ready task has every dependency met and waits only for a concurrency limit, and it can no longer become blocked.
- **Cancelled**, apart from failed. Nothing went wrong with the work, a cancelled task does not block its dependents, and it belongs to a stopped run, not an incomplete one.

Two rules follow from the same principle and are easy to get backwards:

1. **Blocked cascades at once.** A task becomes blocked the moment any task it requires fails or is blocked, and its own dependents become blocked in the same instant, even while other upstreams are still running.
2. **Cancelled never blocks.** The dependents of a cancelled task stay pending. Blocked always means "an upstream failed" and never "the run was stopped". A task that ends by cancellation when nobody asked the run to stop has failed.

## Considered options

- **A status for everything the operator might want to see** (waiting, skipped, crashed). Rejected because a reporter can show all three from the attached data, while each one as a status would add a case to dependencies, roll-up, resume and every consumer.
- **Blocking a dependent only once the group it requires has settled.** Rejected because it hides the reach of a failure, possibly for hours, and makes cleanup tasks wait on work that can never happen.
- **Marking every unreached task as cancelled when a run is stopped.** Rejected because it buries the few tasks that were actually interrupted among all those the run never got to.

## Consequences

- Statuses only move forward: pending to ready or blocked, ready to running, running to succeeded, failed or cancelled. With no built-in retry, failed and blocked are final within a run.
- A reporter that wants to show waiting, an earlier-run success or the kind of failure reads the attached data, not the status.
- When a run is stopped, the tasks it never reached stay pending or ready and are not settled, so an *after* dependency on them is not met. Whether *after* tasks run at all during a stop is a scheduling decision.
