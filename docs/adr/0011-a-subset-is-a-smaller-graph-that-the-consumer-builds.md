# A subset is a smaller graph that the consumer builds

Operators run part of the work: one shard, a handful of tenants, or several operators taking disjoint parts side by side. Dagline has no concept for this. The consumer's app builds a graph from shorter lists, and that graph is the whole of the run. Dagline never knows that a graph is a subset of anything.

The alternative was selection inside a graph: the app builds the full graph, names the nodes to run, and Dagline keeps those and whatever they depend on. One case decided against it. Every task under `trigger` *requires* the group `validate`, which spans every shard, so that nothing is triggered until every validation has passed. An operator asks for shard 3 only. A dependency on a group means whatever is beneath it (ADR 0001), which leaves Dagline two readings:

- **Keep everything beneath the group.** The validations of every shard run, and the subset is no longer a subset.
- **Keep only what was selected.** Selecting `trigger/shard-3` on its own then leaves `validate` empty, an empty group counts as met, and the gate is gone without a word.

What the operator means is that the validations of shard 3 gate the triggers of shard 3. That is not a piece of the full graph. It is the same graph-building code run over a shorter list, and the app already fetches its lists before it builds (ADR 0001). Only that code knows what the gate should shrink to.

Because a node outside the subset is simply not in the graph, no rule anywhere is specific to subsets:

- **The journal.** Its paths outside the subset are left untouched, checkpoints included, as for any journal path that is not in the graph (ADR 0008).
- **Reporters.** The snapshot, a group's status and counts, and the summary cover the graph the run was given. No event is ever published for anything outside it (ADR 0010).
- **The run outcome.** Complete means every task in the graph the run was given has succeeded.
- **The *after* rule** (ADR 0008) reads against the graph of the run. `summary` is *after* the group `shards` and succeeded in a run of shard 3 alone. Continued with every shard, `shards` holds tasks that are not earlier successes, so the success is set aside and `summary` runs again. Continued with shard 3 alone, it is kept.
- **Limits.** A size for a limit the graph does not name is ignored (ADR 0005), so one set of sizes serves every subset.

## Considered options

- **Selection inside a graph, with Dagline pulling in what the selection depends on.** Rejected for the reason above.
- **An optional dependency, dropped when its upstream is not in the graph.** Rejected because it removes a gate silently. A dependency on a path the app's filter dropped stays the ordinary build error (ADR 0001), raised before any task runs, and the app answers it by keeping the upstream or by not declaring the dependency.
- **Refusing to continue a journal with a different subset.** Rejected because Dagline cannot tell a subset from a list that changed, and a graph and a journal that differ are never an error (ADR 0008).
- **Counting a journal as unfinished while it holds a path outside the latest graph that has not succeeded.** Rejected because a failed tenant that later drops off a list would leave its journal unfinished for ever. The run reports the number of such paths instead (ADR 0008).
- **Making a task run again because it was selected.** Rejected. A subset run that continues a journal keeps its earlier successes like any other run. Being in a subset never sets one aside: only the *after* rule does, and any other finished work runs again only on a new journal (ADR 0008).

## Consequences

- Dagline offers no general way to say "only this path". The options that choose a subset are the app's own, and it threads them through its graph-building code.
- A gate that must hold in every subset is written as a dependency on a group, which shrinks with the lists. A group the filter emptied counts as met.
- A journal may be continued with a different subset, and nothing about the subset is recorded. The description, fixed when the journal is created, is the only label, so the recommendation is one journal per subset.
- A journal can be finished while work in it never succeeded. A run with every shard ends incomplete, with failures in shards 3 and 4. A run of shard 3 alone continues the journal and is complete, so the journal is finished (ADR 0008), although the failures of shard 4 are still in it. An app that continues the unfinished journal and otherwise starts a new one would pass it by. The signal is the run's second count (ADR 0008): the journal paths outside this run's graph that have not succeeded.
- A task that is in two subsets runs in each journal, because Dagline does not coordinate across processes. If it starts external work, each operator's run starts that work: a checkpoint guards against starting it twice within one journal (ADR 0006) and cannot be seen from another. Such a task must be safe to run in every journal that has it, or the app leaves it out of subset graphs.
