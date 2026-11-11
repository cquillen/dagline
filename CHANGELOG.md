# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Waiting: `TaskContext.Waiting(text)` opens a waiting scope that marks a running task as idle until an external system changes. Reporters get a `WaitingChanged` event when the wait begins and when it ends, once each however many scopes overlap, and `NodeSnapshot.WaitingSince` and `NodeSnapshot.WaitingText` show the wait on the node while it lasts. Waiting is never a status: a waiting task is running and keeps its slots. A task that settles with a scope open ends its wait just before it settles.
- Docs: the waiting event and snapshot members in the Reporters article.

- Repository scaffold: `Dagline` and `Dagline.Spectre` packages, tests, sample, docs site and CI.
- `GraphBuilder` builds a `Graph` of root-level tasks, each a `TaskBody` delegate, joined by `TaskNode.Requires`.
- `Runner.RunAsync` runs a graph to its end: each task goes pending, ready, running, succeeded in dependency order, ready tasks start on the thread pool in declaration order, and one built graph can be run any number of times.
- `RunOptions` with the required `Journal` choice (`Journal.None` is the only one so far), the run's `TimeProvider` and its `Reporters`.
- Limits and the cap: `Limit` on a task or a group names a limit, and `RunOptions.Limits` and `RunOptions.Cap` (16 by default) size them for one run. A running task holds one slot of the cap and of every limit named on it or on a group that holds it. Ready tasks are tried in declaration order at the start of the run and whenever a task settles: each takes all its slots at once or none, and one that does not fit is passed over without holding up the tasks behind it.
- `RunConfigurationException`, derived from the new base `DaglineException`: `Runner.RunAsync` throws it before anything happens when the cap or a size is below 1, or the graph names a limit that has no size. Its message lists every problem.
- `Snapshot.Cap` and `Snapshot.Limits` give the cap and each limit the graph names as a `LimitUse` (size and slots in use), and `NodeSnapshot.Limits` the limits named on a node.
- Docs: naming limits in the Defining a graph article, and sizing the cap and limits in the Running and stopping article.
- `RunResult` with the `RunOutcome` and the final `Snapshot`, and `RunOutcome.ToExitCode()` giving 0, 1 and 2 for complete, incomplete and stopped.
- `IReporter` receives `RunStarted`, every `TaskStatusChanged` and `RunEnded` in order, each with the `Snapshot` as of that event, and can read the run at any time through the `ISnapshotSource` on `RunStarted`.
- Reporter delivery guarantees: each reporter has its own unbounded queue and is called for one event at a time, the run never waits for a reporter while tasks are running, and at its end the run waits until every reporter still attached has handled every event.
- A reporter that throws, or whose returned `ValueTask` faults, is dropped while the run carries on, and `RunResult.ReporterErrors` has a `ReporterError` naming the reporter, the event and the exception.
- Docs: the Reporters article.
- `Snapshot` lists every node in declaration order as a `NodeSnapshot` (status, start and settle times, declared dependencies), finds a node by path, and counts tasks per `NodeStatus`.
- `NodePath`, the identity of a node, with `Parse`, `TryParse`, `Segments` and `Parent`.
- `TaskContext` gives a task body its `Path` and the run's `TimeProvider`.
- Docs: the Running and stopping article, the generated API reference in the site nav, and the glossary as a page of the site.
- Groups: `GraphBuilder.Group` and `GroupNode.Group` return a `GroupNode` that is also the scope for its children, so groups nest to any depth with plain loops. `Title` on a task or group gives it a readable name, carried to `NodeSnapshot.Title`.
- `After` beside `Requires`, on tasks and on groups, each taking one handle or a collection of handles. A dependency against a group covers every task beneath it, one declared on a group applies to every task beneath it, an empty group is met at once, both kinds on one upstream are treated as *requires*, and exact duplicates collapse.
- Declaration order is a depth-first walk of the tree with each group's children in the order added to that group. `Graph` is an `IReadOnlyList<Node>` in that order and finds a node by path with `TryGetNode`.
- Build validation: `GraphBuilder.Build` checks the whole graph and throws one `GraphException` (a `DaglineException`) whose `Problems` lists every `GraphProblem`: an invalid segment, a duplicate path, siblings that differ only by case, a handle from another builder, a node that depends on itself, a dependency between a node and its own ancestor or descendant, and a cycle with every dependency of it spelled out. Adding a node still returns a handle when its segment is invalid or already taken.
- A builder is single use: once `Build` has returned a graph, the builder and every handle it made throw `InvalidOperationException` from any call that would change the graph.
- Docs: build errors in the Defining a graph article.
- `NodeSnapshot.IsGroup`, a derived `Status` for a group, and `NodeSnapshot.Counts`: the tasks per status beneath a group. The `GroupSettled` event is published when the last task beneath a group settles, deepest group first.
- Docs: the Defining a graph article.
- An exception that escapes a task body, a cancellation exception included, fails that task as an unexpected failure and the run carries on: `NodeSnapshot.Failure` is `FailureKind.Unexpected`, `Message` is the exception's message, and `Detail` holds its type and stack trace. The run outcome is then incomplete.
- Blocking: every task that requires a failed task, directly, through a group or through a chain, becomes blocked at once and names the failed task in `NodeSnapshot.BlockedBy`. A blocked task has a settle time and no start time. *After* tasks and unrelated tasks still run, and a group settles failed when a task beneath it failed, or blocked when none failed and one is blocked.
- The order of events within one step is fixed: the settle, the tasks it blocks, the tasks it makes ready, the groups that settle (deepest first), then the tasks that start, each list in declaration order.
- `Snapshot.Failed` lists the failed tasks, and `Snapshot.Blocked` gives one `BlockedTasks` per failed task that blocked anything, with every task it blocked and the roots: the highest nodes whose every task it blocked.
- What a task reports, through its `TaskContext`: `Status` and `Count` set progress (status text, and a count with an optional total), which shows in the snapshot as `NodeSnapshot.StatusText`, `Count` and `Total`, is never an event, and is frozen when the task settles.
- `TaskContext.Warn` adds a warning that stays on the task whatever status it settles with, in `NodeSnapshot.Warnings`, and publishes the new `WarningAdded` event. `Snapshot.WarningCount` counts every warning of the run and `Snapshot.WithWarnings` lists the tasks that have any.
- `TaskContext.Message` and `TaskContext.Detail` set the message and add detail lines of a task's outcome, on a task that succeeds as well as one that fails. A line break in a message or a warning becomes a space, and text given as detail is split into one line per break.
- Reported failures: `throw context.Fail(message, detail)` ends a task failed with `FailureKind.Reported`. Once `Fail` has been called the task ends that way however its body ends. `Fail` returns the new `ReportedFailureException`, and one that the task's own `Fail` did not make is an unexpected failure.
- An unexpected failure now adds the exception's type and stack trace after any detail lines the task had already added.
- The volume limit: the first 100 warnings and the first 100 detail lines of a task are kept, and the rest are counted in `NodeSnapshot.WarningsOmitted` and `NodeSnapshot.DetailOmitted`. No `WarningAdded` is published for a warning that is not kept.
- `TaskContext.StatusOf` reads the status of a node the task depends on: an upstream declared on it or on a group that holds it, or any node beneath an upstream group.
- A `TaskContext` is safe to call from several threads at once, and changes nothing once its task has settled.
- Docs: the Writing tasks article.
- `PlainTextReporter` prints a run to a `TextWriter` the app hands it: a UTC-timestamped line when the run starts, when a task starts, succeeds or fails (with its duration and message, and a failed task's detail lines beneath it), when a task adds a warning, when a group settles (with its counts) and when the run ends. A task that becomes ready and progress are not printed.
- The plain-text reporter prints one line for consecutive blocked events with one cause, such as `blocked 2 tasks by checks/b`, and a line of counts one minute after the last line it printed, measured on its own `TimeProvider`, until the run ends.
- The plain-text reporter ends a run with a summary: the count of tasks per status, every failed task (kind, time, duration, progress, message, detail lines and what it blocked, named by roots) and every kept warning under its task. A section with nothing in it is left out.
- The plain-text reporter writes all the lines of one event with one call and never lets two of its writes overlap. It guards only its own writes: an app that writes to the same writer during a run shares a synchronized one, such as `Console.Out` or a `TextWriter.Synchronized` wrapper, to keep the lines of one event together. A writer that throws drops the reporter and stops its line of counts, and the error is on `RunResult.ReporterErrors`.
- `samples/Dagline.Samples.Basic` is a real run: one group, two tasks and a *requires*, printed by the plain-text reporter on standard output, with the run outcome mapped to the exit code.
- Docs: the plain-text reporter and its line formats in the Reporters article, and the Concepts article.
- The plain-text reporter prints the progress of a failed task on one line: a line break in the status text the task set becomes a space. `NodeSnapshot.StatusText` still holds the text as the task gave it.
- The event records `RunStarted`, `TaskStatusChanged`, `WarningAdded` and `GroupSettled`, and `ReporterError`, `BlockedTasks` and `GraphProblem`, reject a null argument with `ArgumentNullException`, both when one is made and when one is copied by a `with` expression.
- An exception that escapes a task body and whose own `Message` or `StackTrace` throws still fails only its task, and the run still ends. A message that throws or is `null` is replaced by the full name of the exception's type, and a stack trace that throws is left out of the detail lines.
- Stopping a run: `Runner.RunAsync` takes an optional `StopSignal`, which replaces the two-parameter form. `StopSignal.Request` is safe from any thread, never blocks and never throws, and `IsRequested` and `Requests` report what was asked. Dagline hooks no console or process signal.
- Once the first request is applied, the run publishes the new `StopRequested` event, starts nothing (*after* tasks included) and signals the new `TaskContext.CancellationToken` on every running task, then waits for its running tasks to end. The wait has no time bound yet. A signal already requested when the run starts stops it with nothing started, and a signal can be passed to a later run.
- A body that ends with any cancellation exception after a stop is `Cancelled`, keeping its message, detail lines, warnings and frozen progress. A reported failure still wins, and a body that returns or throws anything else keeps its real status. Cancelled never blocks: a task that requires it stays pending, and one after it becomes ready.
- `Snapshot.Stop` (a `StopState`), `Snapshot.Cancelled` and `Snapshot.NeverReached` (the tasks left pending or ready once the run has ended). The run outcome is `Stopped` when a stop was applied and a task did not succeed, and `Complete` when every task succeeded despite it.
- Docs: stopping a run in the Running and stopping article, and how a task ends once a stop has been requested in the Writing tasks article.
- A stop ends the wait for reporters at the end of a run: any request on the signal once the run has ended, the first or a later one, drops each reporter still busy. The token passed to its `OnEventAsync` is cancelled, its remaining events are discarded, `RunResult.ReporterErrors` has its `ReporterError` with an `OperationCanceledException`, and `RunAsync` returns without waiting for its call. The run outcome is unchanged.
- Docs: a stop ending the wait for reporters in the Reporters article.

[Unreleased]: https://github.com/cquillen/dagline/commits/main
