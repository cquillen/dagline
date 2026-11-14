# Waiting on external systems

Some tasks do little work themselves. They ask another system to do it, such as a batch service or a remote job queue, and then wait until that system says the work is done. Dagline gives such a task one call to wait with, and marks it as **waiting** while it does, so that the operator can tell idle tasks from working ones.

## The wait helper

<xref:Dagline.TaskContext.WaitUntilAsync(System.TimeSpan,System.Func{System.Threading.CancellationToken,System.Threading.Tasks.Task{System.Boolean}},System.String)> takes an interval, a check and a line of text:

```csharp
builder.Task("export", async context =>
{
    var job = await jobs.SubmitAsync("export", context.CancellationToken);

    await context.WaitUntilAsync(
        TimeSpan.FromSeconds(30),
        async token => (await jobs.GetStateAsync(job, token)).IsFinished,
        $"job {job} to finish");

    context.SetMessage($"job {job} finished");
});
```

The helper:

1. marks the task as waiting, with the text;
2. calls the check at once, so work that has already finished is noticed without waiting an interval;
3. while the check returns `false`, waits for the interval and calls it again;
4. returns once the check returns `true`, and the task stops waiting.

The interval runs from the end of one check to the start of the next, so a slow check never makes checks pile up. It is measured on the run's clock, <xref:Dagline.RunOptions.TimeProvider>, so a test that gives the run a fake clock moves the helper on without waiting in real time. An interval of zero or less is refused by the call itself with an `ArgumentOutOfRangeException`.

The check receives the task's <xref:Dagline.TaskContext.CancellationToken>: pass it to everything the check awaits. A check may use the context as the body does, so it can show how far the external work has got:

```csharp
await context.WaitUntilAsync(TimeSpan.FromSeconds(30), async token =>
{
    var state = await jobs.GetStateAsync(job, token);
    context.SetCount(state.ItemsDone, state.ItemsTotal);
    return state.IsFinished;
}, $"job {job} to finish");
```

There is no backoff and no jitter, and tasks that wait at the same interval are not spread out from one another.

## What waiting means

Waiting is a marker on a running task and never a status. The task is running throughout: its dependents, its group and the run outcome are what they would be without the marker, and progress, warnings and the outcome work as usual. The task counts as waiting from the moment it enters the helper until the helper ends, however it ends, time spent inside a check included.

Reporters see a <xref:Dagline.WaitingChanged> when the wait begins and another when it ends, and while it lasts the task's <xref:Dagline.NodeSnapshot> has <xref:Dagline.NodeSnapshot.WaitingSince> and <xref:Dagline.NodeSnapshot.WaitingText>. [Reporters](reporters.md#waiting-is-a-marker) has the details. The plain-text reporter prints a line at each end of the wait, so a long wait is visible in a log:

```text
2026-11-11T04:12:02Z waiting job: job 17 to finish
2026-11-11T04:13:40Z waited job for 1m38s
```

### A waiting task keeps its slots

A waiting task holds its slot of the max running tasks and of each of its limits for the whole wait, as any running task does. A limit on triggered work therefore bounds the work that is in flight in the external system, not only the moment of triggering it: with a limit of 4, no more than four jobs are ever waited on at once.

The cost is that idle tasks count towards the max running tasks. A run with many long waits wants a high <xref:Dagline.RunOptions.MaxRunningTasks>, so that waiting tasks do not keep working tasks from starting, and a [limit](defining-a-graph.md) on the heavy local work, so that a high max running tasks does not let too much of it run at once.

## When a check fails

The helper does not catch an exception from the check. An exception that escapes the body fails the task as an unexpected failure, with the exception's message, type and stack trace, and the task stops waiting just before it fails.

When the external system says the work itself went wrong, report it from inside the check, and the task fails as a reported failure with your message:

```csharp
await context.WaitUntilAsync(TimeSpan.FromSeconds(30), async token =>
{
    var state = await jobs.GetStateAsync(job, token);
    if (state.HasFailed)
    {
        throw context.Fail($"job {job} failed", state.Errors);
    }

    return state.IsFinished;
}, $"job {job} to finish");
```

### Riding out a transient error

