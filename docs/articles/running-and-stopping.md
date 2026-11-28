# Running and stopping

A run is one execution of a graph by one process. You start it, and get back the run: you can ask it to drain or to stop, read its current snapshot, and await its result, which says how it ended and everything Dagline knows about it. An app that needs none of that runs a graph to its end in one call.

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

Every task starts pending, except a task that a [journal](journals.md) the run continues records as succeeded and whose success [stands](journals.md#an-after-task-runs-again-when-what-it-is-after-runs-again). A task becomes ready once every dependency it has is met, and a ready task starts as soon as [the max running tasks and its limits](#sizing-the-max-running-tasks-and-limits) have room, in [declaration order](defining-a-graph.md#declaration-order). Here `prepare` runs first and `publish` runs once it has succeeded. [Defining a graph](defining-a-graph.md) covers groups, paths and both kinds of dependency.

Each task body is started on the thread pool, so work a body does before its first `await` does not hold up other tasks. The body receives a <xref:Dagline.TaskContext> with the task's path and the run's clock, through which it also reports progress, warnings and its outcome: see [Writing tasks](writing-tasks.md).

The call returns when the run has ended: no task is running, none is ready (or the run was [drained or stopped](#ending-a-run-early)), and every reporter that was not dropped has handled every event.

The graph is not changed by a run. You can run the same built graph again, one run after another or several at once, and every run starts with every task pending.

## Holding a run

`RunAsync` is a shorthand. <xref:Dagline.Runner.Start*> takes the same arguments and returns the <xref:Dagline.Run> itself, already started, for an app that wants to do more than wait for it:

```csharp
var run = Runner.Start(graph, new RunOptions { Journal = Journal.None });

Console.WriteLine($"{run.Current.Counts[NodeStatus.Running]} tasks running");

var result = await run.Result;
```

- <xref:Dagline.Run.Current> is the run as it is now. It answers as soon as `Start` returns, before anything has been published, and is the final snapshot once the run has ended. Reading it never waits for the run, a task or a reporter.
- <xref:Dagline.Run.Result> completes with the run's result once it has ended. It is the same task `RunAsync` returns, and the same task every time you read it.
- <xref:Dagline.Run.Drain> and <xref:Dagline.Run.Stop> [end the run early](#ending-a-run-early).

Each call on the run only hands the request to the run and returns at once, from any thread. One loop owns the run and applies what it is told one thing at a time, in the order it was told, so none of the run's work happens on your thread. A run holds no resource of its own and is not disposed. Reporters are never handed the run, only a read-only [source of snapshots](reporters.md), so a reporter cannot drain or stop it.

## Options

<xref:Dagline.RunOptions> holds what you choose for one run. The graph says what the work is, and the options say how this run of it goes.

| Option | Default | What it is |
| --- | --- | --- |
| `Journal` | none: you must set it | The journal choice of the run. |
| `MaxRunningTasks` | 16 | The most tasks that may be running at once. |
| `Limits` | no sizes | The size of each limit the graph names. |
| `TimeProvider` | `TimeProvider.System` | The clock of the run. |
| `GracePeriod` | 30 seconds | How long a stopped run waits for its running tasks. See [the grace period](#the-grace-period). |
| `Reporters` | no reporters | What watches the run. |

### Journal

The journal choice is required, so that running with no journal is something you chose and can see in the code. There are three choices:

- <xref:Dagline.Journal.New*> starts a new journal in a directory you name, so that a later run can continue it.
- <xref:Dagline.Journal.Continue*> continues a given journal: a task it records as succeeded is kept as an earlier success and does not run, and every other task runs. The exception is a task *after* something that runs again: its success is [set aside](journals.md#an-after-task-runs-again-when-what-it-is-after-runs-again) and it runs too.
- <xref:Dagline.Journal.None> records nothing, and a later run cannot continue it.

```csharp
var result = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New("journals/export", "all shards") });
Console.WriteLine($"journal {result.Snapshot.Journal!.Id}");
```

[Journals](journals.md) covers the three choices, what a continuing run keeps and what it runs again.

### TimeProvider

Every time in a run is read from this clock: the time on each event, each task's start and settle times, and the start and end of the run. Each task body receives the same clock on its context, so a delay a task takes can be controlled by a test:

```csharp
builder.Task("prepare", context => Task.Delay(TimeSpan.FromSeconds(30), context.TimeProvider));
```

In a test, pass a fake clock such as `FakeTimeProvider` from the `Microsoft.Extensions.TimeProvider.Testing` package. The grace period is already running on that clock when a reporter hears <xref:Dagline.StopRequested>, so a test that waits for that event and then advances the clock by the grace period sees it end exactly then.

### Reporters

A reporter is something that presents a run to people while it happens. Each reporter in the list receives every event of the run in order: <xref:Dagline.RunStarted> first, a <xref:Dagline.TaskStatusChanged> for every change of a task's status, a <xref:Dagline.GroupSettled> when the last task beneath a group settles, and <xref:Dagline.RunEnded> last. With each event comes a <xref:Dagline.Snapshot> of the run as of that event.

The list is read once when the run starts. A reporter cannot change the run, and Dagline itself never writes to the console: what your app prints is up to you and your reporters.

The run never waits for a reporter while tasks are running, and a reporter that throws is dropped while the run carries on. [Reporters](reporters.md) covers what a reporter may rely on, how to write one, and [the plain-text reporter](reporters.md#the-plain-text-reporter) that Dagline ships. On a terminal, `SpectreReporter.Create()` from `Dagline.Spectre` gives [the terminal display](terminal-ui.md).

## Sizing the max running tasks and limits

Two options bound how much runs at once.

The **max running tasks**, <xref:Dagline.RunOptions.MaxRunningTasks>, is the most tasks that may be running at once, whatever limits they hold. It defaults to 16. The default is fixed and not derived from the processor count, because a task mostly waits on other systems.

A **limit** is a ceiling on the tasks that hold it. The graph [names limits](defining-a-graph.md#limits) on tasks and on groups, and the run gives each one a size:

```csharp
var result = await Runner.RunAsync(graph, new RunOptions
{
    Journal = Journal.None,
    MaxRunningTasks = 32,
    Limits = new Dictionary<string, int>
    {
        ["database"] = 4,
        ["files"] = 2,
    },
});
```

Sizes belong to the run and not to the graph because they are tuning: the graph is the same work whatever machine runs it.

- Every limit the graph names needs a size. A missing one is an [error before the run starts](#errors-before-a-run-starts), so a misspelt limit name cannot silently remove a bound.
- The max running tasks and every size must be at least 1.
- A size for a limit the graph does not name is ignored, so one set of sizes can serve several graphs.
- Names are compared exactly: `database` and `Database` are two limits.

### How ready tasks start

A running task holds one slot of the max running tasks and one slot of each of its limits, from the moment it starts until it settles, whatever its status. That includes the time it spends waiting on another system. If your tasks wait a lot, set a high max running tasks and put limits on the heavy work.

When the run starts, and whenever a task settles after that, Dagline tries the ready tasks in declaration order. Each one that finds a free slot in the max running tasks and in every one of its limits takes them all and starts. One that does not fit takes none and is passed over: it stays ready, and the tasks behind it are still tried. A task held by a full limit therefore never holds up a task that does not need that limit.

There are no priorities and no fairness. A task that needs two busy limits can be passed over many times.

### How full they are

A snapshot shows how busy the run is. <xref:Dagline.Snapshot.RunningTasks> is the max running tasks, and <xref:Dagline.Snapshot.Limits> has every limit the graph names. Each is a <xref:Dagline.LimitUse>: its size, how many of its slots are in use, and <xref:Dagline.LimitUse.IsFull>, which says whether a ready task that holds it must wait.

```csharp
var database = snapshot.Limits["database"];
Console.WriteLine($"{snapshot.RunningTasks.InUse} of {snapshot.RunningTasks.Size} running, database {database.InUse} of {database.Size}");
```

## The result

<xref:Dagline.RunResult> has three parts.

`Outcome` is the run outcome:

- **Complete**: every task succeeded. A graph with no tasks is complete.
- **Incomplete**: the run went as far as it could and at least one task did not succeed.
- **Stopped**: the run was [drained or stopped](#ending-a-run-early) and at least one task did not succeed.

`Snapshot` is the final snapshot of the run: every node in declaration order with its status, its start and settle times and the dependencies and limits declared on it, plus the count of tasks per status, the max running tasks and each limit, the start and end of the run, and the drain and the stop if there were any.

```csharp
foreach (var node in result.Snapshot.Nodes)
{
    Console.WriteLine($"{node.Path}: {node.Status} at {node.Settled:O}");
}

Console.WriteLine($"{result.Snapshot.Counts[NodeStatus.Succeeded]} tasks succeeded");
```

`ReporterErrors` has one entry for each reporter that threw during the run and was dropped, naming the reporter, the event it was handling and the exception. It is empty when no reporter failed. A reporter's error is never thrown at your app and never changes the outcome: see [Reporters](reporters.md#when-a-reporter-fails).

## When a task fails

A failure costs only the work that requires it. An exception that escapes a task body fails that task and nothing else: it is never thrown from `RunAsync`, and the run carries on.

- The task ends **failed**, as an *unexpected failure*. Its message is the exception's message, and the exception's type and stack trace are added to its detail lines. A cancellation exception is treated the same way unless the run has been [stopped](#what-a-stop-does).
- A task that declares a failure itself, with `throw context.Fail(...)`, ends **failed** as a *reported failure*, and everything below applies to it too: see [Failures](writing-tasks.md#failures).
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

## Ending a run early

An app can end a run early in two ways:

- A **drain** starts nothing new and lets the running tasks finish as they would. Your app calls <xref:Dagline.Run.Drain> on the run.
- A **stop** starts nothing new and tells the running tasks to end now. Your app calls <xref:Dagline.Run.Stop> on the run, or cancels the `CancellationToken` it passed when it started the run, the last parameter as in any .NET async method. The two are one request: whichever comes first applies the stop, and the other then changes nothing.

A stop can follow a drain: an operator who started a drain and cannot wait for the running tasks to finish stops the run. A drain or a stop asked for again, or once the run has ended, changes nothing. The usual triggers are Ctrl+C and SIGTERM, which the core package hooks to a run in one call:

```csharp
var run = Runner.Start(graph, new RunOptions { Journal = Journal.None });
using var console = ConsoleStop.Hook(run);

var result = await run.Result;
return result.Outcome.ToExitCode();
```

<xref:Dagline.ConsoleStop.Hook*> hooks the console's cancel key and SIGTERM, which a service manager or a container runtime sends to ask a process to end, to the run you give it:

- **The first Ctrl+C** drains the run.
- **The second Ctrl+C**, or **a SIGTERM** at any time, stops it. SIGTERM skips the drain because a process manager kills the process soon after sending it.
- **After that** nothing more is asked and nothing is swallowed, so the operating system ends the process. That is the way out of an app that hangs, even after a stop.

Each Ctrl+C and SIGTERM that drains or stops is swallowed, so the process lives on and the run returns its result. There is no option to leave SIGTERM out. The hook counts its own presses, so a drain your app asks for itself does not make the next Ctrl+C a stop. Disposing the hook unhooks both. The hook needs a run, so hook it once the run has started: before that, Ctrl+C ends the process as the operating system would, and nothing has started that a drain could let finish. See [The terminal display](terminal-ui.md#draining-and-stopping).

On Windows, .NET raises SIGTERM when the machine shuts down or the user logs off, and Windows ends the process a few seconds later whatever the hook does. On a platform where .NET cannot hook SIGTERM, the hook listens to Ctrl+C alone.

Dagline hooks no Ctrl+C and no process signal unless your app calls `ConsoleStop.Hook`: a library should not take over your process, so what drains or stops a run is up to your app. To wire them yourself, call `Drain` or `Stop` on the run from wherever you decide, such as a file appearing or a message from another process, and pass any token you already have, such as your host's shutdown token or a `CancellationTokenSource` with a timeout:

```csharp
using var deadline = new CancellationTokenSource(TimeSpan.FromHours(6));
var run = Runner.Start(graph, options, deadline.Token);

using var watcher = new FileSystemWatcher("/var/run/export", "drain");
watcher.Created += (_, _) => run.Drain();
watcher.EnableRaisingEvents = true;

var result = await run.Result;
```

`Drain` and `Stop` are safe to call from any thread, a console's cancel handler included. They never block and never throw. A run given no token, or `CancellationToken.None`, can still be stopped with `Stop`.

Cancelling the token, or calling `Stop`, never makes the run's result throw. The run ends and returns its result as usual, with its final snapshot and its reporter errors. Cancelling the token once the run has ended does nothing.

### What a drain does

The run applies a drain like any other change, one at a time, and publishes a <xref:Dagline.DrainRequested> event. From that moment:

- **Nothing starts**, *after* tasks included.
- **The running tasks are not told.** Their token is not signalled, no member of their context says the run is draining, and they finish however they would: succeeded, failed or cancelled by their own doing.
- **The run waits for its running tasks** however long they take, then returns. A drain has no grace period. A task waiting hours on an external system holds the run open for hours: the way out is a stop.

A cancellation exception that escapes a task during a drain is a failure, as before any stop, since the run did not ask the task to end. Requesting the drain again, or once a stop has been applied, changes nothing. <xref:Dagline.Snapshot.Drain> holds when the drain was applied, from the `DrainRequested` snapshot on.

### What a stop does

Cancelling the token, like calling `Stop`, only hands the request to the run, so no work of the run happens on the thread that cancelled it. The run applies the stop like any other change and publishes a <xref:Dagline.StopRequested> event, after `DrainRequested` when a drain came first. From that moment:

- **Nothing starts**, *after* tasks included. A stop means the run starts nothing more, never that cleanup work starts.
- **Every running task sees its token signalled**: <xref:Dagline.TaskContext.CancellationToken> on its context. That token is the run's own, never your token itself, and it is signalled before `StopRequested` is published, as the grace period is already running when a reporter hears of the stop. [Writing tasks](writing-tasks.md#when-the-run-is-stopped) says how a task ends once a stop has been applied.
- **The run waits for its running tasks to end**, for at most the [grace period](#the-grace-period), then returns.

A stop does not undo anything, does not cancel work in other systems, and does not run *after* tasks. A task that ignores its token keeps the run waiting until the grace period runs out.

### What a drain and a stop share

Only starting is suspended. While the run waits, a task that succeeds still moves its dependents from pending to ready, where they stay, and a task that fails still blocks its dependents at once.

A token already cancelled when the run is started ends the run from its start: every task with no dependency becomes ready, the stop is applied, and the run ends with nothing started. The token is read after the options and the journal have been checked, so a bad option or a journal that cannot be opened still throws first. A token is not tied to one run: passing one already cancelled to a second run ends that run from its start. A drain asked for straight after `Start` returns may come after the first tasks have started.

### The grace period

<xref:Dagline.RunOptions.GracePeriod> bounds the wait for the running tasks after a stop. It is 30 seconds by default, and is measured on the run's `TimeProvider` from the moment the stop is applied. <xref:Dagline.Snapshot.Stop> carries the grace period and the deadline, so a display can show the time left.

The wait ends in one of two ways:

- **The last running task ends.** The run ends at once, before the grace period is over.
- **The grace period runs out** with tasks still running.

Nothing else ends it: cancelling the token again and requesting a drain change nothing. An operator who cannot wait kills the process, which the journal survives.

When the grace period runs out, the run publishes a <xref:Dagline.GraceEnded> event. Then each task still running is recorded as **cancelled**, in declaration order, with the message `still running when the run stopped waiting`, and the run **lets go** of it: the run's result completes while that body may still be running, and nothing the body does afterwards changes anything. [Writing tasks](writing-tasks.md#a-task-the-run-has-let-go-of) says what such a task sees. A task that ignores cancellation therefore cannot hang your process.

Both extremes are a setting:

```csharp
// Let go of running tasks the moment a stop is applied.
new RunOptions { Journal = Journal.None, GracePeriod = TimeSpan.Zero };

// Wait for the running tasks however long they take.
new RunOptions { Journal = Journal.None, GracePeriod = Timeout.InfiniteTimeSpan };
```

A limited grace period is at most `uint.MaxValue - 1` milliseconds, about 49.7 days: the longest timer a `TimeProvider` can create. A longer one is refused before the run starts; use `Timeout.InfiniteTimeSpan` to wait with no limit.

### Cancelled and never reached

A drained or stopped run leaves two kinds of unfinished work, and the snapshot tells them apart:

- **Cancelled** tasks were running and ended because of the stop: their body ended with a cancellation exception, or was still running when the grace period ran out. Cancelled is a settled status. <xref:Dagline.Snapshot.Cancelled> lists them in declaration order. A cancelled task never blocks: a task that requires it stays pending.
- **Never reached** tasks had not started: they keep their status, pending or ready, and are not settled. <xref:Dagline.Snapshot.NeverReached> lists them in declaration order once the run has ended, and is empty before then. A group with such a task beneath it does not settle. Tasks a failure blocked are not among them: see [Which tasks did not run, and why](reporters.md#which-tasks-did-not-run-and-why).

A task that finished in spite of the drain or the stop keeps its real status, succeeded or failed.

```csharp
foreach (var cancelled in result.Snapshot.Cancelled)
{
    Console.WriteLine($"{cancelled.Path} was cancelled: {cancelled.Message}");
}

Console.WriteLine($"{result.Snapshot.NeverReached.Count} tasks were never reached");
```

### The stopped run outcome

The run outcome is **stopped** when a drain or a stop was applied and at least one task has not succeeded. It takes precedence over incomplete, and maps to exit code 2, so a script can tell a run that was cut short from one that failed. A run in which every task succeeded despite the drain or the stop, such as one whose last task succeeded as the stop arrived, is **complete**, and so is a graph with no tasks.

## When a journal write fails

A run on a [journal](journals.md) writes to it all through the run. If a write fails, because the disk is full or the file is gone, the run can no longer record what it does, so Dagline stops it rather than let hours of unrecorded work pile up:

- Dagline stops the run itself. A <xref:Dagline.StopRequested> event is published with `By` set to <xref:Dagline.StopRequester.Dagline>, unless a stop was already in progress, and from then on the stop is like any other: nothing new starts, running tasks see their token signalled, and the run waits the grace period.
- Neither a drain nor your token cancelled shortens that wait.
- Nothing more starts. A task whose start could not be written does not start, and stays ready. The dependents of a task whose settle could not be written become ready and do not start.
- Nothing more is written to the journal. Events still describe what happens, so reporters show the run to its end, but none of it is recorded.
- The run ends with the run outcome **unrecorded**, and nothing is thrown. Unrecorded takes precedence over the other three: a run whose last settle could not be written is not called complete. <xref:Dagline.RunEnded> carries it in `Outcome`, with the file system's failure beside it in `JournalError`: an `IOException`, or an `UnauthorizedAccessException` when the process may no longer write the file. The run's result, the same as ever, carries the same outcome and error in <xref:Dagline.RunResult.Outcome> and <xref:Dagline.RunResult.JournalError>, its reporter errors and its final snapshot, whose `Outcome` is unrecorded too.
- A checkpoint save that fails, or that comes after a failed write, throws <xref:Dagline.JournalWriteException> to the task that called it, with the snapshot as of that save. It is the only place the exception is thrown.
- The one exception is the last write of a run, its end. By then every settle is on disk and nothing is left to stop, so the run keeps the outcome it reached, and `RunEnded` and the result carry the error beside it in `JournalError`. The journal has no end recorded.

```csharp
var result = await Runner.RunAsync(graph, options, cancellationToken);
if (result.JournalError is { } failed)
{
    Console.Error.WriteLine($"The journal could not be written: {failed.Message}");
}

return result.Outcome.ToExitCode();
```

The journal has no end recorded, so <xref:Dagline.Journals.ListAsync*> lists it as `Died`. Once the cause is fixed it can be continued like any other, keeping every success that was recorded before the failure.

## Exit codes

Dagline never sets the exit code of your process. <xref:Dagline.RunOutcomeExtensions.ToExitCode*> gives a default mapping that you are free not to use:

| Run outcome | Exit code |
| --- | --- |
| Complete | 0 |
| Incomplete | 1 |
| Stopped | 2 |
| Unrecorded | 3 |

The mapping is stable: these codes keep their meaning in every release, so a script that calls your app can rely on them. Unrecorded was added after 0.1.0, as code 3, and 0, 1 and 2 kept their meaning. See [Compatibility](compatibility.md).

## Errors before a run starts

`Start` and `RunAsync` throw `ArgumentNullException` when the graph or the options are null, and `ArgumentException` when a reporter in the options is null. Both are thrown at the call, before any task starts and before any reporter is called.

`Start` and `RunAsync` throw <xref:Dagline.RunConfigurationException> when the options cannot work for the graph:

- `MaxRunningTasks` is below 1,
- a size in `Limits` is below 1,
- the graph names a limit that `Limits` gives no size,
- the grace period is below zero or above about 49.7 days (`uint.MaxValue - 1` milliseconds), and is not `Timeout.InfiniteTimeSpan`.

Its message lists every problem, one to a line, so you can fix them all in one pass:

```text
The options cannot be used to run the graph:
- MaxRunningTasks is 0. It must be at least 1.
- The graph names the limit 'database', and the options give it no size.
```

<xref:Dagline.RunConfigurationException.Problems> holds the same problems, in the same order, without the heading or the dashes, so you can show each one your own way without splitting the message:

```csharp
catch (RunConfigurationException refused)
{
    foreach (var problem in refused.Problems)
    {
        Console.Error.WriteLine($"error: {problem}");
    }

    return 64;
}
```

It is thrown at the call too: no task has started, no event has been published and no reporter has been called.

A run on a journal then holds, opens and reads it, also before anything has happened. Each of these is thrown at the call, checked in this order:

| Exception | When |
| --- | --- |
| <xref:Dagline.JournalInUseException> | Another run holds the journal, in this process or another. |
| <xref:Dagline.JournalNotFoundException> | <xref:Dagline.Journal.Continue*> names a journal that is not in its directory. |
| <xref:Dagline.JournalAccessException> | The journal, its lock file or its directory cannot be opened, created or read, or a new journal's first line cannot be written. |
| <xref:Dagline.JournalFormatException> | The file cannot be read as a journal: its first line is not a journal header, its format version is unknown (in `Version`), or a line other than the last is not an entry. |

The first three carry the journal's `Id` and `Directory`; for a directory that cannot be created, `JournalAccessException.Id` is null. Each message names the file or directory and says what could not be done with it. See [Journals](journals.md#when-a-journal-cannot-be-used) for what each means for the operator.

`RunConfigurationException` and the journal exceptions derive from <xref:Dagline.DaglineException>, the base of every exception that Dagline itself throws.

A graph that cannot run never reaches `RunAsync`. `Build` refuses it with a <xref:Dagline.GraphException>, which has the same base: see [Build errors](defining-a-graph.md#build-errors).
