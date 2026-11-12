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
   |                         \--> cancelled
   \------> blocked
```

A task starts **pending**. It becomes **ready** once its dependencies are met, and **running** when the run starts it, which may be later when the cap or a limit is full. From there it ends **succeeded** or **failed**, or **cancelled** when the run is stopped while it runs.

A task becomes **blocked** when something it requires failed or was blocked: it will not run in this run. A failure therefore costs only the work that requires it. Tasks that run *after* the failed one still run, and so does everything unrelated.

A task whose status will not change again is **settled**. A group has no status of its own: it is derived from the tasks beneath it, and a group is settled once all of them are.

More in [When a task fails](running-and-stopping.md#when-a-task-fails) and [Groups in a run](defining-a-graph.md#groups-in-a-run). Definitions: [Status](../../GLOSSARY.md#status).

## Outcomes

A settled task leaves an **outcome** behind: its status, and what the task chose to say about its work. That is a one-line message, detail lines, and **warnings**, which are notes that change nothing about the status.

A failure is one of two kinds. A **reported failure** is one the task declared itself, because it found a problem. An **unexpected failure** is one it did not declare: an exception escaped its code. Both fail the task and block what requires it. They are told apart so that "the data is bad" never reads like "the code broke".

While it runs, a task can also report **progress**: a line of status text, a count, or both. Progress is live only. It shows how far a task has got and is not part of its outcome.

More in [Writing tasks](writing-tasks.md). Definitions: [Outcomes](../../GLOSSARY.md#outcomes).

## Runs and the run outcome

A **run** is one execution of a graph by one process. You start it with one call and await it. It goes on until no task is running and none is ready.

How much runs at once is bounded in two ways. The **cap** is the most tasks that may be running at any moment. A **limit** is a named ceiling that the graph puts on some of its tasks, and each run gives it a size.

A run ends with a **run outcome**, which sums up the statuses of its tasks: **complete** when every task succeeded, **incomplete** when the run went as far as it could and at least one task did not, and **stopped** when the run was asked to stop and at least one task had not succeeded. Your app decides what the run outcome means for its exit code, and Dagline offers a default mapping.

More in [Running and stopping](running-and-stopping.md). Definitions: [Runs](../../GLOSSARY.md#runs) and [Scheduling](../../GLOSSARY.md#scheduling).

## Stopping a run

A **stop** is a request, usually from the operator, that the run start nothing new and that its running tasks end. Your app decides what asks for one, such as Ctrl+C, and passes the run a stop signal. From the moment a stop is applied nothing starts, and every running task sees its cancellation token signalled.

The run then waits a **grace period**, 30 seconds by default, for its running tasks to end cleanly. A second stop ends the wait at once.

A task that ends because of the stop, or that is still running when the wait ends, is **cancelled**. Cancelled is a settled status, and it never blocks: a task that requires a cancelled one simply stays pending. A task that finished its work in spite of the stop keeps its real status. Tasks the run never started are never reached: they stay pending or ready, so a later run can see what was left to do.

More in [Stopping a run](running-and-stopping.md#stopping-a-run). Definitions: [Stop](../../GLOSSARY.md#runs), [Grace period](../../GLOSSARY.md#runs) and [Cancelled](../../GLOSSARY.md#status).

## Waiting and working

Some tasks trigger work in an external system and then wait for it to finish. Such a task is still an ordinary running task, but while it waits it is idle, and the operator should be able to tell.

A running task that has told Dagline it is idle until an external system changes is **waiting**. One that is not is **working**. Waiting is never a status: the task is running throughout, keeps its slots, and counts the same towards its group and the run outcome. Only reporters treat it differently, so that a display can show idle tasks apart from working ones and a log can say how long a task waited.

A task waits through a helper that checks the external system at once and then at a fixed interval, and that ends at once on a stop. More in [Waiting on external systems](waiting-on-external-systems.md). Definitions: [Waiting](../../GLOSSARY.md#runs) and [Working](../../GLOSSARY.md#runs).

## Journals and checkpoints

A run can keep a **journal**: the durable record of a first run and every run that continues it, kept as one file in a directory your app names. Your app always chooses, for each run, between a new journal, a given journal to continue, and none. Dagline never picks one for you. A new journal can be given a **description**, free text fixed when it is created, so that an operator can tell the journals in one directory apart.

To **continue** a journal is to start a run on it again. Every task that succeeded in an earlier run, and whose success still stands, is an **earlier success**: it is succeeded from the start, keeps its whole outcome, and does not run again. Every other task runs. A task that runs *after* something keeps its earlier success only when what it is after was kept too, so that a summary or cleanup task is never stale. A journal is **finished** when its latest run was complete.

A running task can save a **checkpoint**: a small piece of text that is on disk before the save returns, and that a run continuing the journal hands back when the task runs again. A task that triggers external work saves what it needs to re-attach to it, so that a crash or a stop never makes it trigger that work twice. Dagline keeps the checkpoint until the task succeeds.

More in [Journals](journals.md), [Running part of the work](running-part-of-the-work.md) and [Waiting on external systems](waiting-on-external-systems.md). Definitions: [Journals](../../GLOSSARY.md#journals) and [Checkpoint](../../GLOSSARY.md#runs).

## Reporters, events and the snapshot

Dagline never writes to the console. What is shown of a run is the work of **reporters**: you give a run the reporters you want, and each one presents it in its own way. A reporter watches a run and cannot change it.

A reporter is given two things:

- **Events.** An event is a record of one thing that happened, with the time it happened: the run started, a task changed status, a task added a warning, a task began or stopped waiting, a group settled, a stop was requested, the run stopped waiting, the run ended. Every reporter receives the same events in the same order, causes before effects.
- **Snapshots.** A snapshot is everything Dagline knows about the run at one instant: every node with its status and what is attached to it, and the counts across the run. One comes with each event, and a reporter can ask for the run as it is now at any time.

Events say what just happened and the snapshot says where things stand, so a reporter never has to piece that together from the events. Once a run has ended, a reporter shows a **summary** from the final snapshot, and your app gets that same snapshot back with the result of the run.

Dagline ships a reporter that prints a run as timestamped lines of text, and you can write your own: see [Reporters](reporters.md). Definitions: [Reporters](../../GLOSSARY.md#reporters).

## Where to go next

- [Defining a graph](defining-a-graph.md): the builder, groups, paths, dependencies and limits.
- [Writing tasks](writing-tasks.md): what a task body can report and how it fails.
- [Waiting on external systems](waiting-on-external-systems.md): the wait helper, transient errors and what a stop does to external work.
- [Running and stopping](running-and-stopping.md): starting a run, its options, its result and stopping it.
- [Journals](journals.md): choosing a journal, what a continuing run keeps, and what is promised after a crash.
- [Running part of the work](running-part-of-the-work.md): subsets, gates and one journal per subset.
- [Reporters](reporters.md): the plain-text reporter, and writing a reporter of your own.
- [Glossary](../../GLOSSARY.md): the exact definition of every term.
