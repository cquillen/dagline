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

Every task starts pending. A task becomes ready once every dependency it has is met, and every ready task starts, in [declaration order](defining-a-graph.md#declaration-order). Here `prepare` runs first and `publish` runs once it has succeeded. [Defining a graph](defining-a-graph.md) covers groups, paths and both kinds of dependency.

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

A reporter is something that presents a run to people while it happens. Each reporter in the list receives every event of the run in order: <xref:Dagline.RunStarted> first, a <xref:Dagline.TaskStatusChanged> for every change of a task's status, a <xref:Dagline.GroupSettled> when the last task beneath a group settles, and <xref:Dagline.RunEnded> last. With each event comes a <xref:Dagline.Snapshot> of the run as of that event.

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

## When a task fails

A failure costs only the work that needs it. An exception that escapes a task body fails that task and nothing else: it is never thrown from `RunAsync`, and the run carries on.

- The task ends **failed**, as an *unexpected failure*. Its message is the exception's message, and its detail lines are the exception's type and stack trace. A cancellation exception is treated the same way, since a run cannot be stopped.
- Every task that *requires* the failed task becomes **blocked** at once, even while its other upstreams are still running. So does every task that requires a blocked task, to the end of the chain. A task that requires a group is blocked as soon as one task beneath the group fails or is blocked: it does not wait for the group to settle.
- A task that runs *after* the failed task, or after a blocked one, still runs.
- Every other task keeps running.
- The run outcome is **incomplete**.

A blocked task does not run in this run. It has a settle time and no start time, and its <xref:Dagline.NodeSnapshot.BlockedBy> is the path of the one failed task that started the cascade, however long the chain between them. A task that two failures would block names the first.

The final snapshot says what went wrong and what it cost:

```csharp
foreach (var failed in result.Snapshot.Failed)
{
    Console.WriteLine($"{failed.Path} failed ({failed.Failure}): {failed.Message}");
    foreach (var line in failed.Detail)
    {
        Console.WriteLine($"    {line}");
    }
}

foreach (var blocked in result.Snapshot.Blocked)
{
    var roots = string.Join(", ", blocked.Roots.Select(root => root.Path));
    Console.WriteLine($"{blocked.Cause} blocked {blocked.Tasks.Count} tasks: {roots}");
}
```

<xref:Dagline.Snapshot.Failed> lists the failed tasks in declaration order. <xref:Dagline.Snapshot.Blocked> has one <xref:Dagline.BlockedTasks> for each failed task that blocked anything: every task it blocked, and the *roots*, which are the highest nodes whose every task it blocked. When a failure blocks every task beneath a group, the group is the root, so a failure that blocked a thousand tasks beneath one group is named by that one group.

## Exit codes

Dagline never sets the exit code of your process. <xref:Dagline.RunOutcomeExtensions.ToExitCode*> gives a default mapping that you are free not to use:

| Run outcome | Exit code |
| --- | --- |
| Complete | 0 |
| Incomplete | 1 |
| Stopped | 2 |

## Errors before a run starts

`RunAsync` throws `ArgumentNullException` when the graph or the options are null, and `ArgumentException` when a reporter in the options is null. Both are thrown at the call, before any task starts and before any reporter is called.
