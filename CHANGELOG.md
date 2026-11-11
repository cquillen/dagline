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
- Reporter delivery guarantees: each reporter has its own unbounded queue and is called for one event at a time, the run never waits for a reporter while tasks are running, and at its end the run waits until every reporter still attached has handled every event.
- A reporter that throws, or whose returned `ValueTask` faults, is dropped while the run carries on, and `RunResult.ReporterErrors` has a `ReporterError` naming the reporter, the event and the exception.
- Docs: the Reporters article.
- `Snapshot` lists every node in declaration order as a `NodeSnapshot` (status, start and settle times, declared dependencies), finds a node by path, and counts tasks per `NodeStatus`.
- `NodePath`, the identity of a node, with `Parse`, `TryParse`, `Segments` and `Parent`.
- `TaskContext` gives a task body its `Path` and the run's `TimeProvider`.
- Docs: the Running and stopping article, the generated API reference in the site nav, and the glossary as a page of the site.

[Unreleased]: https://github.com/cquillen/dagline/commits/main