A check that throws ends the wait, and the external work it was watching is still running. Every task waiting on the same system calls it, so one network blip can fail every waiting task at once. Whether a failed check means the work failed is your decision, so make it in the check: catch the errors you expect to pass, and return `false` to check again at the next interval.

```csharp
await context.WaitUntilAsync(TimeSpan.FromSeconds(30), async token =>
{
    try
    {
        return (await jobs.GetStateAsync(job, token)).IsFinished;
    }
    catch (HttpRequestException exception)
    {
        context.SetStatusText($"could not reach the job service, trying again: {exception.Message}");
        return false;
    }
}, $"job {job} to finish");
```

Catch only what can pass. An error that means the request itself is wrong, such as an unknown job, will be wrong at every check, and the task should fail.

## No deadline

A wait has no deadline. Dagline never gives up on work that may still be running, because it cannot know how long that work should take. When you do know, build the deadline in the check from the run's clock:

```csharp
var began = context.TimeProvider.GetTimestamp();
var limit = TimeSpan.FromHours(2);

await context.WaitUntilAsync(TimeSpan.FromMinutes(1), async token =>
{
    if (context.TimeProvider.GetElapsedTime(began) > limit)
    {
        throw context.Fail($"job {job} did not finish within {limit}");
    }

    return (await jobs.GetStateAsync(job, token)).IsFinished;
}, $"job {job} to finish");
```

The same pattern counts failed checks in a row, if you want to ride out a few transient errors but not an outage.

## A loop of your own

When the helper does not fit, such as a wait on a message rather than on a check, write the loop yourself inside a waiting scope. <xref:Dagline.TaskContext.BeginWaiting(System.String)> marks the task as waiting until the scope is disposed:

```csharp
using (context.BeginWaiting($"job {job} to finish"))
{
    while (!(await jobs.GetStateAsync(job, context.CancellationToken)).IsFinished)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), context.TimeProvider, context.CancellationToken);
    }
}
```

This loop is marked exactly as the helper is: the same events, at the same times, and the same snapshot. Take the delay from `context.TimeProvider` and pass the token, as the helper does.

Scopes may overlap. The task waits from the moment its first scope opens until its last one closes, with the text of the first, and a task may wait any number of times in one run. Disposing a scope twice does nothing.

## When the run is stopped

A stop ends the wait at once. A delay between checks ends as soon as the run signals the task's cancellation token, and the helper throws an `OperationCanceledException`. Let it escape the body and the task is **cancelled**, which never blocks its dependents. Reporters see the wait end immediately before the task is cancelled. If the run was already stopped when the task called the helper, it throws at once, without calling the check or marking the task as waiting.

A check that is running when the stop arrives is not interrupted: it sees the token, which it passes to what it awaits, and ends as that call ends. If the run's grace period runs out first, the run lets go of the task, which stops waiting immediately before it is recorded as cancelled. Whatever the task does after that, including closing its waiting scope, changes nothing. [Running and stopping](running-and-stopping.md) covers the grace period.

**A stop leaves the external work running.** Dagline only stops waiting for it. The job the task triggered carries on in the other system, and a later run that starts the task again starts a new one unless the task checks first. Cancel the external work yourself, if the other system allows it, in the task's own handling of the token:

```csharp
try
{
    await context.WaitUntilAsync(TimeSpan.FromSeconds(30), CheckAsync, $"job {job} to finish");
}
catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
{
    await jobs.CancelAsync(job, CancellationToken.None);
    throw;
}
```

Keep that cleanup within the grace period: once the run lets go of the task, nothing more it does is recorded.

## Checkpoints: re-attaching instead of starting twice

A task that starts work in an external system should not start it a second time when a run fails, is stopped or is killed, and its [journal](journals.md) is continued. A task does this with a **checkpoint**: a small piece of text, such as the external job's id, that Dagline keeps for the task and hands back when the task runs again in a run that continues the journal.

- <xref:Dagline.TaskContext.Checkpoint> is the task's checkpoint: what the journal handed back when the task started, or what the task last saved in this run. It is empty when there is none.
- <xref:Dagline.TaskContext.SaveCheckpointAsync(System.String,System.Threading.CancellationToken)> replaces it, and completes only once the text is flushed to the disk. Saving an empty string clears it.
- <xref:Dagline.TaskContext.ClearCheckpointAsync(System.Threading.CancellationToken)> clears it. Clearing when there is none writes nothing.

