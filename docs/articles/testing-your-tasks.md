# Testing your tasks

`Dagline.Testing` is a package for test projects only. It holds a harness that runs one task body and hands back what the task left behind, and a clock on which every finite delay ends at once, so a task that waits an hour in production takes milliseconds in a test. It depends on `Dagline` alone: no test framework, no assertion library and no terminal library, so it works with whatever your test project already uses.

```shell
dotnet add package Dagline.Testing
```

The examples below use Shouldly for assertions. Any assertion library does the same job.

## The harness and its outcome

<xref:Dagline.Testing.TaskHarness.For(Dagline.TaskBody,System.TimeProvider)> wraps one task body, and <xref:Dagline.Testing.TaskHarness.RunAsync> runs it and returns a <xref:Dagline.Testing.TaskOutcome>:

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
| `Waited` | Whether the task began waiting at least once in this run. |

It has no times, no checkpoint and no counts of lines that were not kept.

A run of the harness is a real run through `Runner.RunAsync`: a graph of one task at the root, a journal in a new directory the harness creates under the system's temporary directory, named `dagline-harness-` and random characters (on Unix only you can open it), and a reporter of the harness's own that watches it. The body sees exactly what it would see in your app, so a test proves what a real run will do. Nothing runs and nothing touches the disk until the first run. Disposing the harness deletes the directory. Disposing it twice is harmless, and a run after disposal throws `ObjectDisposedException`.

An exception the body throws never comes out of the harness: it is the task's unexpected failure and comes back as an outcome, the way a run sees it. An exception from the run itself, such as a journal that cannot be written, comes out of the call unchanged.

A harness does one run at a time and is not safe to call from two threads at once.

## The instant clock and its guard

With no clock passed, `RunAsync` runs on an <xref:Dagline.Testing.InstantClock>: a `TimeProvider` on which every finite delay and timer ends at once, and whose time jumps forward by the delay as it does. A delay or timer with an infinite due time never ends. A task that measures elapsed time, such as one with a deadline of its own, still sees time pass. The harness makes this clock once and uses it for its whole life: it starts at midnight UTC on 1 January 2000 when the harness is made, and a later run carries on from the time the earlier runs moved it to. Its local time zone is UTC, and time only moves forward.

A check that never returns `true` would spin for ever on such a clock, so the clock has a guard. Once more fake time than the guard has passed since the clock was made, making a delay or timer on it throws <xref:Dagline.Testing.ClockGuardException>. In a task body that fails the task, the run ends, and the harness throws `ClockGuardException` in place of returning the outcome, so the test fails with a clear exception instead of hanging. The default guard is 7 days of fake time. The guard is checked only when a delay or timer is made, so a periodic timer made before the guard was passed keeps firing past it; see [Do not hand an instant clock to a reporter](#do-not-hand-an-instant-clock-to-a-reporter). One default clock serves the whole life of a harness, so its guard counts fake time across runs.

A task that legitimately waits for longer gets a clock with a bigger guard:

```csharp
await using var harness = TaskHarness.For(MonthEndReconcile, new InstantClock(guard: TimeSpan.FromDays(40)));
```

A clock passed to `For` is used as is, in every run, and the body sees it as `context.TimeProvider`. The harness checks the guard of an `InstantClock` whether it is the default or passed in.

## A waiting task without real waiting

A task that waits through <xref:Dagline.TaskContext.WaitUntil(System.TimeSpan,System.Func{System.Threading.CancellationToken,System.Threading.Tasks.Task{System.Boolean}},System.String)> finishes without real waiting on the default clock:

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

<xref:Dagline.Testing.TaskHarness.RunUntilWaitingThenStopAsync> ends a first run the way an operator would: it requests a stop as soon as the task begins waiting and returns the outcome the run ends with. Run the harness again, and assert that the fake external system saw one trigger:

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

The same holds after a first run that ended failed: the next run hands the task its checkpoint. Once the task has succeeded, a further run does not call the body. It is an earlier success on the journal, so the run returns the same outcome, with `Waited` false.

## A task's behaviour on a stop

`RunUntilWaitingThenStopAsync` also tests what a task does when it is stopped. A task that waits through the helper and does nothing else comes back `Cancelled`, with `Waited` true. A task that handles the stop itself, by catching the cancellation and reporting, comes back with the outcome it chose:

```csharp
var outcome = await harness.RunUntilWaitingThenStopAsync();

outcome.Status.ShouldBe(NodeStatus.Failed);
outcome.Message.ShouldBe("stopped before the export finished");
```

A task that settles without ever waiting returns its own outcome from the call, so a test of a fast path does not hang.

With no clock passed, this call runs on a clock on which no delay ever ends, so the task is still waiting when the stop arrives. That clock is internal to the harness. Every delay on it never ends, not only the wait's, so a body that delays on `context.TimeProvider` before it begins waiting never reaches the wait: the stop is never requested and the call does not return. Pass a clock to `For` for such a body. A clock you pass to `For` is used as is here too. With an `InstantClock` passed, the call is a race by nature: the wait's checks run without delay, so the task may settle before the stop lands, and that outcome is the one returned.

Every run of the harness has an unlimited grace period, so the run never lets go of the task. A body that ignores the stop keeps the call from returning, and your test framework's timeout is what fails the test.

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

`Failed`, `Blocked`, `Cancelled`, `NeverReached`, `WithWarnings` and `EarlierSuccesses` on <xref:Dagline.Snapshot> answer most questions about a whole run without a reporter.

### Do not hand an instant clock to a reporter

A periodic timer on an `InstantClock` fires again and again, moving time forward each time, until it is disposed, and the guard does not stop it, because the guard is checked only when a delay or timer is made. Anything that keeps a periodic timer for the life of a run would spin on it. Reporters do: the plain-text reporter and the terminal display both refresh on a timer. Give a reporter a clock of its own, such as `TimeProvider.System`, or leave reporters out of a test run.

## When to pass Microsoft's fake clock instead

The instant clock suits tests where time is not the subject: a delay ends, and the task moves on. When the order of events in time is what you are testing, such as what a reporter sees while a wait is in progress, or which of two waiting tasks finishes first, pass `FakeTimeProvider` from the `Microsoft.Extensions.TimeProvider.Testing` package and advance it yourself, step by step. `Dagline.Testing` does not depend on that package; add it to your test project.

## What the harness does not cover

The harness's graph has one node, so two kinds of task are tested by running a small real graph over fakes, as in [Testing how a graph is wired](#testing-how-a-graph-is-wired):

- a task that reads the status of a node it depends on through `context.StatusOf`;
- a task that holds a limit.

The harness also cannot plant a checkpoint or read one directly. Get a task to a checkpoint by running it, and observe the checkpoint by what the task does with it in the next run.
