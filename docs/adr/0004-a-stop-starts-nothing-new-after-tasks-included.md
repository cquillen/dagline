# A stop starts nothing new, *after* tasks included

When a run is asked to stop, nothing new starts from that moment and every running task has its cancellation signalled. That includes *after* tasks, even though *after* reads like `finally` and a reader would expect cleanup to run on the way out.

The reason is that an operator who asks for a stop wants the process back in seconds, and an *after* task is a full task that may take minutes or wait on an external system. Most of them could not run anyway: tasks a stopped run never reached are not settled, so an *after* dependency on a group with unreached members is never met. Running only those whose upstreams happened to have settled would make cleanup depend on timing. Cleanup that must happen before the process exits belongs in the task's own code. Everything else happens on resume.

After a stop the run waits a grace period, 30 seconds by default, for its running tasks to end. A second stop ends the wait at once. Dagline cannot kill a task, so a task still running when the wait ends is recorded as cancelled, with a line on its outcome saying it did not end within the grace period, and anything it does or reports afterwards is ignored.

## Considered options

- **Running *after* tasks during a stop.** Rejected for the reasons above.
- **Leaving a task that outlasts the wait recorded as running**, as a killed process would. Rejected because it breaks a guarantee every reporter and resume can otherwise rely on: once a run has ended, no task is running.
- **A marker of its own for such a task.** Rejected because Dagline treats it exactly as it treats any cancelled task: it never blocks a dependent and it runs again on resume.

## Consequences

- Only starting is suspended during the wait. Statuses keep moving: a task that fails still blocks its dependents at once, and a task that succeeds still moves its dependents from pending to ready, where they stay.
- A cancellation exception that escapes a task after a stop makes the task cancelled, whichever token caused it. Before a stop it is a failure.
- A task left behind may still be executing when the run returns. Dagline has let go of it, not ended it.
- The grace period may be zero, to return at once, or unlimited, to wait until the second stop.
- A stop that arrives before anything has started starts nothing, and the run outcome is stopped.
- The core never hooks Ctrl+C or process signals itself. A stop arrives from outside.