Dagline keeps the checkpoint through a failure, a cancellation and a killed process, and discards it when the task succeeds. It is private to the task: it is in no event and no snapshot, so no reporter ever shows it. Put an external id that the operator should see in the task's progress or outcome as well.

### Trigger or re-attach

The pattern is a few lines at the top of the task: start the work unless the checkpoint says it was started, then wait for it.

```csharp
builder.Task("export", async context =>
{
    var job = context.Checkpoint;
    if (job == "")
    {
        job = await jobs.SubmitAsync("export", context.CancellationToken);
        await context.SaveCheckpointAsync(job);
    }

    context.SetStatusText($"job {job}");
    await context.WaitUntilAsync(
        TimeSpan.FromSeconds(30),
        async token => (await jobs.GetStateAsync(job, token)).IsFinished,
        $"job {job} to finish");

    context.SetMessage($"job {job} finished");
});
```

When the run is stopped, killed or fails while the job is in flight, the next run that continues the journal hands `job` back, and the task waits for the same job instead of submitting a new one. Once the task succeeds, the checkpoint is gone.

The job task of the `Batch` sample is this pattern, with the clearing below and a comment on the open window. [Its README](https://github.com/cquillen/dagline/blob/main/samples/Dagline.Samples.Batch/README.md#3-ctrlc-during-the-jobs-then-again) shows a stopped run re-attaching to its jobs.

### Clearing a checkpoint for a fresh start

A checkpoint survives a failure, so by default a later run re-attaches to the same external work. That suits work that was fixed and re-run in place. When the external work itself has failed and a later run should start fresh work instead, clear the checkpoint before reporting the failure:

```csharp
var state = await jobs.GetStateAsync(job, token);
if (state.HasFailed)
{
    await context.ClearCheckpointAsync(CancellationToken.None);
    throw context.Fail($"job {job} failed", state.Errors);
}
```

### Do not pass the context's token to the save

The save's token cancels only a save that has not begun to write. Do not pass <xref:Dagline.TaskContext.CancellationToken> to a save that records work the task has just started: if the run is stopped between the start and the save, the save is cancelled, nothing is recorded, and a later run starts the work a second time. Pass nothing, or `CancellationToken.None`, as the examples above do.

A save made during the grace period after a stop succeeds and is handed back in the next run. A save or a clearing made once the task has settled, or after the run has let go of it, throws `InvalidOperationException`, because nothing it did would be kept.

### The window between the start and the save

One window stays open. If the process dies after the external system has accepted the work and before the save is on disk, the next run finds no checkpoint and starts the work again. Dagline cannot close that window, but the task can, where the external system allows, in one of two ways:

- **Save an id of your own before starting the work**, and start the work with it as a correlation id. The checkpoint then says only that a start may have happened, since the process can also die between the save and the start. When the task runs again, it settles which it was: it asks the external system for that id, or starts the work in a way that is idempotent on it.
- **Look the work up by a predictable name before starting it**, such as one built from the task's path and the journal's id.

A lookup closes the window only if finding nothing rules out a start the external system has already accepted. Where it cannot, the start itself has to be idempotent on that id or name. [ADR 0006](https://github.com/cquillen/dagline/blob/main/docs/adr/0006-a-checkpoint-is-the-tasks-own-text-kept-until-the-task-succeeds.md) gives the reasoning.

### The rules

- A checkpoint is at most 4,096 bytes as UTF-8, counted in bytes and not characters. A longer one is refused at the call with `ArgumentException`, so a test finds the limit before a long run does.
- Each save is a flush to the disk, which holds up the run for a few milliseconds. A checkpoint is for milestones, not for progress.
- With `Journal.None` the checkpoint lives in memory for the run: it starts empty, reads back what the task saved, and the same rules apply. A task's code is therefore the same in a test with no journal.
- A new journal has no checkpoints. A checkpoint whose path is not in a later run's graph is kept, and handed back if the path returns.
- The journal is readable text: never put a secret in a checkpoint.
