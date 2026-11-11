# Concepts

Dagline runs a graph of tasks inside your command-line app and shows the operator what is happening. This page walks through the ideas in the order you meet them: what you build, how it runs, and how a run is shown.

It says how the ideas fit together and leaves the exact wording to the [Glossary](../../GLOSSARY.md), which has one definition for every term used on this site. Each section links to the part of the glossary it draws on and to the article that covers the subject in full.

## Graph, nodes, tasks and groups

You describe your work as a **graph**: a tree of **nodes**, plus the dependencies between them.

A node is one of two things. A **task** runs your code. A **group** holds tasks and other groups, and runs no code of its own. Groups exist so that the tree can match the shape of your work: a group per shard, a group per tenant within it, and the tasks for that tenant inside.

```text
shard-1                 group
  tenant-a              group
    load                task
    verify              task
publish                 task
```

The tree says what belongs together and nothing about order. Two tasks in one group run at the same time unless a dependency says otherwise.

You build a graph with plain statements and ordinary loops, and it does not change once it is built: see [Defining a graph](defining-a-graph.md). Definitions: [Graph](../../GLOSSARY.md#graph).

## Paths

Every node has a **path**, such as `shard-1/tenant-a/load`: one **segment** for each group that holds it, then its own. The path is the identity of the node. Events and snapshots name a node by its path, and so does everything a reporter prints.

You choose every segment, so that the same path means the same work in every run. A node may also have a title for people to read. The title is never part of the path.

More in [Paths and segments](defining-a-graph.md#paths-and-segments).

## Dependencies: requires and after

A **dependency** says that one node waits for another, its upstream. It orders work and carries no data. There are two kinds, and they differ only in what they ask of the upstream:

- **Requires** asks that the upstream succeeded. Use it for work that needs what the upstream produced.
- **After** asks only that the upstream has settled: its work is over, whatever the outcome. Use it for cleanup and for work that reports on what happened.

A dependency against a group stands for every task beneath that group, and a dependency declared on a group applies to every task beneath it, so you never list members by hand.

More in [Dependencies](defining-a-graph.md#dependencies). Definitions: [Dependencies](../../GLOSSARY.md#dependencies).

## Statuses

During a run every task has one **status** at a time, and a status only ever moves forward:

```text
pending --> ready --> running --> succeeded
   |                         \--> failed
   \------> blocked
```

A task starts **pending**. It becomes **ready** once its dependencies are met, and **running** when the run starts it, which may be later when the cap or a limit is full. From there it ends **succeeded** or **failed**.

A task becomes **blocked** when something it requires failed or was blocked: it will not run in this run. A failure therefore costs only the work that requires it. Tasks that run *after* the failed one still run, and so does everything unrelated.

A task whose status will not change again is **settled**. A group has no status of its own: it is derived from the tasks beneath it, and a group is settled once all of them are.

The glossary also defines a seventh status, **cancelled**, for a task that was running when its run was stopped. Stopping a run is not available yet, so no task ends that way so far.

More in [When a task fails](running-and-stopping.md#when-a-task-fails) and [Groups in a run](defining-a-graph.md#groups-in-a-run). Definitions: [Status](../../GLOSSARY.md#status).

## Outcomes

A settled task leaves an **outcome** behind: its status, and what the task chose to say about its work. That is a one-line message, detail lines, and **warnings**, which are notes that change nothing about the status.

A failure is one of two kinds. A **reported failure** is one the task declared itself, because it found a problem. An **unexpected failure** is one it did not declare: an exception escaped its code. Both fail the task and block what requires it. They are told apart so that "the data is bad" never reads like "the code broke".

While it runs, a task can also report **progress**: a line of status text, a count, or both. Progress is live only. It shows how far a task has got and is not part of its outcome.

More in [Writing tasks](writing-tasks.md). Definitions: [Outcomes](../../GLOSSARY.md#outcomes).

## Runs and the run outcome

A **run** is one execution of a graph by one process. You start it with one call and await it. It goes on until no task is running and none is ready.

How much runs at once is bounded in two ways. The **cap** is the most tasks that may be running at any moment. A **limit** is a named ceiling that the graph puts on some of its tasks, and each run gives it a size.

A run ends with a **run outcome**, which sums up the statuses of its tasks: **complete** when every task succeeded, and **incomplete** when the run went as far as it could and at least one task did not. A third run outcome, **stopped**, belongs to stopping a run and is not produced yet. Your app decides what the run outcome means for its exit code, and Dagline offers a default mapping.

More in [Running and stopping](running-and-stopping.md). Definitions: [Runs](../../GLOSSARY.md#runs) and [Scheduling](../../GLOSSARY.md#scheduling).

## Reporters, events and the snapshot

Dagline never writes to the console. What is shown of a run is the work of **reporters**: you give a run the reporters you want, and each one presents it in its own way. A reporter watches a run and cannot change it.

A reporter is given two things:

- **Events.** An event is a record of one thing that happened, with the time it happened: the run started, a task changed status, a task added a warning, a group settled, the run ended. Every reporter receives the same events in the same order, causes before effects.
- **Snapshots.** A snapshot is everything Dagline knows about the run at one instant: every node with its status and what is attached to it, and the counts across the run. One comes with each event, and a reporter can ask for the run as it is now at any time.

Events say what just happened and the snapshot says where things stand, so a reporter never has to piece that together from the events. Once a run has ended, a reporter shows a **summary** from the final snapshot, and your app gets that same snapshot back with the result of the run.

Dagline ships a reporter that prints a run as timestamped lines of text, and you can write your own: see [Reporters](reporters.md). Definitions: [Reporters](../../GLOSSARY.md#reporters).

## Where to go next

- [Defining a graph](defining-a-graph.md): the builder, groups, paths, dependencies and limits.
- [Writing tasks](writing-tasks.md): what a task body can report and how it fails.
- [Running and stopping](running-and-stopping.md): starting a run, its options and its result.
- [Reporters](reporters.md): the plain-text reporter, and writing a reporter of your own.
- [Glossary](../../GLOSSARY.md): the exact definition of every term.
