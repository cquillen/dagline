# Concepts

> [!NOTE]
> TODO: write this as the core model is designed.

This page will define Dagline's core vocabulary:

- **DAG**: a set of tasks connected by dependencies, with no cycles.
- **Task**: a unit of work.
- **Dependency**: a "must finish before" edge between two tasks.
- **Run**: one execution of a DAG.
- **Progress**: how a run reports queued, running, completed and failed tasks.
