# Running and stopping

A run is one execution of a graph by one process. You start it with one call, await it, and get back how it ended and everything Dagline knows about it.

> [!NOTE]
> Stopping a run is not available yet. This page covers running. A run that has started goes on until no task is running and none is ready.

## Running a graph

<xref:Dagline.Runner.RunAsync*> takes a built graph and the options for this run:

```csharp
using Dagline;

var builder = new GraphBuilder();
var prepare = builder.Task("prepare", PrepareAsync);
builder.Task("publish", PublishAsync).Requires(prepare);
var graph = builder.Build();

var result = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.None });

return result.Outcome.ToExitCode();

static Task PrepareAsync(TaskContext context) => Task.CompletedTask;

static Task PublishAsync(TaskContext context) => Task.CompletedTask;
```

Every task starts pending. A task becomes ready once every dependency it has is met, and a ready task starts as soon as [the cap and its limits](#sizing-the-cap-and-limits) have room, in [declaration order](defining-a-graph.md#declaration-order). Here `prepare` runs first and `publish` runs once it has succeeded. [Defining a graph](defining-a-graph.md) covers groups, paths and both kinds of dependency.

Each task body is started on the thread pool, so work a body does before its first `await` does not hold up other tasks. The body receives a <xref:Dagline.TaskContext> with the task's path and the run's clock.

The call returns when the run has ended: no task is running, none is ready, and every reporter that was not dropped has handled every event.

The graph is not changed by a run. You can run the same built graph again, and every run starts with every task pending.

## Options

<xref:Dagline.RunOptions> holds what you choose for one run. The graph says what the work is, and the options say how this run of it goes.

| Option | Default | What it is |
| --- | --- | --- |
| `Journal` | none: you must set it | The journal choice of the run. |
| `Cap` | 16 | The most tasks that may be running at once. |
| `Limits` | no sizes | The size of each limit the graph names. |
| `TimeProvider` | `TimeProvider.System` | The clock of the run. |
| `Reporters` | no reporters | What watches the run. |

### Journal

The journal choice is required, so that running with no journal is something you chose and can see in the code. <xref:Dagline.Journal.None> is the only choice so far: the run records nothing, and a later run cannot continue it.

### TimeProvider

Every time in a run is read from this clock: the time on each event, each task's start and settle times, and the start and end of the run. Each task body receives the same clock on its context, so a delay a task takes can be controlled by a test:

```csharp
builder.Task("prepare", context => Task.Delay(TimeSpan.FromSeconds(30), context.TimeProvider));
```

In a test, pass a fake clock such as `FakeTimeProvider` from the `Microsoft.Extensions.TimeProvider.Testing` package.

### Reporters

A reporter is something that presents a run to people while it happens. Each reporter in the list receives every event of the run in order: <xref:Dagline.RunStarted> first, a <xref:Dagline.TaskStatusChanged> for every change of a task's status, a <xref:Dagline.GroupSettled> when the last task beneath a group settles, and <xref:Dagline.RunEnded> last. With each event comes a <xref:Dagline.Snapshot> of the run as of that event.

The list is read once when the run starts. A reporter cannot change the run, and Dagline itself never writes to the console: what your app prints is up to you and your reporters.

The run never waits for a reporter while tasks are running, and a reporter that throws is dropped while the run carries on. [Reporters](reporters.md) covers what a reporter may rely on and how to write one.

## Sizing the cap and limits

Two options bound how much runs at once.

The **cap** is the most tasks that may be running at once, whatever limits they hold. It defaults to 16. The default is fixed and not derived from the processor count, because a task mostly waits on other systems.

A **limit** is a ceiling on the tasks that hold it. The graph [names limits](defining-a-graph.md#limits) on tasks and on groups, and the run gives each one a size:

```csharp
var result = await Runner.RunAsync(graph, new RunOptions
{
    Journal = Journal.None,
    Cap = 32,
    Limits = new Dictionary<string, int>
    {
        ["database"] = 4,
        ["files"] = 2,
    },
});
```

Sizes belong to the run and not to the graph because they are tuning: the graph is the same work whatever machine runs it.

- Every limit the graph names needs a size. A missing one is an [error before the run starts](#errors-before-a-run-starts), so a misspelt limit name cannot silently remove a bound.
- The cap and every size must be at least 1.
- A size for a limit the graph does not name is ignored, so one set of sizes can serve several graphs.
- Names are compared exactly: `database` and `Database` are two limits.

### How ready tasks start

A running task holds one slot of the cap and one slot of each of its limits, from the moment it starts until it settles, whatever its status. That includes the time it spends waiting on another system. If your tasks wait a lot, set a high cap and put limits on the heavy work.

When the run starts, and whenever a task settles after that, Dagline tries the ready tasks in declaration order. Each one that finds a free slot in the cap and in every one of its limits takes them all and starts. One that does not fit takes none and is passed over: it stays ready, and the tasks behind it are still tried. A task held by a full limit therefore never holds up a task that does not need that limit.

There are no priorities and no fairness. A task that needs two busy limits can be passed over many times.

### How full they are

A snapshot shows how busy the run is. <xref:Dagline.Snapshot.Cap> is the cap, and <xref:Dagline.Snapshot.Limits> has every limit the graph names. Each is a <xref:Dagline.LimitUse>: its size and how many of its slots are in use.

```csharp
var database = snapshot.Limits["database"];
Console.WriteLine($"{snapshot.Cap.InUse} of {snapshot.Cap.Size} running, database {database.InUse} of {database.Size}");
```

## The result

<xref:Dagline.RunResult> has three parts.

`Outcome` is the run outcome:

- **Complete**: every task succeeded. A graph with no tasks is complete.
- **Incomplete**: the run went as far as it could and at least one task did not succeed.
- **Stopped**: the run was asked to stop. No run ends this way yet.

`Snapshot` is the final snapshot of the run: every node in declaration order with its status, its start and settle times and the dependencies and limits declared on it, plus the count of tasks per status, the cap and each limit, and the start and end of the run.

```csharp
foreach (var node in result.Snapshot.Nodes)
{
    Console.WriteLine($"{node.Path}: {node.Status} at {node.Settled:O}");
}

Console.WriteLine($"{result.Snapshot.Counts[NodeStatus.Succeeded]} tasks succeeded");
```

`ReporterErrors` has one entry for each reporter that threw during the run and was dropped, naming the reporter, the event it was handling and the exception. It is empty when no reporter failed. A reporter's error is never thrown at your app and never changes the outcome: see [Reporters](reporters.md#when-a-reporter-fails).

## Exit codes

Dagline never sets the exit code of your process. <xref:Dagline.RunOutcomeExtensions.ToExitCode*> gives a default mapping that you are free not to use:

| Run outcome | Exit code |
| --- | --- |
| Complete | 0 |
| Incomplete | 1 |
| Stopped | 2 |

## Errors before a run starts

`RunAsync` throws `ArgumentNullException` when the graph or the options are null, and `ArgumentException` when a reporter in the options is null. Both are thrown at the call, before any task starts and before any reporter is called.

`RunAsync` throws <xref:Dagline.RunConfigurationException> when the options cannot work for the graph:

- the cap is below 1,
- a size in `Limits` is below 1,
- the graph names a limit that `Limits` gives no size.

Its message lists every problem, one to a line, so you can fix them all in one pass:

```text
The options cannot be used to run the graph:
- The cap is 0. It must be at least 1.
- The graph names the limit 'database', and the options give it no size.
```

It is thrown at the call too: no task has started, no event has been published and no reporter has been called.

`RunConfigurationException` derives from <xref:Dagline.DaglineException>, the base of every exception that Dagline itself throws.

A graph that cannot run never reaches `RunAsync`. `Build` refuses it with a <xref:Dagline.GraphException>, which has the same base: see [Build errors](defining-a-graph.md#build-errors).
