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
| `Status`, `Count` | [Progress](#progress-status-text-and-a-count): what the task is doing now. |
| `Warn` | [Warnings](#warnings): notes that do not change the task's status. |
| `Message`, `Detail` | The [outcome](#message-and-detail): what the task did or found. |
| `Fail` | A [reported failure](#failures). |
| `StatusOf` | [The status of a node](#reading-the-status-of-a-dependency) this task depends on. |
| `WaitUntil`, `Waiting` | [Waiting on an external system](waiting-on-external-systems.md): checking at an interval, marked as idle. |

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

    context.Status("checking entities");
    for (var checkedSoFar = 0; checkedSoFar < entities.Count; checkedSoFar++)
    {
        await CheckOneAsync(entities[checkedSoFar]);
        context.Count(checkedSoFar + 1, entities.Count);
    }
}
```

- <xref:Dagline.TaskContext.Status*> replaces the status text. Pass `null` to clear it. The text is kept as you give it. Keep it to one line: the plain-text reporter prints a line break in it as a space.
- <xref:Dagline.TaskContext.Count*> replaces the count and its total. The total is optional: leave it out for a running tally. A call without a total clears the last one. A negative count or total throws `ArgumentOutOfRangeException`.
- The text and the count are independent: setting one leaves the other as it was.

Progress shows in the snapshot as <xref:Dagline.NodeSnapshot.StatusText>, <xref:Dagline.NodeSnapshot.Count> and <xref:Dagline.NodeSnapshot.Total>. It is **never an event**, so reporting it often does not flood a reporter: a reporter reads it from the [snapshot source](reporters.md#progress-on-a-timer-the-snapshot-source) when it draws.

Progress is **frozen when the task settles**. Every later snapshot shows the task's last report, so a failed task still shows how far it got.

## Warnings

A warning is a one-line note that a task attaches to its own outcome. It never changes the task's status and never blocks a dependent: a task that warns and returns has succeeded.

```csharp
if (entity.Region is null)
{
    context.Warn($"entity {entity.Id} has no region");
}
```

<xref:Dagline.TaskContext.Warn*> does two things at once:

- It adds the warning to <xref:Dagline.NodeSnapshot.Warnings>, where it stays whether the task goes on to succeed or to fail.
- It publishes a <xref:Dagline.WarningAdded> event, so reporters can show the warning live. A warning added before the body ends always comes before the event of the task settling.

A line break in a warning is replaced by a space, so a warning is always one line.

<xref:Dagline.Snapshot.WarningCount> is the number of warnings in the whole run, and <xref:Dagline.Snapshot.WithWarnings> lists the tasks that have any, in declaration order.

## Message and detail

A task's outcome is what it leaves behind when it settles: its status, a one-line **message**, **detail lines** and its warnings. Message and detail are not only for failures. A task that succeeds can say what it did:

```csharp
context.Message($"checked {entities.Count} entities");
context.Detail($"tenant {tenant.Name}: {entities.Count} entities", $"oldest: {oldest:yyyy-MM-dd}");
```

- <xref:Dagline.TaskContext.Message*> replaces the message. A line break in it is replaced by a space.
- <xref:Dagline.TaskContext.Detail*> adds lines after those already there. Text that holds line breaks is split into one line per break, so a multi-line text can be passed whole.

They show as <xref:Dagline.NodeSnapshot.Message> and <xref:Dagline.NodeSnapshot.Detail>, and the event of the task settling carries them, so a reporter can print the message on the line that says the task succeeded.

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
    context.Message(context.StatusOf(checks) is NodeStatus.Succeeded
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

## When the run is stopped

An app can ask a run to stop: see [Stopping a run](running-and-stopping.md#stopping-a-run). The task finds out through <xref:Dagline.TaskContext.CancellationToken>, which every running task sees signalled the moment the stop is applied. Pass it to everything the body awaits, so the task ends promptly:

```csharp
builder.Task("load", async context =>
{
    foreach (var page in pages)
    {
        await LoadPageAsync(page, context.CancellationToken);
        context.Count(page.Number, pages.Count);
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

- `Status`, `Count`, `Warn`, `Message`, `Detail` and `Fail` do nothing and do not throw. `Waiting` returns a scope that does nothing, and disposing a scope does nothing.
- `StatusOf`, `Path`, `TimeProvider` and `CancellationToken` keep working, and the token stays signalled.
- When the body ends, however it ends, no event is published and no snapshot changes. An exception from it is observed and dropped, so it never surfaces as an unobserved task exception.

A body that honours its token never gets here. Pass the token to everything you await, and keep any cleanup short enough to fit in the grace period.

## After the task has settled

Once a task has settled, its outcome is fixed. If a body leaves work running that still holds the context, `Status`, `Count`, `Warn`, `Message`, `Detail` and `Fail` then change nothing and publish nothing. They still check their arguments, and `Fail` still returns an exception. `StatusOf` still answers.
