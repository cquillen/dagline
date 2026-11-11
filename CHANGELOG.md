# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

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
- `NodeSnapshot.IsGroup`, a derived `Status` for a group, and `NodeSnapshot.Counts`: the tasks per status beneath a group. The `GroupSettled` event is published when the last task beneath a group settles, deepest group first.
- Docs: the Defining a graph article.
- An exception that escapes a task body, a cancellation exception included, fails that task as an unexpected failure and the run carries on: `NodeSnapshot.Failure` is `FailureKind.Unexpected`, `Message` is the exception's message, and `Detail` holds its type and stack trace. The run outcome is then incomplete.
- Blocking: every task that requires a failed task, directly, through a group or through a chain, becomes blocked at once and names the failed task in `NodeSnapshot.BlockedBy`. A blocked task has a settle time and no start time. *After* tasks and unrelated tasks still run, and a group settles failed when a task beneath it failed, or blocked when none failed and one is blocked.
- The order of events within one step is fixed: the settle, the tasks it blocks, the tasks it makes ready, the groups that settle (deepest first), then the tasks that start, each list in declaration order.
- `Snapshot.Failed` lists the failed tasks, and `Snapshot.Blocked` gives one `BlockedTasks` per failed task that blocked anything, with every task it blocked and the roots: the highest nodes whose every task it blocked.

[Unreleased]: https://github.com/cquillen/dagline/commits/main
