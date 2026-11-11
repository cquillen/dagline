# The graph is fixed once a run starts

A graph is built and validated in full before a run starts and never changes during it. Lists that shape the graph (which shards, which tenants) are fetched by the consumer before it builds the graph. Letting a running task add tasks was the alternative. It was deferred because it complicates scheduling, resume and the live display all at once, and nothing in the first release needs it.

Four seams keep later mid-run expansion an addition instead of a rewrite:

1. **Identity is a stable path.** Every segment is chosen by the consumer. Dagline never generates one from an index or a counter, so a node added later cannot disturb the identity of the nodes already there.
2. **The scheduler asks "what is ready now?"** each time a task settles. It never computes one fixed order up front.
3. **Reporters and the state file react to events.** Neither assumes a fixed task count.
4. **A dependency on a group means "whatever is beneath it".** It is evaluated against the group's members and is never a member list frozen when the graph is built.

## Consequences

- Every graph error (a duplicate path, an invalid segment, a dependency on a path that does not exist, a dependency between a node and its own ancestor, a cycle) surfaces when the graph is built, before any task runs.
- A list that differs between a run and its resume produces a different graph. Reconciling that graph with the recorded state belongs to resume, and path equality is the only identity it can rely on.
