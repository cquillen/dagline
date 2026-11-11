# Waiting on external systems

Some tasks do little work themselves. They ask another system to do it, such as a batch service or a remote job queue, and then wait until that system says the work is done. Dagline gives such a task one call to wait with, and marks it as **waiting** while it does, so that the operator can tell idle tasks from working ones.

## The wait helper

<xref:Dagline.TaskContext.WaitUntil(System.TimeSpan,System.Func{System.Threading.CancellationToken,System.Threading.Tasks.Task{System.Boolean}},System.String)> takes an interval, a check and a line of text:

```csharp
builder.Task("export", async context =>
{
    var job = await jobs.SubmitAsync("export", context.CancellationToken);

    await context.WaitUntil(
        TimeSpan.FromSeconds(30),
        async token => (await jobs.GetStateAsync(job, token)).IsFinished,
        $"job {job} to finish");

    context.Message($"job {job} finished");
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
await context.WaitUntil(TimeSpan.FromSeconds(30), async token =>
{
    var state = await jobs.GetStateAsync(job, token);
    context.Count(state.ItemsDone, state.ItemsTotal);
    return state.IsFinished;
}, $"job {job} to finish");
```

There is no backoff and no jitter, and tasks that wait at the same interval are not spread out from one another.

## What waiting means

Waiting is a marker on a running task and never a status. The task is running throughout: its dependents, its group and the run outcome are what they would be without the marker, and progress, warnings and the outcome work as usual. The task counts as waiting from the moment it enters the helper until the helper ends, however it ends, time spent inside a check included.

Reporters see a <xref:Dagline.WaitingChanged> when the wait begins and another when it ends, and while it lasts the task's <xref:Dagline.NodeSnapshot> has <xref:Dagline.NodeSnapshot.WaitingSince> and <xref:Dagline.NodeSnapshot.WaitingText>. [Reporters](reporters.md#waiting-is-a-marker) has the details.

### A waiting task keeps its slots

A waiting task holds its slot of the cap and of each of its limits for the whole wait, as any running task does. A limit on triggered work therefore bounds the work that is in flight in the external system, not only the moment of triggering it: with a limit of 4, no more than four jobs are ever waited on at once.

The cost is that idle tasks take up the cap. A run with many long waits wants a high <xref:Dagline.RunOptions.Cap>, so that waiting tasks do not keep working tasks from starting, and a [limit](defining-a-graph.md) on the heavy local work, so that a high cap does not let too much of it run at once.

## When a check fails

The helper does not catch an exception from the check. An exception that escapes the body fails the task as an unexpected failure, with the exception's message, type and stack trace, and the task stops waiting just before it fails.

When the external system says the work itself went wrong, report it from inside the check, and the task fails as a reported failure with your message:

```csharp
await context.WaitUntil(TimeSpan.FromSeconds(30), async token =>
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
await context.WaitUntil(TimeSpan.FromSeconds(30), async token =>
{
    try
    {
        return (await jobs.GetStateAsync(job, token)).IsFinished;
    }
    catch (HttpRequestException exception)
    {
        context.Status($"could not reach the job service, trying again: {exception.Message}");
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

await context.WaitUntil(TimeSpan.FromMinutes(1), async token =>
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

When the helper does not fit, such as a wait on a message rather than on a check, write the loop yourself inside a waiting scope. <xref:Dagline.TaskContext.Waiting(System.String)> marks the task as waiting until the scope is disposed:

```csharp
using (context.Waiting($"job {job} to finish"))
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
    await context.WaitUntil(TimeSpan.FromSeconds(30), CheckAsync, $"job {job} to finish");
}
catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
{
    await jobs.CancelAsync(job, CancellationToken.None);
    throw;
}
```

Keep that cleanup within the grace period: once the run lets go of the task, nothing more it does is recorded.
