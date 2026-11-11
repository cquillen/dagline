# Dagline

Dagline runs a graph of tasks inside a command-line app and shows the operator what is happening. This glossary fixes the words used for that graph and for the work that moves through it.

## Language

### Graph

**Graph**:
The tree of nodes together with the dependencies between them. A graph does not change once a run starts.
_Avoid_: Pipeline, workflow, plan

**Node**:
A task or a group.
_Avoid_: Item, element, vertex

**Task**:
A unit of work that runs consumer code. A task is always a leaf of the tree.
_Avoid_: Job, step, activity

**Group**:
A named container of tasks and other groups. A group never runs code itself.
_Avoid_: Stage, phase, parent task

**Path**:
The identity of a node: its segments from the root joined by `/`, as in `shard-3/tenant-a/prepare`. The same path means the same work in every run.
_Avoid_: Id, key, name

**Segment**:
One part of a path, naming a node among its siblings. The consumer chooses every segment.
_Avoid_: Slug, label

### Dependencies

**Dependency**:
A directed link from a dependent node to an upstream node. It orders work and carries no data.
_Avoid_: Edge, link, input

**Requires**:
The kind of dependency that is met when the upstream has succeeded.
_Avoid_: Needs, depends on

**After**:
The kind of dependency that is met when the upstream has settled, whatever the outcome.
_Avoid_: Finally, always

**Settled**:
Said of a task that will not change status again in this run, and of a group once every task beneath it is settled. Provisional until the task statuses are defined.
_Avoid_: Done, finished, complete
