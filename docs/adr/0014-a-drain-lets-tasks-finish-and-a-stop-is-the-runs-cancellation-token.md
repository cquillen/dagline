# A drain lets tasks finish, and a stop is the run's cancellation token

An app ends a run early in one of two ways, and each has its own mechanism. A **drain** is requested on a `DrainSignal`: the run starts nothing new, *after* tasks included, and lets its running tasks finish as they would, without telling them and without a time limit. A **stop** is the cancellation of the `CancellationToken` passed to the run, as the last parameter, by .NET convention: the run starts nothing new, signals every running task's token, waits the grace period, and then lets go of the tasks still running. A stop can follow a drain. Only the grace period running out ends the wait; nothing else does.

Before this, one `StopSignal` carried both stages, told apart by counting requests: the first stopped the run and signalled the tasks, and the second ended the grace period. A run took no token, which broke the convention that an async method takes one, so an app's shutdown token or a test's timeout token had to be linked to the signal by hand. Adding a token next to the two-stage signal would have given the app two ways to say the same thing. Splitting the stages gives each a single meaning, and the token means what a .NET caller expects: stop now, gracefully, as `IHostedService.StopAsync` and Kestrel's shutdown do.

## Considered options

- **A token next to the two-stage signal**, cancelling it counting as one request on the signal. Rejected: two ways to request the same stop, and the meaning of cancelling the token would depend on how many requests the signal had already seen.
- **A token instead of the signal.** Rejected: a token has one state, so it cannot say both "start nothing more" and "end now", and the drain would be lost.
- **A token that lets go at once, with no grace period.** Rejected: a stop that does not give tasks the chance to end cleanly leaves more work for the run that continues the journal. An app that wants to let go at once sets a grace period of zero.
- **A second request, or a second token, that ends the grace period early.** Rejected: one fewer mechanism. An operator who cannot wait kills the process, which the journal already survives.

## Consequences

- Cancelling the token never makes `Runner.RunAsync` throw. The run ends and reports as usual, with the outcome stopped, so the app keeps its final snapshot and its reporter errors.
- A drain is invisible to tasks: no member of the task's context says the run is draining. Reporters learn of it from its own event, which is published before any stop event, and from the snapshot.
- A drain can hold a run open for as long as a running task takes, hours for a task waiting on an external system. The operator's way out is a stop.
- `ConsoleStop` is no longer a signal. It exposes a drain signal and a token: the first Ctrl+C drains, and a second Ctrl+C or any SIGTERM stops. SIGTERM skips the drain because a process manager kills the process soon after sending it.
- When Dagline stops a run itself, because its journal can no longer be written, that is a stop. A drain or a cancelled token from the app does not shorten its grace period.
- At the end of a run, cancelling the token drops reporters still busy only when it comes after every task has ended. After a stop, nothing more can drop a hung reporter, and the operator kills the process.
- The test harness passes a test's token straight through as the run's token, with its grace period still unlimited, so a body that ignores the stop still keeps the call from returning.
