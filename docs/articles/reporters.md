# Reporters

A reporter is something that presents a run to people while it happens: lines in a log, a display on a terminal, a message to a chat channel. Dagline itself never writes to the console. What your app shows of a run is up to the reporters you give it in <xref:Dagline.RunOptions.Reporters>.

A reporter watches a run and cannot change it. Nothing a reporter does, or fails to do, changes a task or the run outcome.

The `Dagline` package ships one reporter, [the plain-text reporter](#the-plain-text-reporter), which prints a run as timestamped lines. The sections before it are for writing your own.

On a terminal, use the display from the `Dagline.Spectre` package instead: <xref:Dagline.Spectre.SpectreReporter.Create*> gives the display when standard output is a terminal and the plain-text reporter when it is redirected, so the same code serves an operator and a log file. See [The terminal display](terminal-ui.md).

> [!NOTE]
> While the display is up, your app's own logging goes to a file and not to the console: see [The terminal display](terminal-ui.md#your-apps-own-output).

## Events and the snapshot

Dagline gives a reporter two things.

An **event** is a record of one thing that happened in the run, with the time it happened, read from the run's clock. Every event derives from <xref:Dagline.RunEvent>, and the set of kinds is closed:

| Event | When | What it carries |
| --- | --- | --- |
| <xref:Dagline.RunStarted> | First, once per run. | The snapshot source of the run. |
| <xref:Dagline.TaskStatusChanged> | Every time a task changes status, pending to ready included. | The task as it is after the change, and the status before and after. |
| <xref:Dagline.WarningAdded> | When a running task adds a warning that is kept: the first 100 of each task. | The path of the task and the warning. |
| <xref:Dagline.WaitingChanged> | When a running task begins waiting on an external system and when it stops: once each per wait, however many waiting scopes overlap. | The path of the task, whether it is now waiting, the text saying what it waits for, and when the wait began. |
| <xref:Dagline.GroupSettled> | When the last task beneath a group settles, after that task's event and deepest group first. Never for a group with no tasks beneath it, and never for one made only of earlier successes, which is settled from the start. | The path of the group, its status, and its <xref:Dagline.TaskCounts>: the tasks beneath it per status, with the earlier successes among the succeeded counted apart. Not the warnings beneath it: read the group's `WarningCount` from the snapshot handed with the event. |
| <xref:Dagline.StopRequested> | When the run applies a stop, at most once per run. From then on nothing starts. | Who asked, `By`: the app through its `StopSignal`, or Dagline when a journal write failed. The grace period: how long the run will wait for its running tasks. `Timeout.InfiniteTimeSpan` means it waits until they have ended. |
| <xref:Dagline.GraceEnded> | When the run stops waiting for its running tasks after a stop with some still running, at most once per run. Never when the last running task ended in time. | Why: the grace period was over, or a second stop. The tasks left behind follow, each cancelled. |
| <xref:Dagline.RunEnded> | Last, once per run. | Exactly one of the run outcome and an error: a run whose journal could no longer be written ends with a `JournalWriteException` in `Error` and a null `Outcome`. |

An event carries everything needed to print a line about it, so a reporter that prints a line per event looks nothing up.

Nothing an event carries is ever null, except the text of a wait the task gave none for, and one of the outcome and the error of <xref:Dagline.RunEnded>. Only Dagline creates events. The event types are records, so two events with the same values are equal, but their constructors are internal and their properties are get-only: a reporter reads an event and never makes or changes one. The same holds for what the snapshot and the result hand you, such as <xref:Dagline.LimitUse>, <xref:Dagline.StopState>, <xref:Dagline.BlockedTasks>, <xref:Dagline.Dependency> and <xref:Dagline.ReporterError>, and for <xref:Dagline.GraphProblem>. To test a reporter, run a small graph with it as one of the run's reporters, and assert on what it wrote.

### The order within one step

A change and everything that follows from it at once are one step. The events of a step all have the same time and come in a fixed order, causes before effects:

1. The change itself: the run starting, a task settling, a stop being applied, or the run stopping waiting with the tasks it leaves behind.
2. Every task that this blocks, pending to blocked.
3. Every task whose dependencies are now all met, pending to ready.
4. Every group that is now settled, deepest first.
5. Every task that now starts, ready to running.

Each list is in declaration order. The steps of a run never overlap, so the events of one are never mixed with those of another.

Once a stop has been applied, the last group is always empty: nothing starts.

A warning is a step of its own, and so is a stop being applied, and so is a task beginning or ending a wait. A stop applied before any task has started gives `RunStarted`, the tasks that become ready, `StopRequested`, then `RunEnded`. When the run stops waiting with tasks still running, `GraceEnded` comes first, then each task it leaves behind, cancelled, in declaration order. A task that settles while it is still waiting ends its wait in the step in which it settles: the `WaitingChanged` that ends it comes immediately before that task's `TaskStatusChanged`, so a settled task is never waiting.

### Waiting is a marker

A task that triggers work in an external system and then waits for it opens a waiting scope with <xref:Dagline.TaskContext.BeginWaiting(System.String)>. While a scope is open the task is **waiting**: it is idle, not working. Waiting is never a status. The task is running throughout, it keeps its slots, and its group and the run outcome are what they would be without the marker. A reporter uses it only to show idle tasks apart from working ones.

A wait begins when the task's first open scope opens and ends when its last one closes, so `WaitingChanged` comes in pairs: one with `Waiting` true and `Since` equal to `At`, then one with `Waiting` false and the same `Text` and `Since`. Overlapping scopes make one wait with the text and time of the scope that opened first, and a task that waits twice in a run gives two pairs.

A warning is a step of its own, with one event. A warning a task adds before its body ends always comes before the event of that task settling.

Progress is never an event. The status text and count a task reports change only the snapshot, and only the task's own node in it, never a group, so a task that reports often costs a reporter nothing.

A failure is the step with the most consequences. Take a group `checks` that holds the task `verify`, a task `publish` that requires `checks`, and a task `report` that runs after `checks`. When `verify` fails, a reporter receives:

```text
checks/verify: running -> failed
publish: pending -> blocked
report: pending -> ready
group checks settled: failed
report: ready -> running
```

A blocked task gets one event, pending to blocked, and its `Node` names the failed task in <xref:Dagline.NodeSnapshot.BlockedBy>. A reporter that would rather print one line for a whole cascade than one per task can group consecutive blocked events by that path, as the plain-text reporter does.

A **snapshot** is everything Dagline knows about the run at one instant: every node in declaration order with its status and times, the counts of the run's tasks, and how full the max running tasks and each limit are. A <xref:Dagline.Snapshot> is immutable, so a reporter can keep one, compare two, or hand one to another thread.

A reporter gets snapshots in two ways:

- **With each event.** The snapshot handed to <xref:Dagline.IReporter.OnEventAsync*> is the run as of that event. It includes that event and none after it, however far the run has moved on since. Use it to show totals beside the event.
- **From the snapshot source.** <xref:Dagline.RunStarted> carries an <xref:Dagline.ISnapshotSource>, and its `Current` is the run as it is now. Use it to show the run on a timer of your own.

Dagline keeps the snapshot, so a reporter never pieces it together from events, and two reporters never disagree about it.

### Counts

The run, every group and every task count their tasks with one type, <xref:Dagline.TaskCounts>: <xref:Dagline.Snapshot.Counts> for the run, <xref:Dagline.NodeSnapshot.Counts> for a node, and <xref:Dagline.GroupSettled.Counts> on the event that says a group settled. A group counts the tasks beneath it at any depth, and a task counts itself alone. So a reporter draws a group's line and the run's line with one piece of code.

| Member | What it counts |
| --- | --- |
| `counts[status]` | The tasks with that status. |
| <xref:Dagline.TaskCounts.Total> | Every task, whatever its status. |
| <xref:Dagline.TaskCounts.Settled> | The tasks whose status is settled: succeeded, failed, blocked or cancelled. With `Total` it says how far the run has got. |
| <xref:Dagline.TaskCounts.Unsucceeded> | `Total` less the succeeded: what a later run of the journal has left to do. |
| <xref:Dagline.TaskCounts.EarlierSuccesses> | The earlier successes, among the succeeded: kept from an earlier run of the journal, so the rest of the succeeded are what this run did. |
| <xref:Dagline.TaskCounts.Waiting> | The running tasks that are waiting on an external system. |
| <xref:Dagline.TaskCounts.Working> | The running tasks that are not. With `Waiting` it makes up the running, and no task is in both. |

Only Dagline makes a `TaskCounts`. It is not a dictionary and you cannot enumerate it: ask for the count of each status you show.

```csharp
var counts = snapshot.Counts;
var line = $"{counts.Settled}/{counts.Total} settled, {counts.Working} working, {counts.Waiting} waiting";
```

### Answers the snapshot gives

A snapshot also answers the questions a summary asks, each in declaration order, so every reporter's summary agrees:

- <xref:Dagline.Snapshot.Failed> lists the failed tasks. Each has its <xref:Dagline.NodeSnapshot.Failure> kind, its message and its detail lines.
- <xref:Dagline.Snapshot.Cancelled> lists the cancelled tasks: those that were running when the run was stopped and ended because of it, or were left behind when the run stopped waiting. Each keeps its message, detail lines, warnings and the progress it had reported.
- <xref:Dagline.Snapshot.NeverReached> lists the tasks a stopped run never started, which stay pending or ready. It is empty until the run has ended.
- <xref:Dagline.Snapshot.WarningCount> counts every warning added, and <xref:Dagline.Snapshot.WithWarnings> lists the tasks that have any. Each task keeps its first 100 warnings in <xref:Dagline.NodeSnapshot.Warnings> and counts the rest in <xref:Dagline.NodeSnapshot.WarningsOmitted>.
- <xref:Dagline.Snapshot.Blocked> has one <xref:Dagline.BlockedTasks> for each failed task that blocked anything: the tasks it blocked and the *roots*, the highest nodes whose every task it blocked. Name the roots and count the tasks, and a failure that blocked a thousand tasks beneath one group is one short line. <xref:Dagline.Snapshot.TryGetBlocked*> finds the entry for one failed task by its path, and gives `false` for a task that blocked nothing.
- <xref:Dagline.Snapshot.Waiting> and <xref:Dagline.Snapshot.Working> list the running tasks that are waiting on an external system and those that are working. Together they are every running task, and no task is in both.
- <xref:Dagline.Snapshot.EarlierSuccesses> lists the tasks a continuing run kept from its journal and did not run again. See [A run on a journal](#a-run-on-a-journal).

Each list holds tasks only, never a group. A question none of them answers is a filter over <xref:Dagline.Snapshot.Nodes>, which holds every group and every task in declaration order. A node says whether it is a group with <xref:Dagline.NodeSnapshot.IsGroup>: there is no kind enum to test. Its <xref:Dagline.NodeSnapshot.Dependencies> are the dependencies declared on it, one <xref:Dagline.Dependency> for each upstream node: its `Upstream` path and its <xref:Dagline.DependencyKind>, `Requires` or `After`. A dependency declared on a group is listed on the group, not on the tasks beneath it.

```csharp
var ranThisRun = snapshot.Nodes
    .Where(node => !node.IsGroup && !node.IsEarlierSuccess && node.Status is NodeStatus.Succeeded)
    .Select(node => node.Path);

var groupsWithWarnings = snapshot.Nodes.Where(node => node.IsGroup && node.WarningCount > 0);
```

### Which tasks did not run, and why

A task that ends a run without having run is in one of three lists, and each has its own cause. Read all three to account for every such task:

| List | Status | Why the task did not run | When it applies |
| --- | --- | --- | --- |
| <xref:Dagline.Snapshot.Blocked> | blocked | A task it *requires*, or one beneath a group it requires, failed. | In any run with a failure, stopped or not. Blocked is settled, so a group made only of blocked and other settled tasks settles. |
| <xref:Dagline.Snapshot.NeverReached> | pending or ready | The run was stopped before the task could start: it was ready, or still waiting on a dependency, such as one on a task the stop cancelled. | Only once a stopped run has ended. Empty while the run is going and for a run that was not stopped. These tasks are not settled. |
| <xref:Dagline.Snapshot.EarlierSuccesses> | succeeded | An earlier run of the journal succeeded with it, so this run kept that success. | Only in a run that continues a journal. |

`NeverReached` is not the tasks a failure blocked: a failure never leaves a task pending, it blocks it. A task that runs *after* a failed task is not blocked either: it runs.

```csharp
var snapshot = result.Snapshot;

foreach (var entry in snapshot.Blocked)
{
    Console.WriteLine($"{entry.Cause} failed and blocked {entry.Tasks.Count} tasks:");
    foreach (var root in entry.Roots)
    {
        Console.WriteLine($"  {root.Path}");
    }
}

foreach (var task in snapshot.NeverReached)
{
    Console.WriteLine($"{task.Path} was not reached before the run stopped");
}

Console.WriteLine($"{snapshot.EarlierSuccesses.Count} tasks were kept from earlier runs");
```

A later run of the journal has the blocked and the never-reached tasks left to do, along with the failed and the cancelled ones. <xref:Dagline.TaskCounts.Unsucceeded> on <xref:Dagline.Snapshot.Counts> counts them all.

Each node answers the same way for itself, so a reporter never writes Dagline's rules again:

- <xref:Dagline.NodeSnapshot.WarningCount> is every warning a task was given, kept in `Warnings` or only counted in `WarningsOmitted`. For a group it is the sum for the tasks beneath it, so a reporter can mark a group that holds warnings without visiting its tasks.
- <xref:Dagline.NodeSnapshot.IsWaiting> is true for a running task with a waiting scope open, and <xref:Dagline.NodeSnapshot.IsWorking> for a running task with none. Both are false for a task that is not running.
- A group is waiting when it has running tasks beneath it and every one of them is waiting: it is idle on external systems. It is working when its status is running and it is not waiting. A group with no running task beneath it is neither.
- <xref:Dagline.NodeSnapshot.Ran> is how long a task ran: its settle time less its start time. It is null for a task that has not settled, a blocked task, an earlier success and a group, none of which has both times.
- <xref:Dagline.NodeStatusExtensions.IsSettled*> says whether a status is settled: succeeded, failed, blocked or cancelled.

A group's counts, warnings and waiting are kept up to date as the tasks beneath it change: each snapshot shows them as of its own instant, whether the change was an event (a task changing status, beginning or ending a wait, or adding a warning that is kept) or not (a warning beyond the 100 kept). Progress never changes a group.

A task's <xref:Dagline.NodeSnapshot.Message> and <xref:Dagline.NodeSnapshot.WaitingText> are each one line, and null when the task gave empty text, so a reporter tests each only for null. <xref:Dagline.NodeSnapshot.StatusText> is null when the task gave empty text too, but is kept as the task gave it otherwise, line breaks included: a reporter that prints it on one line replaces each line break itself. Making it one line as the task reports it was measured to slow a task that reports progress in a tight loop, so it is left to the reporter.

### A run on a journal

A run that continues a [journal](journals.md) begins with its earlier successes already succeeded. They are in the first snapshot, the one handed with `RunStarted`, and no event is ever published for them, so an event always means something happened in this run. What the snapshot says about the journal is fixed for the run:

- <xref:Dagline.Snapshot.Journal> is a <xref:Dagline.JournalInfo> that names the journal: its id, its description and which run of it this is, counting from 1. It is null for a run with `Journal.None`, and a new journal's id is known only from here.
- <xref:Dagline.Snapshot.EarlierSuccesses> lists the earlier successes, in declaration order, and `EarlierSuccesses` on the run's <xref:Dagline.Snapshot.Counts> counts them. They are among the succeeded, and <xref:Dagline.NodeSnapshot.IsEarlierSuccess> marks each one. A group's counts give those beneath it, and `GroupSettled` carries the same counts, so a reporter can show how much this run did.
- <xref:Dagline.Snapshot.SetAside> lists the tasks whose earlier success this run set aside, in declaration order. They start pending and run again.
- <xref:Dagline.Snapshot.JournalPathsNotInGraph> counts the paths the journal holds that this graph does not have as a task, and <xref:Dagline.Snapshot.JournalPathsNotInGraphUnsucceeded> how many of those have not succeeded.

An earlier success keeps its whole outcome, warnings included, so `WarningCount` and `WithWarnings` cover every task in the graph. A reporter that lists warnings in its summary can tell those of earlier runs apart by `IsEarlierSuccess`.

A run whose journal can no longer be written is stopped by Dagline: `StopRequested` has `By` set to `StopRequester.Dagline`, unless the app had already asked for a stop, whose `StopRequested` stays the only one, with `By` set to `StopRequester.App`. Either way `RunEnded` carries the `JournalWriteException` in `Error` with no run outcome. The final snapshot has an end time and no `Outcome`.

## What a reporter may rely on

- **One order.** Events arrive in the order they happened, causes before effects, and that order is the same for every reporter. `RunStarted` is the first event and `RunEnded` is the last.
- **One call at a time.** A reporter is never called for an event while it is still handling an earlier one. The next call is made only once the `ValueTask` returned by the last one has completed, so a reporter needs no lock for what it does with events. Calls come from the thread pool, and not always from the same thread.
- **The snapshot matches the event.** The snapshot handed with an event is the run exactly as of that event, even when the reporter is handed the event long after it happened.
- **The run never waits for a reporter while tasks are running.** Each reporter has a queue of its own. A slow reporter falls behind, and neither the tasks nor the other reporters are held up by it. The queue has no size limit, and the number of events in a run is in proportion to its tasks, so that is all a reporter that falls behind can cost in memory.
- **A reporter that throws is dropped.** It receives no further call, and the tasks, the run outcome and the other reporters are unaffected. The error is returned with the result of the run and is never thrown at your app: see [When a reporter fails](#when-a-reporter-fails).
- **The run waits for reporters at its end.** `Runner.RunAsync` returns only once every reporter that was not dropped has handled every event, `RunEnded` included, so the last lines of a run are always printed before your app exits. That wait has no time bound, but a stop ends it: see [A stop ends the wait for reporters](#a-stop-ends-the-wait-for-reporters). With no stop request, a call to a reporter that never completes keeps `RunAsync` from returning.

The list of reporters is read once, when the run starts, and no reporter joins later. The cancellation token passed to `OnEventAsync` is cancelled only when a stop ends the wait for a reporter that is still busy.

## Writing a reporter

Implement <xref:Dagline.IReporter>. It has one method, which is handed each event with its snapshot:

```csharp
using Dagline;

public sealed class SettleLineReporter(TextWriter writer) : IReporter
{
    public async ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken)
    {
        switch (runEvent)
        {
            case TaskStatusChanged { To: NodeStatus.Succeeded } changed:
                var succeeded = snapshot.Counts[NodeStatus.Succeeded];
                await writer.WriteLineAsync($"{changed.At:HH:mm:ss} {changed.Node.Path} succeeded, {succeeded} so far");
                break;

            case RunEnded ended:
                await writer.WriteLineAsync($"{ended.At:HH:mm:ss} run ended: {ended.Outcome}");
                break;
        }
    }
}
```

Give it to the run in the options:

```csharp
var result = await Runner.RunAsync(graph, new RunOptions
{
    Journal = Journal.None,
    Reporters = [new SettleLineReporter(Console.Out)],
});
```

Things to keep in mind:

- **Ignore the events you do not show.** A reporter is handed every event. Return `ValueTask.CompletedTask`, or fall out of the `switch`, for the rest.
- **Explain an event from the event.** The line about a task that settled comes from the `Node` on the event, and totals beside it come from the snapshot handed with it. The snapshot source may already be ahead, so reading it to explain an event can describe a later moment.
- **Slow output is fine.** A reporter that awaits a slow writer delays only itself. The one place it is felt is the end of the run, which waits until the reporter has caught up.
- **Take times from the events.** Each event has the time it happened on the run's clock. The time at which the reporter is called may be later.
- **Catch what you can recover from.** An exception that leaves `OnEventAsync` drops the reporter for the rest of the run. A reporter that would rather skip one line than go silent catches that error itself.
- **One reporter, one run at a time.** A reporter that keeps state between events, such as the snapshot source, should be given to one run at a time, or reset its state on `RunStarted`. Listing one reporter twice in the options makes it two reporters: it is handed every event twice, and the two sets of calls can overlap.

### When a reporter fails

A reporter that throws from `OnEventAsync`, or returns a `ValueTask` that faults, is dropped, and the run carries on. <xref:Dagline.RunResult.ReporterErrors> has one <xref:Dagline.ReporterError> for each reporter that was dropped, in the order the reporters were given:

```csharp
foreach (var error in result.ReporterErrors)
{
    Console.Error.WriteLine($"{error.Reporter.GetType().Name} failed on {error.Event}: {error.Exception.Message}");
}
```

Each entry names the reporter, the event it was handling and the exception. This holds for every event, the last one included: a reporter that throws while handling `RunEnded` is on the list, and the run still returns its result.

The run outcome says how the work went and is not changed by a reporter's failure. Whether a run whose display broke should exit with an error is your app's decision.

### A stop ends the wait for reporters

A stuck reporter cannot hang your process. Once the run has ended, any request on its <xref:Dagline.StopSignal> ends the wait for reporters: the first request, when every task had already ended, or a later one, when the run had already been stopped. A reporter still busy at that moment is dropped like one that threw:

- the `CancellationToken` passed to its `OnEventAsync` is cancelled. Each reporter has a token of its own, never cancelled otherwise, so a reporter that awaits slow output can pass it on. It is already cancelled when `RunAsync` returns, but a callback you register on it runs on the thread pool and the run does not wait for it;
- its remaining events are discarded, and it gets no further call;
- `ReporterErrors` has a `ReporterError` naming it, the event it was handling and an `OperationCanceledException`;
- `RunAsync` returns without waiting for its call to come back, even a call that blocks its thread and never returns a task. Whatever that call does later is ignored.

A reporter that had already handled every event is unaffected and is not on the list. A request made once the tasks have ended never changes the run outcome. A request made just as the run ends is never lost: either the run applies it as a stop, or it ends the wait for reporters. A run given no signal waits for its reporters with no time bound.

## Progress on a timer: the snapshot source

Some of what a reporter shows is not tied to one event: how many tasks are running at this moment, or a line every few seconds so that a quiet stretch does not look like a hang. For that, keep the <xref:Dagline.ISnapshotSource> that `RunStarted` carries and read `Current` whenever you want to draw:

```csharp
using Dagline;

public sealed class TickingReporter(TextWriter writer, TimeProvider timeProvider) : IReporter
{
    private readonly CancellationTokenSource _runEnded = new();
    private Task _ticking = Task.CompletedTask;

    public async ValueTask OnEventAsync(RunEvent runEvent, Snapshot snapshot, CancellationToken cancellationToken)
    {
        switch (runEvent)
        {
            case RunStarted started:
                _ticking = TickAsync(started.Source);
                break;

            case RunEnded:
                await _runEnded.CancelAsync();
                await _ticking;
                _runEnded.Dispose();
                await writer.WriteLineAsync($"ended: {Describe(snapshot)}");
                break;
        }
    }

    private async Task TickAsync(ISnapshotSource source)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10), timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(_runEnded.Token))
            {
                await writer.WriteLineAsync(Describe(source.Current));
            }
        }
        catch (OperationCanceledException)
        {
            // The run ended: stop ticking.
        }
    }

    private static string Describe(Snapshot snapshot)
        => $"{snapshot.Counts[NodeStatus.Running]} running, {snapshot.Counts[NodeStatus.Succeeded]} succeeded";
}
```

What to know about the source:

- **`Current` is the run as it is now.** It may be ahead of the events the reporter has been handed so far: a reporter still handling `RunStarted` can read a `Current` in which every task has already succeeded.
- **Each read is one consistent snapshot.** Read `Current` once per draw and take everything from that one snapshot. Two reads may give two different instants.
- **Reading never waits for a task or a reporter**, and is safe from any thread.
- **Progress is here and nowhere else.** A running task's <xref:Dagline.NodeSnapshot.StatusText>, <xref:Dagline.NodeSnapshot.Count> and <xref:Dagline.NodeSnapshot.Total> are as the task last reported them. Once the task has settled they stay as it left them.
- **So is how long a task has waited.** While a task waits, <xref:Dagline.NodeSnapshot.WaitingSince> is when the wait began and <xref:Dagline.NodeSnapshot.WaitingText> what it waits for. Both are null while the task is working and once it has settled: the marker is live only.
- **So is the time left after a stop.** <xref:Dagline.Snapshot.Stop> is null until a stop is applied. From the snapshot handed with `StopRequested` on, it is a <xref:Dagline.StopState>: when the stop was applied, the grace period, the `Deadline` by which the run stops waiting at the latest, or null when the grace period is unlimited, and `By`, who asked for the stop. Since `Current` may be ahead of the events, a display reads `By` from the snapshot rather than waiting for `StopRequested`. A display can count down to the deadline on its own timer, with no event for each second.
- **After the run has ended** `Current` is the final snapshot: the same one that is handed with `RunEnded` and returned on <xref:Dagline.RunResult.Snapshot>.

A timer is the reporter's own concurrency. Dagline makes one call at a time into `OnEventAsync`, but it knows nothing of a loop the reporter started, so the reporter keeps the two apart itself. The example writes from `OnEventAsync` only after its loop has stopped. It also awaits the loop while handling `RunEnded`, so an exception from the loop leaves `OnEventAsync` and is reported like any other reporter error.

Pass the timer the same `TimeProvider` as the run, so that a test with a fake clock drives both.

## The plain-text reporter

<xref:Dagline.PlainTextReporter> prints a run as lines of text: one timestamped line per thing that happened, and a summary at the end. It is the reporter to use when output is redirected to a file or shown by a build server. Its output is for people: the wording and layout may change in any release, so do not parse it, and write a reporter of your own when a program needs the run (see [Compatibility](compatibility.md)). Give it the writer to print to:

```csharp
var result = await Runner.RunAsync(graph, new RunOptions
{
    Journal = Journal.None,
    Reporters = [new PlainTextReporter(Console.Out)],
});
```

It writes to that writer and to nothing else, and never closes it.

Take a group `checks` that holds the tasks `a` and `b`, a task `publish` that requires `checks`, a task `notify` that requires `publish`, and a task `report` that runs after `checks`. When `b` fails, the run prints:

```text
2026-11-11T04:12:00Z run started: 5 tasks
2026-11-11T04:12:00Z started checks/a
2026-11-11T04:12:00Z started checks/b
2026-11-11T04:12:02Z warning checks/a: entity 9 has no region
2026-11-11T04:12:03Z failed checks/b in 3.1s: 2 bad entities
    entity 3: missing owner
    entity 17: bad date
2026-11-11T04:12:03Z blocked 2 tasks by checks/b
2026-11-11T04:12:05Z succeeded checks/a in 5.0s: checked 40 entities
2026-11-11T04:12:05Z group checks failed: 1 succeeded, 1 failed
2026-11-11T04:12:05Z started report
2026-11-11T04:13:05Z tasks: 1 running, 1 succeeded, 1 failed, 2 blocked
2026-11-11T04:13:20Z succeeded report in 1m15s
2026-11-11T04:13:20Z run ended: incomplete in 1m20s
tasks: 2 succeeded, 1 failed, 2 blocked
failed: 1
  checks/b (reported) at 2026-11-11T04:12:03Z after 3.1s
    progress: checking entities, 17 of 40
    message: 2 bad entities
    detail:
      entity 3: missing owner
      entity 17: bad date
    blocked 2 tasks: publish, notify
warnings: 1
  checks/a
    entity 9 has no region
```

A stopped run prints the stop, the wait for its running tasks and each task the stop cancelled. Take a task `a`; a task `job` that requires `a` and waits on an external job; a task `slow` that ignores the stop; a task `tidy` that requires `job`; and a task `report` that runs after `job`. With the default grace period of 30 seconds, a stop while `job` waits prints:

```text
2026-11-11T04:12:00Z run started: 5 tasks
2026-11-11T04:12:00Z started a
2026-11-11T04:12:00Z started slow
2026-11-11T04:12:01Z succeeded a in 1.0s
2026-11-11T04:12:01Z started job
2026-11-11T04:12:02Z waiting job: job 17 to finish
2026-11-11T04:13:02Z tasks: 2 pending, 2 running, 1 succeeded
2026-11-11T04:13:40Z stop requested: waiting up to 30.0s for 2 running tasks
2026-11-11T04:13:40Z waited job for 1m38s
2026-11-11T04:13:40Z cancelled job in 1m39s
2026-11-11T04:14:10Z stopped waiting: grace period over, 1 task still running
2026-11-11T04:14:10Z cancelled slow in 2m10s: still running when the run stopped waiting
2026-11-11T04:14:10Z run ended: stopped in 2m10s
tasks: 1 pending, 1 ready, 1 succeeded, 2 cancelled
cancelled: 2
  job at 2026-11-11T04:13:40Z after 1m39s
  slow at 2026-11-11T04:14:10Z after 2m10s
    message: still running when the run stopped waiting
```

`job` ended because of the stop. `slow` was still running when the grace period was over, so the run let go of it. `tidy` and `report` were never reached: `tidy` stays pending, since a cancelled task never blocks, and `report` became ready and was not started.

A run on a [journal](journals.md) names it, and says what it kept from earlier runs. Take a first run with the description `all shards` and the tasks `a` (it succeeded with a warning), `b` (it failed), `report` (after `b`; it succeeded), `old` (it failed) and `gone` (it succeeded). A second run continues it with a graph of `a`, `b` and `report`, and `b` now succeeds. `a` is an earlier success, `report` is set aside because `b` runs again, and `old` and `gone` are journal paths the graph no longer has, one of them not succeeded. The second run prints:

```text
2026-11-11T05:00:00Z run started: 3 tasks, 1 earlier success, 1 set aside, 2 journal paths not in graph (1 not succeeded)
2026-11-11T05:00:00Z journal 20261111T041200Z-a3f9c2 run 2: all shards
2026-11-11T05:00:00Z started b
2026-11-11T05:00:04Z succeeded b in 4.0s
2026-11-11T05:00:04Z started report
2026-11-11T05:00:05Z succeeded report in 1.0s: 2 of 2 checks passed
2026-11-11T05:00:05Z run ended: complete in 5.0s
journal: 20261111T041200Z-a3f9c2 run 2: all shards
tasks: 3 succeeded (1 earlier)
warnings: 1
  from earlier runs, not listed again: 1 on 1 task
set aside: 1
  report
journal paths not in graph: 2 (1 not succeeded)
```

A run with `Journal.None` prints neither the journal line nor any of these parts.

### The lines of a run

Every line about an event begins with the time of the event, read from the run's clock.

| Line | When |
| --- | --- |
| `run started: <n> tasks` | The run starts. On a journal it goes on, each only when it is not zero, with `, <n> earlier successes`, `, <n> set aside` and `, <n> journal paths not in graph (<m> not succeeded)`. |
| `journal <id> run <n>: <description>` | The run starts on a journal, just after `run started`. With no description it ends after the run number. |
| `started <path>` | A task starts running. |
| `succeeded <path> in <duration>: <message>` | A task succeeds. |
| `failed <path> in <duration>: <message>` | A task fails with a reported failure. Its detail lines follow, indented. |
| `failed <path> in <duration> (unexpected): <message>` | A task fails with an unexpected failure. Its detail lines follow, indented: the type of the exception and its stack trace. |
| `blocked <n> tasks by <path>` | A failure blocked tasks. The path is that of the failed task. |
| `cancelled <path> in <duration>: <message>` | A task is cancelled: it ended because of a stop, or was still running when the run stopped waiting. |
| `warning <path>: <warning>` | A task adds a warning that is kept. |
| `waiting <path>: <text>` | A task begins waiting on an external system. With no text it reads `waiting <path>`. |
| `waited <path> for <duration>` | A task stops waiting. The duration is the whole wait. |
| `stop requested: waiting up to <duration> for <n> running tasks` | The run applies a stop. With an unlimited grace period it reads `stop requested: waiting for <n> running tasks`. A stop that Dagline asked for, because the journal could no longer be written, begins `stop requested by Dagline, journal write failed:` and goes on the same way. |
| `stopped waiting: grace period over, <n> tasks still running` | The grace period is over with tasks still running. A cancelled line for each follows. |
| `stopped waiting: second stop, <n> tasks still running` | A second stop ends the wait with tasks still running. A cancelled line for each follows. |
| `group <path> <status>: <counts>` | The last task beneath a group settles. |
| `tasks: <counts>` | Nothing has been printed for a minute. |
| `run ended: <run outcome> in <duration>` | The run ends. The summary follows. |
| `run ended: journal write failed after <duration>: <message>` | The run ends with an error because its journal could no longer be written. The message is that of the cause. The summary follows. |

What to know about them:

- **A settle with no message ends after the duration**, as `succeeded report in 1m15s` and `cancelled job in 1m39s` do. The detail lines of a task that succeeded or was cancelled are not printed. A task the run let go of has the message `still running when the run stopped waiting`.
- **One task reads in the singular**: `1 running task` and `1 task still running`.
- **The text of a wait is one line.** A line break in the text the task gave is printed as a space.
- **A task that becomes ready is not printed, and neither is progress.** The log has one line when a task starts and one when it settles.
- **Paths are printed and titles are not**, so a line can be found by the path your code declared.
- **One line for a whole cascade.** Consecutive blocked events that name the same failed task are counted into one line, and a single task reads `blocked 1 task by <path>`. The reporter cannot know that the last of them has arrived until something else happens, so the line is printed just before the next line of any other kind. It still carries the time at which the tasks were blocked.
- **A line of counts after a quiet minute.** One minute after the last line it printed, while the run has not ended, the reporter prints the count of tasks per status as the run is at that moment, so that a quiet hour does not look like a hang. It prints another after each further quiet minute, and none once the run has ended.

### The formats

- **Times** are in UTC, as `2026-11-11T04:12:03Z`, whatever the time zone of the machine.
- **Numbers** are written in the invariant culture with no grouping: `1234567`.
- **A duration** is `3.1s` under a minute, `1m15s` under an hour and `2h14m` from there, always rounded down.
- **A list of counts** names each status that has at least one task, in the order pending, ready, running, succeeded, failed, blocked, cancelled. When there are no tasks at all it reads `no tasks`. Earlier successes are among the succeeded, and follow that count in brackets when there are any, as in `3 succeeded (1 earlier)`: in the summary, on the line of counts and on a group's line.
- **Lines that were not kept.** Dagline keeps the first 100 detail lines and the first 100 warnings of a task. Where there were more, a line such as `and 3 more` follows the ones that were kept, at the same indent.

### The summary

The summary follows the `run ended` line and has no timestamps. A section with nothing in it is left out, so a complete run with no journal and no warnings ends with the counts alone.

1. **The journal**: on a run with a journal, `journal:` and the same text as the line after `run started`.
2. **The counts**: `tasks:` and the count of tasks per status. The tasks a stopped run never reached are counted here as pending and ready, and are not listed anywhere else.
3. **The failures**: `failed:` and the number of failed tasks, then each of them in declaration order. The first line of a failure has the path, the kind of failure (`reported` or `unexpected`), when the task failed and how long it had run. Beneath it, each only when there is something to show:
   - `progress:` the progress the task had reported when it failed. It reads `<text>`, `<count>`, `<count> of <total>`, or the text and the count joined by a comma. It is always one line: a line break in the status text is printed as a space.
   - `message:` its message.
   - `detail:` its detail lines, one to a line.
   - `blocked <n> tasks:` what the failure blocked, named by its roots: the highest nodes whose every task it blocked. A failure that blocked every task beneath a group names that one group.
4. **The cancelled tasks**: `cancelled:` and the number of cancelled tasks, then each of them in declaration order, in the shape of a failure without its kind: the path, when the task was cancelled and how long it had run, then its `progress:`, `message:` and `detail:` lines, each only when there is something to show. A cancelled task blocks nothing, so it has no `blocked` line.
5. **The warnings**: `warnings:` and the number of warnings of every task in the graph, kept or not, then each task that added any in this run, in declaration order, with the warnings that were kept beneath it. The warnings of earlier successes were listed by the run that added them, so they are one line after those: `from earlier runs, not listed again: <n> on <m> tasks`. They stay on each node in the snapshot.
6. **What is left**: on a run with a journal, `left for a later run: <n> tasks`, the tasks that have not succeeded. A complete run has none.
7. **The set-asides**: `set aside:` and the number of tasks whose earlier success was set aside, then each path.
8. **The journal paths not in the graph**: `journal paths not in graph: <n> (<m> not succeeded)`.

### The writer and the clock

All the lines about one event are written with one call to the writer, <xref:System.IO.TextWriter.Write(System.String)>, which is then flushed. The reporter's own writes never overlap: the line of counts comes from a timer, and it waits for a write that is in progress. Writes are synchronous.

That is all the reporter can promise, because it guards only its own calls. Whether a line your app writes to the same writer can land in the middle of a failure's detail lines or of the summary depends on the writer:

- **A synchronized writer keeps the lines of one event together.** It lets one call in at a time, so the reporter's one call is written whole. `Console.Out` and `Console.Error` are synchronized. `TextWriter.Synchronized(writer)` wraps any other writer: give the wrapper to the reporter and write through the same wrapper everywhere else in your app.
- **Any other writer does not.** A `StreamWriter` or a `StringWriter` is not safe to call from two threads at once, so sharing one with the reporter can mix the two outputs or corrupt the writer. Wrap it as above, or give the reporter a writer that nothing else writes to while the run is going.

The quiet minute is measured on a clock of the reporter's own, which is the system clock unless you pass another. That clock drives only the line of counts, and gives it its time. In a test, pass the reporter the same fake clock as the run:

```csharp
var clock = new FakeTimeProvider();
var writer = new StringWriter();

var result = await Runner.RunAsync(graph, new RunOptions
{
    Journal = Journal.None,
    TimeProvider = clock,
    Reporters = [new PlainTextReporter(writer, clock)],
});
```

When the writer throws, the plain-text reporter is dropped like any other reporter and prints nothing more, the line of counts included. The error is on <xref:Dagline.RunResult.ReporterErrors>: see [When a reporter fails](#when-a-reporter-fails). An error from writing a line of counts stops that line at once and is reported with the next event of the run.

Give one plain-text reporter to one run at a time.
