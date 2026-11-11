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

Every task starts pending. A task becomes ready once every task it requires has succeeded, and every ready task starts, in declaration order: the order in which the tasks were added to the builder. Here `prepare` runs first and `publish` runs once it has succeeded.

Each task body is started on the thread pool, so work a body does before its first `await` does not hold up other tasks. The body receives a <xref:Dagline.TaskContext> with the task's path and the run's clock.

The call returns when the run has ended: no task is running, none is ready, and every reporter that was not dropped has handled every event.

The graph is not changed by a run. You can run the same built graph again, and every run starts with every task pending.

## Options

<xref:Dagline.RunOptions> holds what you choose for one run. The graph says what the work is, and the options say how this run of it goes.

| Option | Default | What it is |
| --- | --- | --- |
| `Journal` | none: you must set it | The journal choice of the run. |
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

A reporter is something that presents a run to people while it happens. Each reporter in the list receives every event of the run in order: <xref:Dagline.RunStarted> first, a <xref:Dagline.TaskStatusChanged> for every change of a task's status, and <xref:Dagline.RunEnded> last. With each event comes a <xref:Dagline.Snapshot> of the run as of that event.

The list is read once when the run starts. A reporter cannot change the run, and Dagline itself never writes to the console: what your app prints is up to you and your reporters.

The run never waits for a reporter while tasks are running, and a reporter that throws is dropped while the run carries on. [Reporters](reporters.md) covers what a reporter may rely on and how to write one.

## The result

<xref:Dagline.RunResult> has three parts.

`Outcome` is the run outcome:

- **Complete**: every task succeeded. A graph with no tasks is complete.
- **Incomplete**: the run went as far as it could and at least one task did not succeed.
- **Stopped**: the run was asked to stop. No run ends this way yet.

`Snapshot` is the final snapshot of the run: every node in declaration order with its status, its start and settle times and the dependencies declared on it, plus the count of tasks per status and the start and end of the run.

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
