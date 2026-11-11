# Reporters

A reporter is something that presents a run to people while it happens: lines in a log, a display on a terminal, a message to a chat channel. Dagline itself never writes to the console. What your app shows of a run is up to the reporters you give it in <xref:Dagline.RunOptions.Reporters>.

A reporter watches a run and cannot change it. Nothing a reporter does, or fails to do, changes a task or the run outcome.

> [!NOTE]
> While a display is up, your app's own logging goes to a file and not to the console: see [Terminal UI](terminal-ui.md).

## Events and the snapshot

Dagline gives a reporter two things.

An **event** is a record of one thing that happened in the run, with the time it happened, read from the run's clock. Every event derives from <xref:Dagline.RunEvent>, and the set of kinds is closed:

| Event | When | What it carries |
| --- | --- | --- |
| <xref:Dagline.RunStarted> | First, once per run. | The snapshot source of the run. |
| <xref:Dagline.TaskStatusChanged> | Every time a task changes status, pending to ready included. | The task as it is after the change, and the status before and after. |
| <xref:Dagline.GroupSettled> | When the last task beneath a group settles, after that task's event and deepest group first. Never for a group with no tasks beneath it. | The path of the group, its status and its count of tasks per status. |
| <xref:Dagline.RunEnded> | Last, once per run. | The run outcome. |

An event carries everything needed to print a line about it, so a reporter that prints a line per event looks nothing up.

### The order within one step

A change and everything that follows from it at once are one step. The events of a step all have the same time and come in a fixed order, causes before effects:

1. The change itself: the run starting, or a task settling.
2. Every task that this blocks, pending to blocked.
3. Every task whose dependencies are now all met, pending to ready.
4. Every group that is now settled, deepest first.
5. Every task that now starts, ready to running.

Each list is in declaration order. The steps of a run never overlap, so the events of one are never mixed with those of another.

A failure is the step with the most consequences. Take a group `checks` that holds the task `verify`, a task `publish` that requires `checks`, and a task `report` that runs after `checks`. When `verify` fails, a reporter receives:

```text
checks/verify: running -> failed
publish: pending -> blocked
report: pending -> ready
group checks settled: failed
report: ready -> running
```

A blocked task gets one event, pending to blocked, and its `Node` names the failed task in <xref:Dagline.NodeSnapshot.BlockedBy>. A reporter that would rather print one line for a whole cascade than one per task can group consecutive blocked events by that path.

A **snapshot** is everything Dagline knows about the run at one instant: every node in declaration order with its status and times, and the count of tasks per status. A <xref:Dagline.Snapshot> is immutable, so a reporter can keep one, compare two, or hand one to another thread.

A reporter gets snapshots in two ways:

- **With each event.** The snapshot handed to <xref:Dagline.IReporter.OnEventAsync*> is the run as of that event. It includes that event and none after it, however far the run has moved on since. Use it to show totals beside the event.
- **From the snapshot source.** <xref:Dagline.RunStarted> carries an <xref:Dagline.ISnapshotSource>, and its `Current` is the run as it is now. Use it to show the run on a timer of your own.

Dagline keeps the snapshot, so a reporter never rebuilds the state of the run from events, and two reporters never disagree about it.

A snapshot also answers the questions a summary asks, each in declaration order, so every reporter's summary agrees:

- <xref:Dagline.Snapshot.Failed> lists the failed tasks. Each has its <xref:Dagline.NodeSnapshot.Failure> kind, its message and its detail lines.
- <xref:Dagline.Snapshot.Blocked> has one <xref:Dagline.BlockedTasks> for each failed task that blocked anything: the tasks it blocked and the *roots*, the highest nodes whose every task it blocked. Name the roots and count the tasks, and a failure that blocked a thousand tasks beneath one group is one short line.

## What a reporter may rely on

- **One order.** Events arrive in the order they happened, causes before effects, and that order is the same for every reporter. `RunStarted` is the first event and `RunEnded` is the last.
- **One call at a time.** A reporter is never called for an event while it is still handling an earlier one. The next call is made only once the `ValueTask` returned by the last one has completed, so a reporter needs no lock for what it does with events. Calls come from the thread pool, and not always from the same thread.
- **The snapshot matches the event.** The snapshot handed with an event is the run exactly as of that event, even when the reporter is handed the event long after it happened.
- **The run never waits for a reporter while tasks are running.** Each reporter has a queue of its own. A slow reporter falls behind, and neither the tasks nor the other reporters are held up by it. The queue has no size limit, and the number of events in a run is in proportion to its tasks, so that is all a reporter that falls behind can cost in memory.
- **A reporter that throws is dropped.** It receives no further call, and the tasks, the run outcome and the other reporters are unaffected. The error is returned with the result of the run and is never thrown at your app: see [When a reporter fails](#when-a-reporter-fails).
- **The run waits for reporters at its end.** `Runner.RunAsync` returns only once every reporter that was not dropped has handled every event, `RunEnded` included, so the last lines of a run are always printed before your app exits. That wait has no time bound: a call to a reporter that never completes keeps `RunAsync` from returning.

The list of reporters is read once, when the run starts, and no reporter joins later. The cancellation token passed to `OnEventAsync` is never cancelled.

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
- **Reading never waits for the run**, and is safe from any thread.
- **After the run has ended** `Current` is the final snapshot: the same one that is handed with `RunEnded` and returned on <xref:Dagline.RunResult.Snapshot>.

A timer is the reporter's own concurrency. Dagline makes one call at a time into `OnEventAsync`, but it knows nothing of a loop the reporter started, so the reporter keeps the two apart itself. The example writes from `OnEventAsync` only after its loop has stopped. It also awaits the loop while handling `RunEnded`, so an exception from the loop leaves `OnEventAsync` and is reported like any other reporter error.

Pass the timer the same `TimeProvider` as the run, so that a test with a fake clock drives both.
