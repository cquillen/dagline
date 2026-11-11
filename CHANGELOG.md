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
- `RunResult` with the `RunOutcome` and the final `Snapshot`, and `RunOutcome.ToExitCode()` giving 0, 1 and 2 for complete, incomplete and stopped.
- `IReporter` receives `RunStarted`, every `TaskStatusChanged` and `RunEnded` in order, each with the `Snapshot` as of that event, and can read the run at any time through the `ISnapshotSource` on `RunStarted`.
- `Snapshot` lists every node in declaration order as a `NodeSnapshot` (status, start and settle times, declared dependencies), finds a node by path, and counts tasks per `NodeStatus`.
- `NodePath`, the identity of a node, with `Parse`, `TryParse`, `Segments` and `Parent`.
- `TaskContext` gives a task body its `Path` and the run's `TimeProvider`.
- Docs: the Running and stopping article, the generated API reference in the site nav, and the glossary as a page of the site.
- Groups: `GraphBuilder.Group` and `GroupNode.Group` return a `GroupNode` that is also the scope for its children, so groups nest to any depth with plain loops. `Title` on a task or group gives it a readable name, carried to `NodeSnapshot.Title`.
- `After` beside `Requires`, on tasks and on groups, each taking one handle or a collection of handles. A dependency against a group covers every task beneath it, one declared on a group applies to every task beneath it, an empty group is met at once, both kinds on one upstream are treated as *requires*, and exact duplicates collapse.
- Declaration order is a depth-first walk of the tree with each group's children in the order added to that group. `Graph` is an `IReadOnlyList<Node>` in that order and finds a node by path with `TryGetNode`.
- `NodeSnapshot.IsGroup`, a derived `Status` for a group, and `NodeSnapshot.Counts`: the tasks per status beneath a group. The `GroupSettled` event is published when the last task beneath a group settles, deepest group first.
- Docs: the Defining a graph article.

[Unreleased]: https://github.com/cquillen/dagline/commits/main
