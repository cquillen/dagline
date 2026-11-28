# Testing your tasks

`Dagline.Testing` is a package for test projects only. It holds a harness that runs one task body and hands back what the task left behind, a scripted run that a test drives on its own thread one happening at a time, and a clock on which every finite delay ends at once, so a task that waits an hour in production takes milliseconds in a test. It depends on `Dagline` alone: no test framework, no assertion library and no terminal library, so it works with whatever your test project already uses.

```shell
dotnet add package Dagline.Testing
```

The examples below use Shouldly for assertions. Any assertion library does the same job. For a worked example on realistic tasks, [`samples/Dagline.Samples.Batch.Tests`](https://github.com/cquillen/dagline/tree/main/samples/Dagline.Samples.Batch.Tests) tests the task bodies of the `Batch` sample with the harness, a re-attach across two runs included, and uses nothing but the packages and a test framework.

## The harness and its outcome

<xref:Dagline.Testing.TaskHarness.For(Dagline.TaskBody,System.TimeProvider,System.String)> wraps one task body, and <xref:Dagline.Testing.TaskHarness.RunAsync*> runs it and returns a <xref:Dagline.Testing.TaskOutcome>:

```csharp
[Test]
public async Task An_empty_export_warns_and_succeeds()
{
    await using var harness = TaskHarness.For(context => Export(context, new FakeStore(rows: 0)));

    var outcome = await harness.RunAsync();

    outcome.Status.ShouldBe(NodeStatus.Succeeded);
    outcome.Warnings.ShouldBe(["nothing to export"]);
}
```

The outcome carries everything a task reports:

| Member | What it holds |
| --- | --- |
| `Status` | Succeeded, Failed or Cancelled. |
| `Failure` | `FailureKind.Reported` for a failure the task reported through `context.Fail`, `FailureKind.Unexpected` for an exception that escaped the body, and `null` unless the task failed. |
| `Message` | The task's one-line message, or the exception's message for an unexpected failure. |
| `Detail` | The detail lines that were kept. For an unexpected failure they include the exception's type and stack trace. |
| `Warnings` | The warnings that were kept, whatever the task settled with. |
| `StatusText` | The status text the task last set with `context.SetStatusText`, as the operator saw it, line breaks included. `null` when it set none or cleared it. |
| `Waited` | Whether the task began waiting at least once in this run. |
| `WaitingText` | The text of the last wait the task began in this run, on one line as the operator saw it, kept after the wait has ended. `null` when it began no wait, or that wait gave no text. |
| `Checkpoint` | The task's checkpoint as the run left it: the one a later run hands the task. Empty when it has none, cleared it or succeeded, since a success discards the checkpoint. |

It has no times, no count and total, and no counts of lines that were not kept.

So a test asserts what the operator would have seen, as well as how the task ended:

```csharp
[Test]
public async Task The_export_says_what_it_is_waiting_for()
{
    await using var harness = TaskHarness.For(context => Export(context, new FakeJobs(finishAfterChecks: 3)));

    var outcome = await harness.RunAsync();

    outcome.StatusText.ShouldBe("17 rows written");
    outcome.WaitingText.ShouldBe("job-1 to finish");
}
```

A run of the harness is a real run through Dagline's public API, a [scripted run](#scripting-a-whole-run) that the harness pumps inside the call: a graph of one task at the root, a journal in a new directory the harness creates under the system's temporary directory, named `dagline-harness-` and random characters (on Unix only you can open it), an unlimited grace period and reporter wait, and a reporter of the harness's own that watches it. The journal is [handed to the operating system](journals.md#durability) and never flushed to the disk, so a test waits on no flush. The body sees exactly what it would see in your app, so a test proves what a real run will do, with one difference: the body starts on the thread that calls the harness, under a synchronization context of the harness's own, so that its awaits come back to the harness. A body that blocks on its own asynchronous work, with `.Result` or `.Wait()`, or on something your test does only after the call returns, hangs under the harness, though it may not in an app. Nothing runs and nothing touches the disk until the first run. Disposing the harness deletes the directory. Disposing it twice is harmless, and a run after disposal throws `ObjectDisposedException`.

An exception the body throws never comes out of the harness: it is the task's unexpected failure and comes back as an outcome, the way a run sees it. An exception from the run itself, such as a journal that cannot be written, comes out of the call unchanged.

A harness does one run at a time and is not safe to call from two threads at once.

## The harness's clock and its guard

With no clock passed, every run of the harness is on a clock of the harness's own, on which time moves only when the harness fires a timer. Whenever nothing of the run is left to do, the harness fires the timer due first and moves the clock on to when it is due. From the body's point of view, every finite delay and timer ends at once, and a task that waits for hours finishes in milliseconds. A delay or timer with an infinite due time never ends. Two delays in flight at once move the clock to the later of the two, not by their sum, so a task that measures elapsed time sees the time that would really have passed. The harness makes this clock once and uses it for its whole life: it starts at midnight UTC on 1 January 2000, and a later run carries on from the time the earlier runs moved it to. Its local time zone is UTC, and time only moves forward. The clock is not public: the body sees it as `context.TimeProvider`.

A check that never returns `true` would go on for ever on such a clock, so the clock has the guard of an <xref:Dagline.Testing.InstantClock>. Once more than 7 days of fake time have passed, the body can make no delay or timer on it: making one throws <xref:Dagline.Testing.ClockGuardException>. That fails the task, the run ends, and the harness throws `ClockGuardException` in place of returning the outcome, so the test fails with a clear exception instead of hanging. The guard refuses only the body's timers, never the run's own, so the run always ends. One clock serves the whole life of a harness, so its guard counts fake time across runs.

A task that legitimately waits for longer gets an `InstantClock` with a bigger guard:

```csharp
await using var harness = TaskHarness.For(MonthEndReconcile, new InstantClock(guard: TimeSpan.FromDays(40)));
```

A clock passed to `For` is used as is, in every run, and the body sees it as `context.TimeProvider`. The harness never moves it and never fires its timers: it pumps the run until its result, waiting in real time whenever nothing is queued. An `InstantClock` fires its timers on the thread pool as soon as they are made, so its delays still end at once, but out of the harness's sight. The harness checks the guard of an `InstantClock` passed in, as it checks its own.

Whenever the body awaits something the harness cannot see, such as real I/O or work on the thread pool, the harness waits in real time, for as long as it takes, until the body comes back. A body that never ends keeps the call from returning, and your test framework's timeout is what fails the test.

## A waiting task without real waiting

A task that waits through <xref:Dagline.TaskContext.WaitUntilAsync(System.TimeSpan,System.Func{System.Threading.CancellationToken,System.Threading.Tasks.Task{System.Boolean}},System.String)> finishes without real waiting on the default clock:

```csharp
[Test]
public async Task The_export_waits_for_the_job_to_finish()
{
    var jobs = new FakeJobs(finishAfterChecks: 3);
    await using var harness = TaskHarness.For(context => Export(context, jobs));

    var outcome = await harness.RunAsync();

    outcome.Status.ShouldBe(NodeStatus.Succeeded);
    outcome.Waited.ShouldBeTrue();
}
```

## A re-attach test across two runs

A task that triggers work in an external system should save a checkpoint and, in a later run, re-attach to that work instead of triggering it twice. [Trigger or re-attach](waiting-on-external-systems.md#trigger-or-re-attach) shows the pattern. The harness tests it in a few lines, because every run after the first continues the harness's own journal, and the task gets back the checkpoint it saved.

<xref:Dagline.Testing.TaskHarness.RunUntilWaitingThenStopAsync*> ends a first run the way an operator would: it stops the run as soon as the task begins waiting and returns the outcome the run ends with. Run the harness again, and assert that the fake external system saw one trigger:

```csharp
[Test]
public async Task A_stopped_export_re_attaches_to_its_job()
{
    var jobs = new FakeJobs(finishAfterChecks: 3);
    await using var harness = TaskHarness.For(context => Export(context, jobs));

    var stopped = await harness.RunUntilWaitingThenStopAsync();
    var continued = await harness.RunAsync();

    stopped.Status.ShouldBe(NodeStatus.Cancelled);
    continued.Status.ShouldBe(NodeStatus.Succeeded);
    jobs.Submitted.ShouldBe(1);
}
```

`stopped.Checkpoint` is the checkpoint the stopped run left, so the test can also assert what the task saved: `stopped.Checkpoint.ShouldBe("job-1")`.

The same holds after a first run that ended failed: the next run hands the task its checkpoint. Once the task has succeeded, a further run does not call the body. It is an earlier success on the journal, so the run returns the same message, detail lines and warnings, with `Waited` false, no status text and no waiting text, since a journal keeps no progress and the task began no wait in that run. Its `Checkpoint` is empty, as the success left it.

## Starting from a checkpoint

To test how a task re-attaches without a first run to save a checkpoint, pass one to `For`. The task reads it back as `context.Checkpoint` in the first run, as if an earlier run had saved it:

```csharp
[Test]
public async Task An_export_handed_its_job_re_attaches_without_submitting()
{
    var jobs = new FakeJobs(finishAfterChecks: 3, existing: "job-1");
    await using var harness = TaskHarness.For(context => Export(context, jobs), checkpoint: "job-1");

    var outcome = await harness.RunAsync();

    outcome.Status.ShouldBe(NodeStatus.Succeeded);
    jobs.Submitted.ShouldBe(0);
}
```

The harness plants the checkpoint by saving it on the task's own context, through `SaveCheckpointAsync`, just before it calls the body, so the journal holds it as it would hold the task's own save, and the save publishes no event. It is planted in the first run only: a later run hands the task the checkpoint the run before it left, the one in that outcome's `Checkpoint`. A `null` or empty checkpoint plants none, since an absent checkpoint is empty text.

## A task's behaviour on a stop

`RunUntilWaitingThenStopAsync` also tests what a task does when it is stopped. A task that waits through the helper and does nothing else comes back `Cancelled`, with `Waited` true. A task that handles the stop itself, by catching the cancellation and reporting, comes back with the outcome it chose:

```csharp
var outcome = await harness.RunUntilWaitingThenStopAsync();

outcome.Status.ShouldBe(NodeStatus.Failed);
outcome.Message.ShouldBe("stopped before the export finished");
```

A task that settles without ever waiting returns its own outcome from the call, so a test of a fast path does not hang.

The harness pumps the run until nothing is queued. If the task is then waiting, as the run has applied it, the harness stops the run, once, with `Run.Stop`, and pumps it to its result. If not, it fires its clock's next timer and pumps again. A task is waiting inside `WaitUntilAsync` and inside a `BeginWaiting` scope alike. So:

- A body that delays before it waits, with a back-off before a first attempt, a settling pause after a trigger or a short retry loop around the trigger, reaches its wait with the clock moved by exactly those delays.
- The wait has made its first check when the stop arrives, and none of its delays has ended: the stop ends the one it is in.
- After the stop, timers fire again whenever nothing is queued, so a delay in cleanup after the stop ends.
- A wait that begins and ends before the run is next idle, such as one whose first check returns `true`, is never seen waiting, and the run goes on.

`WaitUntilAsync` resumes on the context the body runs on, so a stop of a task waiting in it stays on the harness's pump, as the rest of the body does.

A clock you pass to `For` is used as is here too, and the harness fires none of its timers. With an `InstantClock` passed, the call is a race by nature: the wait's checks run without delay on the thread pool, so the task may settle before the harness sees it waiting, and that outcome is the one returned.

Every run of the harness has an unlimited grace period, so the run never lets go of the task. A body that ignores the stop keeps the call from returning, and your test framework's timeout is what fails the test.

## Stopping a run with your token

`RunAsync` and `RunUntilWaitingThenStopAsync` each take a `CancellationToken`, passed straight through as the run's token. Cancelling it stops the run, as an operator would: the task's `context.CancellationToken` is signalled, the call returns the outcome the task ends with, and it never throws because of the cancellation. `RunUntilWaitingThenStopAsync` makes its own stop on the run itself, so either one stops the run. A token already cancelled starts no body: the task comes back ready, or as the earlier success it was.

So a test can stop a task at a moment of its own choosing, or pass its framework's timeout token so that a stuck test ends with the task's outcome:

```csharp
[Test]
public async Task A_load_stopped_while_it_runs_comes_back_cancelled()
{
    var loading = new TaskCompletionSource();
    await using var harness = TaskHarness.For(async context =>
    {
        loading.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, context.TimeProvider, context.CancellationToken);
    });
    using var stop = new CancellationTokenSource();

    var running = harness.RunAsync(stop.Token);
    await loading.Task;
    await stop.CancelAsync();
    var outcome = await running;

    outcome.Status.ShouldBe(NodeStatus.Cancelled);
}
```

The grace period is still unlimited: a body that ignores its token still keeps the call from returning.

## Write a task as a named method or factory

A test can reach a task body only if the body has a name. A lambda written inside the code that builds your graph cannot be handed to the harness. Write each task you want to test as a static method, or as a factory that takes the task's services and returns a body:

```csharp
public static class ExportTask
{
    public static TaskBody Create(IJobs jobs) => context => Run(context, jobs);

    public static async Task Run(TaskContext context, IJobs jobs)
    {
        // ...
    }
}

// In the graph-building code:
builder.Task("export", ExportTask.Create(jobs));

// In a test:
await using var harness = TaskHarness.For(ExportTask.Create(new FakeJobs(finishAfterChecks: 3)));
```

## Testing how a graph is wired

The harness runs one task. To test the graph itself (that a failure blocks what requires it, that an *after* task still runs, that a group settles as it should), run the real graph over fakes with an `InstantClock` and no journal, and assert with the snapshot's summary queries:

```csharp
[Test]
public async Task A_failed_export_blocks_the_report()
{
    var graph = BatchGraph.Build(new FakeJobs(failing: "export"));

    var result = await Runner.RunAsync(graph, new RunOptions
    {
        Journal = Journal.None,
        TimeProvider = new InstantClock(),
    });

    result.Outcome.ShouldBe(RunOutcome.Incomplete);
    result.Snapshot.Failed.Select(node => node.Path.ToString()).ShouldBe(["export"]);
    result.Snapshot.Blocked.Single().Tasks.Select(node => node.Path.ToString()).ShouldBe(["report"]);
}
```

`Failed`, `Blocked`, `Cancelled`, `NeverReached`, `EarlierSuccesses`, `WithWarnings` and `Counts` on <xref:Dagline.Snapshot> answer most questions about a whole run without a reporter.

### Do not hand an instant clock to a reporter

A periodic timer on an `InstantClock` fires again and again, moving time forward each time, until it is disposed, and the guard does not stop it, because the guard is checked only when a delay or timer is made. Anything that keeps a periodic timer for the life of a run would spin on it. Reporters do: the plain-text reporter and the terminal display both refresh on a timer. Give a reporter a clock of its own, such as `TimeProvider.System`, or leave reporters out of a test run.

### A graph run on the instant clock with reporters

The run measures its [reporter wait](reporters.md#the-reporter-wait) on its own clock, and on an `InstantClock` that timer fires as soon as the thread pool runs it, moving the clock forward by the wait. Whether a reporter is still busy by then is a race: one may be dropped, and be on `ReporterErrors`, and the clock may end the run the length of the wait further on. A run with no reporter, or whose reporters have all caught up as it ends, makes no such timer, so the clock does not move at the end of the run. To have every reporter handle every event, and keep the clock where the tasks left it, wait for the reporters with no limit:

```csharp
var result = await Runner.RunAsync(graph, new RunOptions
{
    Journal = Journal.None,
    TimeProvider = new InstantClock(),
    Reporters = [reporter],
    ReporterWait = Timeout.InfiniteTimeSpan,
});
```

Once the clock is past its guard it refuses to make the timer, and the run still returns its result: the wait runs out at once, and each reporter dropped then has the `ClockGuardException` as the inner exception of its error. The grace period after a stop is measured on the same clock and meets the same guard: on an `InstantClock` a limited grace period runs out as soon as the thread pool runs its timer, and once the clock is past its guard a stop with tasks still running ends the grace period at once, the run letting go of those tasks, rather than faulting the run.

## Scripting a whole run

<xref:Dagline.Testing.ScriptedRun> starts a whole graph as a run that nothing happens in until your test pumps it, on your test's own thread. The test makes each thing happen in the order it means, asserts between happenings, and runs once:

```csharp
[Test]
public void An_export_that_ends_before_the_stop_lets_the_report_start()
{
    var exported = new TaskCompletionSource();
    var builder = new GraphBuilder();
    var export = builder.Task("export", async _ => await exported.Task);
    var report = builder.Task("report", _ => Task.CompletedTask);
    report.Requires(export);

    var scripted = ScriptedRun.Start(builder.Build(), new RunOptions { Journal = Journal.None });
    scripted.Pump();

    exported.SetResult();
    scripted.PumpBodies();
    scripted.Run.Stop();
    scripted.PumpLoop();

    scripted.Run.Current[report.Path].Status.ShouldBe(NodeStatus.Running);
    scripted.Finish(TimeSpan.FromSeconds(10)).Outcome.ShouldBe(RunOutcome.Complete);
}
```

The export's end is posted before the stop, so the run applies it first and starts the report before it applies the stop. Both tasks succeed, so the run is complete though it was stopped. `Run` is the run as an app holds it: drain it, stop it, read its `Current` snapshot and its `Result`. A request only posts to the run, and the run applies it when the loop is next pumped. The run's work waits in three queues:

| Call | Runs |
| --- | --- |
| `Pump` | Everything queued, and everything that queues meanwhile, until nothing is. |
| `PumpLoop` | The loop's turns only: each applies every request, body end and report posted to the run, in the order posted. |
| `PumpBodies` | Body starts and what the bodies' awaits resume. |
| `PumpReporters` | The reporters' deliveries. |
| `PumpUntil`, `PumpUntilAsync` | Pumps until a condition on the current snapshot holds, waiting in real time, with a limit, whenever nothing is queued. |
| `Finish` | Pumps until the result, with a real-time limit, and returns it. |

When the options keep the default clock, `TimeProvider.System`, the scripted run puts a clock of its own in its place, starting at midnight UTC on 1 January 2000. Its time moves only when the test says: `Advance` moves it on, firing each timer that comes due on the way and pumping after each, and `FireNextTimer` fires the next timer alone and pumps nothing. The run's grace period and reporter wait are on that clock too. A clock passed in the options is used as is, and `Advance` and `FireNextTimer` refuse.

What a scripted run can promise, and what it cannot:

- Pumping until nothing is queued means the queues are empty, not that the run is waiting for the test.
- A body is exact only when everything it awaits is completed by the test, by the scripted run's clock or by the run, such as a checkpoint save or a stop. Anything else, such as real I/O or `Task.Run`, comes back to the pump at a moment it cannot foresee, and needs `PumpUntil`, in real time. An await with `ConfigureAwait(false)` on a delay that a stop cancels resumes on the thread pool, and leaves the pump.
- The instant clock fires its timers on the thread pool, so it cannot make a scripted run exact: a body that delays on it is followed only by `PumpUntil`.
- A test must not block on a task whose continuation is queued to the scripted run, such as a body's: nothing runs it until the blocked thread pumps.

## When to pass Microsoft's fake clock instead

The instant clock suits tests where time is not the subject: a delay ends, and the task moves on. When the order of events in time is what you are testing, such as what a reporter sees while a wait is in progress, or which of two waiting tasks finishes first, a scripted run's own clock, moved with `Advance`, says exactly when each timer fires. Outside a scripted run, pass `FakeTimeProvider` from the `Microsoft.Extensions.TimeProvider.Testing` package and advance it yourself, step by step. `Dagline.Testing` does not depend on that package; add it to your test project.

## What the harness does not cover

The harness's graph has one node, so two kinds of task are tested by running a small real graph over fakes, as in [Testing how a graph is wired](#testing-how-a-graph-is-wired), or as a [scripted run](#scripting-a-whole-run):

- a task that reads the status of a node it depends on through `context.StatusOf`;
- a task that holds a limit.
