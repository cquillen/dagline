# The graph is fixed once a run starts

A graph is built and validated in full before a run starts and never changes during it. Lists that shape the graph (which shards, which tenants) are fetched by the consumer before it builds the graph. Letting a running task add tasks was the alternative. It was deferred because it complicates scheduling, continuing a journal and the live display all at once, and nothing in the first release needs it.

Four seams keep later mid-run expansion an addition instead of a rewrite:

1. **Identity is a stable path.** Every segment is chosen by the consumer. Dagline never generates one from an index or a counter, so a node added later cannot disturb the identity of the nodes already there.
2. **The scheduler asks "what is ready now?"** each time a task settles. It never computes one fixed order up front.
3. **Reporters and the journal follow changes as they happen.** Neither assumes a fixed task count. They do not follow them the same way (ADR 0010).
4. **A dependency on a group means "whatever is beneath it".** It is evaluated against the group's members and is never a member list frozen when the graph is built.

## Consequences

- Every graph error (a duplicate path, an invalid segment, a dependency on a path that does not exist, a dependency between a node and its own ancestor, a cycle) surfaces when the graph is built, before any task runs.
- A list that differs between a run and a later run that continues its journal produces a different graph. Path equality is the only identity for reconciling that graph with the journal (ADR 0008).
