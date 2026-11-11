# Dependencies order work and carry no data

A dependency says only when the dependent may start. It comes in two kinds: *requires* is met when the upstream has succeeded, and *after* is met when the upstream has settled, whatever the outcome. A task never receives a value produced by an upstream task.

The reason is resume. A task that succeeded in an earlier run is skipped, so any value it handed downstream would have to be saved and loaded back. That would turn the state file into a data store, with serialisation and versioning of consumer types to match. Tasks share data through the consumer's own services, and whatever a task needs to know about its place in the graph (which tenant it belongs to) is bound when the consumer builds the graph.

A task may read the status of the nodes it depends on, so that a cleanup task knows what it is cleaning up after. Status is Dagline's own data, not a value a task produced.

## Considered options

- **Data-flow dependencies**, where an upstream's output is the dependent's input. Rejected for the resume cost above.
- **One kind of dependency plus an end-of-run hook** for cleanup. Rejected because cleanup is needed per tenant and per shard, and a hook is not a task the operator can see.
- **A per-task rule covering all of its dependencies** ("run when all succeeded" or "run when all settled"). Rejected because one task can need both: it *requires* its own setup and runs *after* the work it tidies up.

## Consequences

- An *after* task that succeeded in an earlier run may hold stale results once its upstreams run again on resume. Resume has to decide when such a task runs again.
- Whether *after* tasks still run when the operator stops a run is a scheduling decision.
