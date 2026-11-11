# Reporters

A reporter is something that presents a run to people while it happens: lines in a log, a display on a terminal, a message to a chat channel. Dagline itself never writes to the console. What your app shows of a run is up to the reporters you give it in <xref:Dagline.RunOptions.Reporters>.

A reporter watches a run and cannot change it. Nothing a reporter does, or fails to do, changes a task or the run outcome.

The `Dagline` package ships one reporter, [the plain-text reporter](#the-plain-text-reporter), which prints a run as timestamped lines. The sections before it are for writing your own.

> [!NOTE]
> While a display is up, your app's own logging goes to a file and not to the console: see [Terminal UI](terminal-ui.md).

## Events and the snapshot

Dagline gives a reporter two things.

An **event** is a record of one thing that happened in the run, with the time it happened, read from the run's clock. Every event derives from <xref:Dagline.RunEvent>, and the set of kinds is closed:

| Event | When | What it carries |
| --- | --- | --- |
| <xref:Dagline.RunStarted> | First, once per run. | The snapshot source of the run. |
| <xref:Dagline.TaskStatusChanged> | Every time a task changes status, pending to ready included. | The task as it is after the change, and the status before and after. |
| <xref:Dagline.WarningAdded> | When a running task adds a warning that is kept: the first 100 of each task. | The path of the task and the warning. |
| <xref:Dagline.WaitingChanged> | When a running task begins waiting on an external system and when it stops: once each per wait, however many waiting scopes overlap. | The path of the task, whether it is now waiting, the text saying what it waits for, and when the wait began. |
| <xref:Dagline.GroupSettled> | When the last task beneath a group settles, after that task's event and deepest group first. Never for a group with no tasks beneath it. | The path of the group, its status and its count of tasks per status. |
| <xref:Dagline.RunEnded> | Last, once per run. | The run outcome. |

An event carries everything needed to print a line about it, so a reporter that prints a line per event looks nothing up.

Nothing an event carries is ever null, except the text of a wait the task gave none for. The event types are records with public constructors, and each rejects a null argument with `ArgumentNullException`, when it is made and when it is copied by a `with` expression. <xref:Dagline.ReporterError>, <xref:Dagline.BlockedTasks> and <xref:Dagline.GraphProblem> do the same.

### The order within one step

A change and everything that follows from it at once are one step. The events of a step all have the same time and come in a fixed order, causes before effects:

1. The change itself: the run starting, or a task settling.
2. Every task that this blocks, pending to blocked.
3. Every task whose dependencies are now all met, pending to ready.
4. Every group that is now settled, deepest first.
5. Every task that now starts, ready to running.

Each list is in declaration order. The steps of a run never overlap, so the events of one are never mixed with those of another.

A warning is a step of its own, and so is a task beginning or ending a wait. A task that settles while it is still waiting ends its wait in the step in which it settles: the `WaitingChanged` that ends it comes immediately before that task's `TaskStatusChanged`, so a settled task is never waiting.

### Waiting is a marker

A task that triggers work in an external system and then waits for it opens a waiting scope with <xref:Dagline.TaskContext.Waiting(System.String)>. While a scope is open the task is **waiting**: it is idle, not working. Waiting is never a status. The task is running throughout, it keeps its slots, and its group and the run outcome are what they would be without the marker. A reporter uses it only to show idle tasks apart from working ones.

A wait begins when the task's first open scope opens and ends when its last one closes, so `WaitingChanged` comes in pairs: one with `Waiting` true and `Since` equal to `At`, then one with `Waiting` false and the same `Text` and `Since`. Overlapping scopes make one wait with the text and time of the scope that opened first, and a task that waits twice in a run gives two pairs.

A warning is a step of its own, with one event. A warning a task adds before its body ends always comes before the event of that task settling.

Progress is never an event. The status text and count a task reports change only the snapshot, so a task that reports often costs a reporter nothing.

A failure is the step with the most consequences. Take a group `checks` that holds the task `verify`, a task `publish` that requires `checks`, and a task `report` that runs after `checks`. When `verify` fails, a reporter receives:

```text
checks/verify: running -> failed
publish: pending -> blocked
report: pending -> ready
group checks settled: failed
report: ready -> running
```

A blocked task gets one event, pending to blocked, and its `Node` names the failed task in <xref:Dagline.NodeSnapshot.BlockedBy>. A reporter that would rather print one line for a whole cascade than one per task can group consecutive blocked events by that path, as the plain-text reporter does.

A **snapshot** is everything Dagline knows about the run at one instant: every node in declaration order with its status and times, the count of tasks per status, and how full the cap and each limit are. A <xref:Dagline.Snapshot> is immutable, so a reporter can keep one, compare two, or hand one to another thread.

A reporter gets snapshots in two ways:

- **With each event.** The snapshot handed to <xref:Dagline.IReporter.OnEventAsync*> is the run as of that event. It includes that event and none after it, however far the run has moved on since. Use it to show totals beside the event.
- **From the snapshot source.** <xref:Dagline.RunStarted> carries an <xref:Dagline.ISnapshotSource>, and its `Current` is the run as it is now. Use it to show the run on a timer of your own.

Dagline keeps the snapshot, so a reporter never pieces it together from events, and two reporters never disagree about it.

A snapshot also answers the questions a summary asks, each in declaration order, so every reporter's summary agrees:

- <xref:Dagline.Snapshot.Failed> lists the failed tasks. Each has its <xref:Dagline.NodeSnapshot.Failure> kind, its message and its detail lines.
- <xref:Dagline.Snapshot.WarningCount> counts every warning added, and <xref:Dagline.Snapshot.WithWarnings> lists the tasks that have any. Each task keeps its first 100 warnings in <xref:Dagline.NodeSnapshot.Warnings> and counts the rest in <xref:Dagline.NodeSnapshot.WarningsOmitted>.
- <xref:Dagline.Snapshot.Blocked> has one <xref:Dagline.BlockedTasks> for each failed task that blocked anything: the tasks it blocked and the *roots*, the highest nodes whose every task it blocked. Name the roots and count the tasks, and a failure that blocked a thousand tasks beneath one group is one short line.

## What a reporter may rely on

- **One order.** Events arrive in the order they happened, causes before effects, and that order is the same for every reporter. `RunStarted` is the first event and `RunEnded` is the last.
- **One call at a time.** A reporter is never called for an event while it is still handling an earlier one. The next call is made only once the `ValueTask` returned by the last one has completed, so a reporter needs no lock for what it does with events. Calls come from the thread pool, and not always from the same thread.
- **The snapshot matches the event.** The snapshot handed with an event is the run exactly as of that event, even when the reporter is handed the event long after it happened.
- **The run never waits for a reporter while tasks are running.** Each reporter has a queue of its own. A slow reporter falls behind, and neither the tasks nor the other reporters are held up by it. The queue has no size limit, and the number of events in a run is in proportion to its tasks, so that is all a reporter that falls behind can cost in memory.
- **A reporter that throws is dropped.** It receives no further call, and the tasks, the run outcome and the other reporters are unaffected. The error is returned with the result of the run and is never thrown at your app: see [When a reporter fails](#when-a-reporter-fails).
- **The run waits for reporters at its end.** `Runner.RunAsync` returns only once every reporter that was not dropped has handled every event, `RunEnded` included, so the last lines of a run are always printed before your app exits. That wait has no time bound, but a stop ends it: see [A stop ends the wait for reporters](#a-stop-ends-the-wait-for-reporters). With no stop request, a call to a reporter that never completes keeps `RunAsync` from returning.

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

### A stop ends the wait for reporters

A stuck reporter cannot hang your process. Once the run has ended, any request on its <xref:Dagline.StopSignal> ends the wait for reporters: the first request, when every task had already ended, or a later one, when the run had already been stopped. A reporter still busy at that moment is dropped like one that threw:

- the `CancellationToken` passed to its `OnEventAsync` is cancelled. That token is never cancelled otherwise, so a reporter that awaits slow output can pass it on;
- its remaining events are discarded, and it gets no further call;
- `ReporterErrors` has a `ReporterError` naming it, the event it was handling and an `OperationCanceledException`;
- `RunAsync` returns without waiting for its call to come back. Whatever that call does later is ignored.

A reporter that had already handled every event is unaffected and is not on the list. A request made once the tasks have ended never changes the run outcome. A run given no signal waits for its reporters with no time bound.

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
- **After the run has ended** `Current` is the final snapshot: the same one that is handed with `RunEnded` and returned on <xref:Dagline.RunResult.Snapshot>.

A timer is the reporter's own concurrency. Dagline makes one call at a time into `OnEventAsync`, but it knows nothing of a loop the reporter started, so the reporter keeps the two apart itself. The example writes from `OnEventAsync` only after its loop has stopped. It also awaits the loop while handling `RunEnded`, so an exception from the loop leaves `OnEventAsync` and is reported like any other reporter error.

Pass the timer the same `TimeProvider` as the run, so that a test with a fake clock drives both.

## The plain-text reporter

<xref:Dagline.PlainTextReporter> prints a run as lines of text: one timestamped line per thing that happened, and a summary at the end. It is the reporter to use when output is redirected to a file or shown by a build server. Give it the writer to print to:

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

### The lines of a run

Every line about an event begins with the time of the event, read from the run's clock.

| Line | When |
| --- | --- |
| `run started: <n> tasks` | The run starts. |
| `started <path>` | A task starts running. |
| `succeeded <path> in <duration>: <message>` | A task succeeds. |
| `failed <path> in <duration>: <message>` | A task fails with a reported failure. Its detail lines follow, indented. |
| `failed <path> in <duration> (unexpected): <message>` | A task fails with an unexpected failure. Its detail lines follow, indented: the type of the exception and its stack trace. |
| `blocked <n> tasks by <path>` | A failure blocked tasks. The path is that of the failed task. |
| `warning <path>: <warning>` | A task adds a warning that is kept. |
| `group <path> <status>: <counts>` | The last task beneath a group settles. |
| `tasks: <counts>` | Nothing has been printed for a minute. |
| `run ended: <run outcome> in <duration>` | The run ends. The summary follows. |

What to know about them:

- **A settle with no message ends after the duration**, as `succeeded report in 1m15s` does. The detail lines of a task that succeeded are not printed.
- **A task that becomes ready is not printed, and neither is progress.** The log has one line when a task starts and one when it settles.
- **Paths are printed and titles are not**, so a line can be found by the path your code declared.
- **One line for a whole cascade.** Consecutive blocked events that name the same failed task are counted into one line, and a single task reads `blocked 1 task by <path>`. The reporter cannot know that the last of them has arrived until something else happens, so the line is printed just before the next line of any other kind. It still carries the time at which the tasks were blocked.
- **A line of counts after a quiet minute.** One minute after the last line it printed, while the run has not ended, the reporter prints the count of tasks per status as the run is at that moment, so that a quiet hour does not look like a hang. It prints another after each further quiet minute, and none once the run has ended.

### The formats

- **Times** are in UTC, as `2026-11-11T04:12:03Z`, whatever the time zone of the machine.
- **Numbers** are written in the invariant culture with no grouping: `1234567`.
- **A duration** is `3.1s` under a minute, `1m15s` under an hour and `2h14m` from there, always rounded down.
- **A list of counts** names each status that has at least one task, in the order pending, ready, running, succeeded, failed, blocked, cancelled. When there are no tasks at all it reads `no tasks`.
- **Lines that were not kept.** Dagline keeps the first 100 detail lines and the first 100 warnings of a task. Where there were more, a line such as `and 3 more` follows the ones that were kept, at the same indent.

### The summary

The summary follows the `run ended` line and has no timestamps. It has up to three sections. A section with nothing in it is left out, so a complete run with no warnings ends with the counts alone.

1. **The counts**: `tasks:` and the count of tasks per status.
2. **The failures**: `failed:` and the number of failed tasks, then each of them in declaration order. The first line of a failure has the path, the kind of failure (`reported` or `unexpected`), when the task failed and how long it had run. Beneath it, each only when there is something to show:
   - `progress:` the progress the task had reported when it failed. It reads `<text>`, `<count>`, `<count> of <total>`, or the text and the count joined by a comma. It is always one line: a line break in the status text is printed as a space.
   - `message:` its message.
   - `detail:` its detail lines, one to a line.
   - `blocked <n> tasks:` what the failure blocked, named by its roots: the highest nodes whose every task it blocked. A failure that blocked every task beneath a group names that one group.
3. **The warnings**: `warnings:` and the number of warnings added in the run, kept or not, then each task that has any, in declaration order, with the warnings that were kept beneath it.

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
