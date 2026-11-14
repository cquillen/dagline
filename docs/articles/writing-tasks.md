# Writing tasks

A task is a unit of work that runs your code. Its body is an ordinary async method, and the context it is handed lets it say what it is doing and what it found.

## The body and its context

A task body is a <xref:Dagline.TaskBody>: an async delegate over a <xref:Dagline.TaskContext>. A lambda, a method group and a named method all work:

```csharp
var builder = new GraphBuilder();
var load = builder.Task("load", LoadAsync);
builder.Task("verify", async context =>
{
    await Task.Delay(TimeSpan.FromSeconds(1), context.TimeProvider);
}).Requires(load);

static async Task LoadAsync(TaskContext context)
{
    await Task.Yield();
}
```

A run creates one context for each task it starts. The context gives the body:

| Member | What it is for |
| --- | --- |
| `Path` | The path of the task this body is running as. |
| `TimeProvider` | The clock of the run. Take delays and timestamps from it so that a test controls time. |
| `CancellationToken` | Signalled when the run is [stopped](#when-the-run-is-stopped). Pass it to everything the body awaits. |
| `SetStatusText`, `SetCount` | [Progress](#progress-status-text-and-a-count): what the task is doing now. |
| `AddWarning` | [Warnings](#warnings): notes that do not change the task's status. |
| `SetMessage`, `AddDetail` | The [outcome](#message-and-detail): what the task did or found. |
| `Fail` | A [reported failure](#failures). |
| `StatusOf` | [The status of a node](#reading-the-status-of-a-dependency) this task depends on. |
| `WaitUntilAsync`, `BeginWaiting` | [Waiting on an external system](waiting-on-external-systems.md): checking at an interval, marked as idle. |
| `Checkpoint`, `SaveCheckpointAsync`, `ClearCheckpointAsync` | A [checkpoint](#checkpoints): text the task keeps for a later run of itself. |

A task **succeeds** when its body completes without an exception and without having called `Fail`. Otherwise it **fails**: see [Failures](#failures).

Everything a task reports is on its <xref:Dagline.NodeSnapshot> in the snapshot from the moment the call returns. Only a warning is also an event. [Reporters](reporters.md) covers how a reporter reads both.

## A task captures its services

Dagline has no dependency injection, and the context carries no service provider. A body gets what it needs the way any C# code does: a lambda captures it, or the body is a method of an object that holds it.

```csharp
var store = new EntityStore(connectionString);
var checker = new EntityChecker(store);

var builder = new GraphBuilder();
foreach (var tenant in tenants)
{
    var group = builder.Group(tenant.Key).Title(tenant.Name);
    var load = group.Task("load", context => store.LoadAsync(tenant, context));
    group.Task("check", context => checker.CheckAsync(tenant, context)).Requires(load);
}
```

The loop variable is captured too, which is how one method serves many tasks. An app that uses a container resolves its services before it builds the graph and captures them the same way.

A task hands no value to the tasks that depend on it: a dependency orders work and carries no data. Tasks share data through what they capture, such as the store above.

## The thread pool

Every task body is started on the thread pool. Work a body does before its first `await` therefore runs on a pool thread and does not hold up the run or the tasks that start with it.

A body that blocks a thread for a long time still occupies a pool thread for that time. Await where you can, and put a [limit](defining-a-graph.md#limits) on work that cannot.

A body may fan out to several threads of its own. The context is safe to call from all of them at once: each call is applied whole, one at a time, and no report is lost. Calls made at the same moment from two threads are applied in an order that is not defined.

## Progress: status text and a count

Progress is what a running task reports about how far it has got: a line of text, a count, or both.

```csharp
static async Task CheckAsync(Tenant tenant, EntityStore store, TaskContext context)
{
    var entities = await store.EntitiesOfAsync(tenant);

    context.SetStatusText("checking entities");
    for (var checkedSoFar = 0; checkedSoFar < entities.Count; checkedSoFar++)
    {
        await CheckOneAsync(entities[checkedSoFar]);
        context.SetCount(checkedSoFar + 1, entities.Count);
    }
}
```

- <xref:Dagline.TaskContext.SetStatusText*> replaces the status text. Pass `null` or empty text to clear it. Other text is kept as you give it. Keep it to one line: the reporters print a line break in it as a space.
- <xref:Dagline.TaskContext.SetCount*> replaces the count and its total. The total is optional: leave it out for a running tally. A call without a total clears the last one. A negative count or total throws `ArgumentOutOfRangeException`.
- The text and the count are independent: setting one leaves the other as it was.

Progress shows in the snapshot as <xref:Dagline.NodeSnapshot.StatusText>, <xref:Dagline.NodeSnapshot.Count> and <xref:Dagline.NodeSnapshot.Total>. It is **never an event**, so reporting it often does not flood a reporter: a reporter reads it from the [snapshot source](reporters.md#progress-on-a-timer-the-snapshot-source) when it draws.

Progress is **frozen when the task settles**. Every later snapshot shows the task's last report, so a failed task still shows how far it got.

## Warnings

A warning is a one-line note that a task attaches to its own outcome. It never changes the task's status and never blocks a dependent: a task that warns and returns has succeeded.

```csharp
if (entity.Region is null)
{
    context.AddWarning($"entity {entity.Id} has no region");
}
```

<xref:Dagline.TaskContext.AddWarning*> does two things at once:

- It adds the warning to <xref:Dagline.NodeSnapshot.Warnings>, where it stays whether the task goes on to succeed or to fail.
- It publishes a <xref:Dagline.WarningAdded> event, so reporters can show the warning live. A warning added before the body ends always comes before the event of the task settling.

A line break in a warning is replaced by a space, so a warning is always one line.

<xref:Dagline.Snapshot.WarningCount> is the number of warnings in the whole run, and <xref:Dagline.Snapshot.WithWarnings> lists the tasks that have any, in declaration order.

## Message and detail

A task's outcome is what it leaves behind when it settles: its status, a one-line **message**, **detail lines** and its warnings. Message and detail are not only for failures. A task that succeeds can say what it did:

```csharp
context.SetMessage($"checked {entities.Count} entities");
context.AddDetail($"tenant {tenant.Name}: {entities.Count} entities", $"oldest: {oldest:yyyy-MM-dd}");
```

- <xref:Dagline.TaskContext.SetMessage*> replaces the message. A line break in it is replaced by a space. An empty message clears it, so `context.SetMessage("")` takes back a message set earlier.
- <xref:Dagline.TaskContext.AddDetail*> adds lines after those already there. Text that holds line breaks is split into one line per break, so a multi-line text can be passed whole.

They show as <xref:Dagline.NodeSnapshot.Message> and <xref:Dagline.NodeSnapshot.Detail>, and the event of the task settling carries them, so a reporter can print the message on the line that says the task succeeded.

### One rule for the text a task reports

A message and waiting text follow one rule, applied where the task reports them: each is kept as one line, every line break replaced by a space, and empty text means none. So `context.SetMessage("")`, `context.Fail("")` and `context.BeginWaiting("")` leave the task with no message or no waiting text, and a reporter only ever tests for null. The <xref:Dagline.WaitingChanged> event carries the waiting text as it is kept. A message an earlier run recorded in a [journal](journals.md) is read back as it was written.

A warning is always one line too. Status text follows half of the rule: empty status text means none, so `context.SetStatusText("")` clears it like `context.SetStatusText(null)`, but other text is kept as given, line breaks included, and each reporter prints a line break in it as a space. A task may report status text many thousands of times a second, and making it one line each time was measured to slow that loop, so the work is left to the reporter, which does it only when it prints.

## Failures

A task fails in one of two ways, and <xref:Dagline.NodeSnapshot.Failure> says which. Either way the task ends **failed**, every task that [requires](defining-a-graph.md) it is blocked, and the rest of the run carries on: see [When a task fails](running-and-stopping.md#when-a-task-fails).

### A reported failure

A reported failure is one the task declares itself: bad data it found, or external work that ended in error. Call <xref:Dagline.TaskContext.Fail*> and throw what it returns:

```csharp
if (bad.Count > 0)
{
    throw context.Fail($"{bad.Count} bad entities", [.. bad.Select(entity => $"entity {entity.Id}: {entity.Problem}")]);
}
```

`Fail` replaces the task's message, adds its detail lines after those already there, and returns a <xref:Dagline.ReportedFailureException> for the body to throw. The task ends failed with <xref:Dagline.FailureKind.Reported>.

It is the call that records the failure, and not the exception. **Once `Fail` has been called the task ends as a reported failure however the body ends:**

- the body throws the exception `Fail` returned,
- the body returns normally, because the `throw` was forgotten,
- the body lets a different exception escape. That exception is not recorded.

So a forgotten `throw` can never turn bad data into a success. The body does run on to its end, though, so write the `throw`.

### An unexpected failure

An unexpected failure is one the task did not declare: its code broke. Any exception that escapes a body that has not called `Fail` is caught by Dagline, and the task ends failed with <xref:Dagline.FailureKind.Unexpected>:

- the message becomes the exception's message, on one line, replacing any the task had set,
- the exception's type and then its stack trace are added to the detail lines, after any the task had already added.

An exception type can override `Message` and `StackTrace`, and an override can itself throw. Dagline reads both once, when it catches the exception, and a property that cannot be read never costs the task its outcome: a message that throws or is `null` is replaced by the full name of the exception's type, and a stack trace that throws is left out, so the type is the only detail line added.

The exception is never thrown at your app and fails no other task. A cancellation exception is treated the same way unless the run has been stopped: see [When the run is stopped](#when-the-run-is-stopped).

A `ReportedFailureException` is not special by itself. One that escapes a task whose own context recorded no failure, such as an exception caught from another task's `Fail` and thrown again, is an unexpected failure.

A reporter and a summary can then tell "the task found a problem" from "the task's code broke" without parsing a message.

## The volume limit

Dagline keeps the **first 100 warnings** and the **first 100 detail lines** of each task, counted separately, and only counts the rest:

| Kept | Counted past the limit |
| --- | --- |
| <xref:Dagline.NodeSnapshot.Warnings> | <xref:Dagline.NodeSnapshot.WarningsOmitted> |
| <xref:Dagline.NodeSnapshot.Detail> | <xref:Dagline.NodeSnapshot.DetailOmitted> |

- A warning past the limit publishes no `WarningAdded` event. It is still counted in `Snapshot.WarningCount`.
- Detail lines are counted after splitting, and the lines given to `Fail` and the type and stack trace of an unexpected failure count towards the same 100.

A task that finds three thousand problems therefore cannot flood the run. Put the total in the message, the first few problems in the detail, and the full list wherever your app keeps its data.

## Reading the status of a dependency

<xref:Dagline.TaskContext.StatusOf*> gives the status of a node this task depends on. It takes the handle the builder returned, which stays valid for this after the graph is built. Its main use is a task that runs *after* other work and says how that work went:

```csharp
var builder = new GraphBuilder();
var checks = builder.Group("checks");
var owners = checks.Task("owners", CheckOwnersAsync);
var regions = checks.Task("regions", CheckRegionsAsync);

builder.Task("report", context =>
{
    var failed = new[] { owners, regions }.Where(check => context.StatusOf(check) is NodeStatus.Failed).ToList();
    context.SetMessage(context.StatusOf(checks) is NodeStatus.Succeeded
        ? "every check succeeded"
        : $"{failed.Count} checks failed: {string.Join(", ", failed.Select(check => check.Path))}");
    return Task.CompletedTask;
}).After(checks);
```

A task can ask about:

- an upstream it declared itself, with `Requires` or `After`,
- an upstream declared on a group that holds it,
- any node beneath an upstream that is a group, at any depth, whether a task or a group.

A group's status is derived from the tasks beneath it. For any other node, such as an unrelated task, the task itself, a group that holds it or a handle from another graph, `StatusOf` throws `ArgumentException`.

`StatusOf` gives the status and nothing else: no message, no detail and no data. A task starts only once its dependencies are met, so every node it can ask about has already settled. One it requires has succeeded, and one it runs after has succeeded, failed or been blocked.

## Checkpoints

A task can keep a **checkpoint**: one piece of text, at most 4,096 bytes as UTF-8, that Dagline hands back when the task runs again in a later run that continues the same [journal](journals.md). It is typically the id of work the task started in an external system, so that the later run re-attaches to that work instead of starting it a second time.

```csharp
builder.Task("export", async context =>
{
    var job = context.Checkpoint;
    if (job == "")
    {
        job = await jobs.SubmitAsync("export", context.CancellationToken);
        await context.SaveCheckpointAsync(job);
    }

    await WaitForJobAsync(job, context);
});
```

- `Checkpoint` is empty when there is none, and reads back the latest save within a run.
- `SaveCheckpointAsync` replaces the checkpoint and completes only once it is flushed to the disk. Saving an empty string clears it, and `ClearCheckpointAsync` does the same. Clearing when there is none writes nothing.
- The checkpoint is kept through a failure, a cancellation and a killed process, and discarded when the task succeeds.
- A save during the grace period after a stop succeeds. One made after the task has settled, or after the run has let go of it, throws `InvalidOperationException`.
- A checkpoint is private to the task: it is in no event and no snapshot.
- With `Journal.None` it lives in memory for the run, under the same rules, so a task's code is the same in a test.

Do not pass the context's `CancellationToken` to a save that records work the task has just started: a stop would cancel the save, and a later run would start the work again. [Waiting on external systems](waiting-on-external-systems.md#checkpoints-re-attaching-instead-of-starting-twice) gives the whole pattern, clearing a checkpoint for a fresh start, and the one window a checkpoint cannot close.

## When the run is stopped

An app can ask a run to stop: see [Stopping a run](running-and-stopping.md#stopping-a-run). The task finds out through <xref:Dagline.TaskContext.CancellationToken>, which every running task sees signalled the moment the stop is applied. Pass it to everything the body awaits, so the task ends promptly:

```csharp
builder.Task("load", async context =>
{
    foreach (var page in pages)
    {
        await LoadPageAsync(page, context.CancellationToken);
        context.SetCount(page.Number, pages.Count);
    }
});
```

The token is the same for every task of the run, is signalled once, and stays signalled. A run that is never stopped never signals it. It is already signalled when `RunAsync` returns, but a callback you register on it runs on the thread pool and the run does not wait for it. Do your own cleanup when it is signalled, so you leave other systems in a known condition, then let the cancellation escape.

Once a stop has been requested, a task ends in the first of these ways that applies:

1. It called `Fail`: it is **failed** as a reported failure. A failure you reported is never recorded as a cancellation.
2. Its body ended with any `OperationCanceledException`, whichever token caused it: it is **cancelled**.
3. Otherwise it gets its real status: **succeeded** if the body returned, and **failed** as an unexpected failure if it threw anything else. Work that finished in spite of the stop is not thrown away.

Before any stop, a cancellation exception is an unexpected failure like any other.

A cancelled task keeps its outcome: its message, detail lines, warnings and frozen progress. Cancelled is settled and never blocks: a task that requires a cancelled task stays pending, and a task that runs after it becomes ready. Neither starts, since nothing starts once a stop has been requested.

### A task the run has let go of

The run waits for its running tasks for at most the grace period, and a second stop ends the wait at once: see [The grace period](running-and-stopping.md#the-grace-period). A task still running when the wait ends is recorded as **cancelled** and the run **lets go** of it: `RunAsync` returns while its body may still be running.

Such a task has the message `still running when the run stopped waiting`, in place of any message it set. It keeps its warnings, detail lines and frozen progress, and is listed in `Snapshot.Cancelled` like any other cancelled task. If it was waiting, its wait ends just before it is cancelled.

Its body may go on, but nothing it does changes the run:

- `SetStatusText`, `SetCount`, `AddWarning`, `SetMessage`, `AddDetail` and `Fail` do nothing and do not throw. `BeginWaiting` returns a scope that does nothing, and disposing a scope does nothing.
- `SaveCheckpointAsync` and `ClearCheckpointAsync` throw `InvalidOperationException`, since nothing they did would be kept.
- `StatusOf`, `Checkpoint`, `Path`, `TimeProvider` and `CancellationToken` keep working, and the token stays signalled.
- When the body ends, however it ends, no event is published and no snapshot changes. An exception from it is observed and dropped, so it never surfaces as an unobserved task exception.

A body that honours its token never gets here. Pass the token to everything you await, and keep any cleanup short enough to fit in the grace period.

## After the task has settled

Once a task has settled, its outcome is fixed. If a body leaves work running that still holds the context, `SetStatusText`, `SetCount`, `AddWarning`, `SetMessage`, `AddDetail` and `Fail` then change nothing and publish nothing. They still check their arguments, and `Fail` still returns an exception. `StatusOf` and `Checkpoint` still answer. `SaveCheckpointAsync` and `ClearCheckpointAsync` throw `InvalidOperationException`, so that a task never believes a checkpoint was kept when it was not.
