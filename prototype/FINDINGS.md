# Engine loop prototype: findings

A throwaway prototype of the redesign in ADR 0015 (one loop owns a run) and ADR 0016 (a run is a handle). It exists to answer three questions. Nothing here is meant to ship: this branch is never merged, and it is kept only as a reference for the redesign's spec until that spec is implemented.

## Verdicts

- **A. Can a manual host make a whole run deterministic?** Yes for the engine, and only partly for bodies. The loop and the reporters run only inside a pump call, so the order in which the run applies inputs is always the order the test caused them. A body stays on the pumping thread only while everything it awaits is completed by the test, by the driver's clock or by the loop. "Pump until idle" cannot tell "nothing left" from "the work is somewhere I cannot see", and it returns early without saying so. The driver therefore needs a second operation, "pump until this condition", that may wait in real time.
- **B. What does a post cost against today's lock?** About the same when nothing contends, and less when bodies run at once. On the scenarios shaped like `RunBenchmarks`, allocation is within 10% either way. Two things do change: a progress report costs a third of what it did, and the inbox has no back-pressure (one task that waits 50,000 times in a loop had about 90,000 inputs queued).
- **C. What did the design get wrong?** Nothing that sinks it. It left out 32 things that had to be invented or decided, listed in section C. The four that matter most:
  1. The host seam has five jobs, not three (item 10).
  2. The sketch applies an already-cancelled token one step too late, after tasks have started (item 7).
  3. A step is cut by every write, so "one write per step" does not hold, and a failed write of a *start* entry has no rule (items 1 and 2).
  4. The promise that a report shows in the current snapshot when the call returns is gone, which breaks the test harness's clock (item 24).

## How to run it

```
dotnet test --project prototype/Dagline.Prototype.Tests
dotnet run -c Release --project prototype/Dagline.Prototype.Bench -- 15 16
dotnet run -c Release --project prototype/Dagline.Prototype.Bench -- 15 1
```

The two arguments of the comparison are the iterations and the max running tasks of both engines. The test run takes about 13 seconds, most of it the probe matrix, which writes `artifacts/bin/Dagline.Prototype.Tests/debug/probe-matrix.md`. The prototype is not in `Dagline.slnx`, so `dotnet build` and `dotnet test` at the root do not touch it.

Last run of the tests: 35 of 35 passed. An earlier state with 30 tests passed three times in a row. Three of the core's rules were then broken on purpose, one at a time. Breaking "nothing starts after a failed settle" failed cases 2 and 3, and dropping the held stop failed the cancelled-token test. The third (marking the run unrecorded when only the end's write fails) failed nothing, because the outcome is decided before that write and nothing reads the flag afterwards.

## What was built

| File | What it is |
|---|---|
| `Dagline.Prototype/RunCore.cs` | The reducer: `Apply(input, now)` returns an ordered list of effects. Adapted from `RunSteps`. |
| `Dagline.Prototype/ProtoRun.cs` | The handle (drain, stop, current snapshot, result) and the loop. |
| `Dagline.Prototype/Inbox.cs` | Many writers, one reader. A post enqueues and, if the loop is waiting, hands its continuation to the host. |
| `Dagline.Prototype/Hosts.cs` | `IRunHost`, the thread-pool host, and the manual host with three ways to start bodies. |
| `Dagline.Prototype/TaskSlot.cs`, `ProtoTask.cs`, `ProtoContext.cs` | The task's slot (progress and counts beyond the kept), the loop's record of a task, and the stand-in for `TaskContext`. |
| `Dagline.Prototype/Reporters.cs`, `Journals.cs`, `PumpedClock.cs` | Reporter delivery pumped by the host, a journal in memory that fails on a chosen write, a journal on a real file, and a clock the pump fires. |
| `Dagline.Prototype.Tests` | `CaseTests` (the five cases), `InvariantTests`, `FailedWriteTests`, `HostProbes` (question A's matrix), `HarnessTests` (the harness rebuilt). |
| `Dagline.Prototype.Bench` | The old-against-new comparison for question B. |

### Reused, stood in for, left out

- **Reused through `InternalsVisibleTo`** (the only change under `src/`, in `Dagline.csproj`): `Graph` and `GraphBuilder`, `BuiltTask`, `Slots`, `HeldTasks`, `RollUp`, `SnapshotTaker`, `Snapshot`, `NodeSnapshot`, every `RunEvent` type, `TaskOutcome`, `TaskChange`, `RunSettings`, `GracePeriodRules`, the journal entry records, `JournalFile`, `JournalWriteException`, `ReporterError`, `IReporter` and `PlainTextReporter`. So limits, groups, the roll-up and a snapshot for every event are all real.
- **Stood in for:**
  - `TaskContext` is sealed and bound to `Run`, so bodies take a `ProtoContext`. A graph is built with placeholder bodies, and `ProtoBodies` maps each placeholder to the prototype's body.
  - `TaskInstance` became `ProtoTask`, a copy that samples a slot.
  - `RunSteps`, `Run`, `ReporterQueue` and `JournalRecorder` were rewritten.
  - `RunResult` became `ProtoResult`, which carries the failed write.
  - The fourth run outcome is `(RunOutcome)3`, by a cast, so that `RunEnded` and `Snapshot` carry it without a change to the public enum.
- **Left out:** continuing a journal (earlier successes, set-asides, a checkpoint handed back), `TaskContext.StatusOf`, `ConsoleStop`, and the one-call `Runner.RunAsync` over the handle. The journal write is synchronous on the loop's turn.

## The cases

Each is one synchronous test on the manual host, run once, with no sleep and no real time. All pass.

| Case | Test (in `CaseTests`) | What it showed |
|---|---|---|
| 1, stop first | `Case1_stop_applied_first_then_the_body_ends` | Events: stop requested, grace period over, `load` cancelled, run ended stopped. The dependent stays pending. The body's later end changes nothing. |
| 1, body first | `Case1_the_body_ends_first_then_the_stop` | Events: `load` succeeded, `publish` ready, `publish` running, stop requested, grace period over, `publish` cancelled, run ended stopped. |
| 1, one task | `Case1_one_task_stop_first_is_stopped_and_body_first_is_complete` | The same two happenings in the two orders give stopped and complete. |
| 2 | `Case2_a_settle_whose_write_fails_...` | The settle is published, `publish` becomes ready, Dagline's stop follows, and the run ends unrecorded. `publish` never starts, nothing more is written, and the result task does not fault. |
| 3, last settle | `Case3_the_last_settles_write_fails_...` | Every task succeeded, and the run is unrecorded, with a stop by Dagline published before the end. |
| 3, end entry | `Case3_only_the_end_entrys_write_fails_...` | The outcome stays complete, with no stop. |
| 4 | `Case4_a_save_whose_write_fails_...` | The save throws `JournalWriteException` to the body, the task's checkpoint stays empty, the token is signalled, and the run ends unrecorded when the body ends. |
| 5 | `Case5_a_save_before_the_grace_period_elapses_...` | A save at 9 of 10 seconds is on disk and readable. One after letting go throws `InvalidOperationException`. |
| 5, variants | `Case5_a_save_after_letting_go_is_refused_by_the_core_...`, `Case5_a_save_in_flight_when_the_grace_period_elapses_...` | The first is answered by the core while the loop waits for reporters. The second is posted before the grace period elapses and applied first, so it succeeds: the order of the inbox decides. |

Two things the cases taught that the design does not say:

- **A body's end is the post of its input, not the completion of what it awaited.** To put "the body ended" ahead of "stop" in the inbox, the test must pump the bodies between opening the gate and asking for the stop. This is why the manual host pumps the loop, the bodies and the reporters separately as well as together.
- **A task can be started and let go of in the same turn of the loop.** In case 1, body first, `publish` starts and is cancelled within one turn, so its body first runs after the run has ended, with its token already signalled. Starting a body is an effect the host carries out later. The old engine has the same window, but narrower.

### Failed writes the cases do not reach (in `FailedWriteTests`, all pass)

- **A start entry's write fails.** Nothing starts, the slot is given back, the task stays ready, and the run ends unrecorded. The settle before it landed in a write of its own.
- **The run's start entry's write fails.** `RunStarted` and the ready events are still published, nothing starts, and the run ends unrecorded with an empty journal.
- **A save after an earlier failed write.** It throws the earlier failure and writes nothing.
- **A write fails after the app's own stop.** No second stop is published. The snapshot says the app stopped the run and the outcome says unrecorded.

### Invariants (in `InvariantTests`, all pass)

- **The tasks' token is signalled before the result is available.** Checked from a continuation on the result. The core's effects for a stop are, in order: signal the tasks, start the grace period, publish the stop.
- **The grace timer exists before a reporter hears of the stop.** On the thread pool, a reporter that advances a fake clock by the grace period the moment it hears `StopRequested` ends the run. Had the timer been made later, the test would hang.
- **A reporter never reads a current snapshot older than an event it holds.** On the thread pool, 60 tasks, no violation in the run. This follows from the loop setting the current snapshot before it queues the event, so the test is a check and not a proof.
- Also covered: a token cancelled before the call starts nothing; a drain; one time across a write; the reporter wait on the run's clock, and that cancelling the token after the end changes nothing; every piece of a manual run on the test's thread; progress never posted.

## A. Can a manual host make a whole run deterministic?

### What was measured

`HostProbes` runs one task whose body awaits one kind of thing, under each way of starting bodies, and records three facts: whether every piece of the body ran on the pumping thread, whether pump-until-idle finished the run, and whether pump-until-condition did. Unlike the cases, the probes use real time: a piece that runs off the thread sleeps 150 ms, so that a pump that returns early is seen to, and pump-until-condition waits up to 3 seconds. A TUnit test thread has no `SynchronizationContext` of its own.

The two mechanisms, a `SynchronizationContext` installed while pumping and a custom `TaskScheduler`, gave the same answer on every probe.

| The body awaits | Every piece on the pumping thread | Pump until idle finishes the run |
|---|---|---|
| (i) a `TaskCompletionSource` the test completes, with or without `RunContinuationsAsynchronously` | yes | yes |
| (ii) `Task.Delay` on `FakeTimeProvider`, ended by `Advance` | yes | yes |
| (ii) the same delay, ended by the stop | yes | yes |
| a checkpoint save, with or without `ConfigureAwait(false)` | yes | yes |
| (iii) `Task.Yield()` | yes | yes |
| (iii) a TCS or a fake delay with `ConfigureAwait(false)`, completed by the test with no context on its thread | yes (it runs inline in the completing call) | yes |
| (iii) a source of the body's own, cancelled from a registration on the run's token, awaited with `ConfigureAwait(false)` | yes | yes |
| (iii) a TCS or a fake delay with `ConfigureAwait(false)`, completed while the thread has a context of its own | **no** | **no** |
| (iii) `Task.Delay(...).ConfigureAwait(false)` ended by the stop | **no** | **no** |
| (iii) `ConfigureAwait(false)`, then `Task.Yield()` | **no** | **no** |
| (iv) a pipe read, a file read, `Task.Run`, a delay on `InstantClock` | yes, the continuation comes back to the host's queue | **no**: it comes back at a moment the host cannot foresee |

Pump-until-condition finished every one of these.

The third way of starting bodies leaves them on the thread pool and makes only the loop and the reporters manual. Pump-until-condition finished every probe under it except the fake-clock delay, which did not end in either run of the matrix: the test advanced the clock before the body had made its timer. Only starting bodies on the host removes that race.

The asynchronous form, `PumpUntilAsync`, was tried in one test with a body that awaits `Task.Run`. It finished the run. After its first real wait it went on pumping from a thread-pool thread: in the run recorded, the test began on thread 18 and the body's continuation was pumped on thread 12. So "the test's thread" then means "inside this call, one thread at a time". The blocking `PumpUntil` keeps the test on one thread, and is what the other tests use.

### What follows

- **The engine is deterministic regardless of the bodies.** Engine code runs only inside a pump. Inputs are applied in the order they were posted. A body can only change *when* its inputs are posted, and a test controls that by making each thing happen before it causes the next.
- **`ConfigureAwait(false)` leaves the host in three ways**, pinned by `ConfigureAwait_false_leaves_the_pumping_thread_in_three_ways`:
  1. A `Task.Delay` ended by its token never resumed a `ConfigureAwait(false)` awaiter inline, though the cancel ran on the pumping thread. A source of the body's own, cancelled the same way, did resume inline, so this belongs to the delay. The runtime probably forces asynchronous continuations when a delay is cancelled; its source was not read.
  2. Once a continuation has run with no context, the next `Task.Yield()` goes to the thread pool.
  3. Completing the awaited thing on a thread that has a context of its own (a UI thread, or a test framework that installs one) sends the continuation to the thread pool.
- **`TaskContext.WaitUntilAsync` is written the first way today.** A stop of a task waiting in Dagline's own helper leaves the host. `HarnessTests` shows it: the outcome is still exact, and the body's own code still resumes on the pumping thread (its await of the helper captured the context), but the host was idle in between and the pump had to wait in real time. Dropping `ConfigureAwait(false)` inside the helper fixes this and changes nothing in an app, where a body starts on the thread pool with no context.
- **"Idle" is defined only for what the host can see.** That is a queue of loop turns, body continuations and reporter deliveries. I/O in flight, a continuation on the thread pool and a timer of a clock the host does not own are all invisible, and the pump returns as if done. A driver cannot detect this: a body that awaits a gate the test still holds looks the same.
- **`InstantClock` defeats a manual host.** Its timers fire on the thread pool as soon as they are made. A manual run needs a clock whose timers fire on the pumping thread: `FakeTimeProvider.Advance`, or the prototype's `PumpedClock`, which fires the next due timer when asked.
- **The reducer alone is the most deterministic surface.** `RunCore.Apply` has no thread, no clock and no await. Two of the invariant tests drive it directly and assert the effect list. Most tests of ordering could be written that way and need no host at all.

### Proposed driver

```csharp
namespace Dagline.Testing;

public sealed class ScriptedRun
{
    // Starts a run on a manual host. Nothing has happened when this returns. The clock is the driver's unless the options give one.
    public static ScriptedRun Start(Graph graph, RunOptions options, CancellationToken cancellationToken = default);

    public Run Run { get; }                 // the handle of ADR 0016: Drain(), Stop(), Current, Result

    // Runs everything queued, on the calling thread, until nothing is queued. Returns how many items ran.
    public int Pump();

    // One kind only, for ordering a body's end against a request.
    public int PumpLoop();
    public int PumpBodies();
    public int PumpReporters();

    // Pumps until the condition holds. When nothing is queued and it does not hold, blocks until something is queued from
    // another thread. Returns false when the real-time limit runs out.
    public bool PumpUntil(Func<Snapshot, bool> condition, TimeSpan realTimeLimit);
    public Task<bool> PumpUntilAsync(Func<Snapshot, bool> condition, TimeSpan realTimeLimit);

    // Only when the driver owns the clock.
    public void Advance(TimeSpan by);       // fires what falls due, on the calling thread
    public bool FireNextTimer();            // moves time to the next due timer and fires it; false when there is none

    // Pumps until the result, waiting in real time if it must.
    public RunResult Finish(TimeSpan realTimeLimit);
}
```

Its honest limits, which its documentation would have to state:

- `Pump` returning does not mean the run is waiting for the test. It means the host's queues are empty.
- A body is scripted only if everything it awaits is completed by the test, by the driver's clock or by the run. Anything else needs `PumpUntil`, and then the test is as deterministic as the order in which the test waits for things.
- Complete a test primitive from the test's thread with no `SynchronizationContext` installed on it, or awaiters using `ConfigureAwait(false)` leave.
- Do not block on a task whose continuation is queued to the host (`.Result`, `.Wait()`): the pump is the only thing that would run it.
- After `PumpUntilAsync` has waited once, the rest of the test runs on a thread-pool thread.
- The loop's turn applies every input in the inbox. The prototype has no way to apply exactly one. None of the cases needed it, because the test controls what is in the inbox when it pumps.

### Can `RunUntilWaitingThenStopAsync` be rebuilt without its special clock?

It can be, and it has to be. `InstantUntilWaitingClock` decides each timer by reading the live snapshot at the moment the body makes the timer, and relies on the wait being in the snapshot when `BeginWaiting` returns. Under one loop it is not: `A_wait_is_not_in_the_current_snapshot_when_the_call_that_began_it_returns` shows the answer is still "not waiting" then. The existing clock was not run against the prototype; from its code, it would fire the poll delay and the body would poll an undefined number of times before the stop.

`HarnessTests.RunUntilWaitingThenStop` is the rebuild, in about 40 lines:

1. Pump until idle.
2. If the snapshot shows the task waiting, or the run has ended, go to step 4.
3. Fire the next timer and go to step 1. If there is no timer, wait in real time for something to be queued.
4. Stop, then pump until the result. No timer fires after the stop.

It gives one check, a clock moved by exactly the delay before the wait, and a cancelled task, on one thread, when the body uses a helper without `ConfigureAwait(false)`. It still needs a clock of its own, but a simpler one: a clock the pump fires. The decision moves out of the clock and into a condition on what the loop has applied.

## B. What does a post cost against today's lock?

### What is compared

Both engines run on the thread pool, the old one through `Runner.RunAsync`. The graphs have the shapes of `RunBenchmarks`, built by the same `GraphBuilder`, with `PlainTextReporter` writing to nowhere. The harness is a plain console program, not BenchmarkDotNet, so that old and new runs alternate and whatever else the machine does falls on both. Each row is 15 iterations after 3 warm-ups, on 4 logical processors, .NET 10.0.15.

The prototype does per event what the old engine does: the same roll-up, the same snapshot taker, a snapshot handed with every event, a queue per reporter. One run of each engine handed its reporters the same number of events in every scenario (11,350, 26,454, 8, 6,486, 100,006 and 486), and the plain-text reporter wrote the same number of characters in all but the journal scenario.

**Not like for like:**

- The prototype's effects are structs in a list the core reuses. The old engine allocates a `Step` and two lists for each step. That is a choice of the prototype, not of the loop, and the old engine could make it too. It accounts for some of the allocation difference.
- The prototype's snapshot carries no journal information, so in the journal scenario the reporter wrote 2 lines fewer.
- The prototype has no earlier successes and no set-asides. These scenarios have none either.
- On a journal the prototype makes two writes for a settle that starts a task (the settle, flushed, then the start, not flushed) where the old engine makes one.
- Reporter delivery is a `ConcurrentQueue` pumped through the host, not a `Channel` with a loop of its own.

**The scenarios are the worst case for a lock.** Every body does nothing but report, synchronously, so with 16 running tasks on 4 processors the bodies fight over the old engine's lock. A real task spends its time awaiting. The contended rows say what the lock costs under that fight, not what an app would see.

### Numbers

Max running tasks 16 (the default):

| scenario | engine | wall ms: min / median / max | allocated MB: min / median / max | inputs posted | most queued at once | times the loop was woken |
|---|---|---|---|---|---|---|
| BeginAndEndWaiting (1,024 tasks x 4 waits) | old (lock) | 30.3 / 118.0 / 145.0 | 43.73 / 44.08 / 44.63 | - | - | - |
| BeginAndEndWaiting (1,024 tasks x 4 waits) | new (loop) | 25.8 / 31.7 / 67.7 | 40.92 / 41.63 / 42.68 | 9,218 | 143 to 144 | 1 to 138 |
| WarningsBeyondKept (256 tasks x 250 warnings) | old (lock) | 69.5 / 195.7 / 327.2 | 102.47 / 103.18 / 103.35 | - | - | - |
| WarningsBeyondKept (256 tasks x 250 warnings) | new (loop) | 50.0 / 52.9 / 63.1 | 92.95 / 94.46 / 95.34 | 25,858 | 1,611 to 1,616 | 1 to 6 |
| FrequentProgress (1 task x 200,000 reports) | old (lock) | 22.1 / 22.4 / 23.2 | 0.03 / 0.03 / 0.04 | - | - | - |
| FrequentProgress (1 task x 200,000 reports) | new (loop) | 7.3 / 7.4 / 7.6 | 0.04 / 0.04 / 0.04 | 3 | 1 to 1 | 2 to 2 |
| LargeGraph (2,048 chained tasks) | old (lock) | 21.2 / 25.0 / 98.8 | 30.39 / 30.43 / 30.74 | - | - | - |
| LargeGraph (2,048 chained tasks) | new (loop) | 18.5 / 19.8 / 34.8 | 31.15 / 31.27 / 31.32 | 4,098 | 32 to 32 | 1 to 49 |
| OneTaskManyWaits (1 task x 50,000 waits) | old (lock) | 104.4 / 115.7 / 214.1 | 211.40 / 211.42 / 211.56 | - | - | - |
| OneTaskManyWaits (1 task x 50,000 waits) | new (loop) | 99.9 / 139.3 / 386.8 | 174.74 / 175.94 / 176.89 | 100,003 | 89,043 to 94,879 | 2 to 2 |
| OnAJournalFile (160 tasks, every settle flushed) | old (lock) | 158.2 / 179.6 / 316.6 | 1.73 / 1.83 / 1.86 | - | - | - |
| OnAJournalFile (160 tasks, every settle flushed) | new (loop) | 163.4 / 175.5 / 320.6 | 1.72 / 1.73 / 1.76 | 322 | 32 to 32 | 1 to 2 |

Max running tasks 1, so no two bodies run at once and nothing contends:

| scenario | engine | wall ms: min / median / max | allocated MB: min / median / max | inputs posted | most queued at once | times the loop was woken |
|---|---|---|---|---|---|---|
| BeginAndEndWaiting | old (lock) | 34.9 / 40.4 / 60.8 | 43.88 / 43.89 / 43.93 | - | - | - |
| BeginAndEndWaiting | new (loop) | 34.8 / 50.6 / 74.5 | 40.99 / 41.63 / 41.77 | 9,218 | 9 to 9 | 1,010 to 1,027 |
| WarningsBeyondKept | old (lock) | 59.4 / 63.3 / 142.2 | 102.43 / 102.44 / 102.52 | - | - | - |
| WarningsBeyondKept | new (loop) | 49.7 / 52.8 / 74.5 | 93.37 / 94.17 / 94.79 | 25,858 | 101 to 101 | 255 to 258 |
| FrequentProgress | old (lock) | 21.3 / 21.9 / 27.5 | 0.03 / 0.03 / 0.04 | - | - | - |
| FrequentProgress | new (loop) | 7.4 / 7.5 / 8.6 | 0.04 / 0.04 / 0.04 | 3 | 1 to 1 | 2 to 2 |
| LargeGraph | old (lock) | 19.3 / 22.4 / 78.7 | 30.35 / 30.36 / 30.44 | - | - | - |
| LargeGraph | new (loop) | 22.5 / 25.1 / 40.2 | 30.59 / 30.65 / 30.67 | 4,098 | 2 to 2 | 1,976 to 2,049 |
| OneTaskManyWaits | old (lock) | 97.8 / 103.5 / 111.0 | 211.39 / 211.39 / 211.46 | - | - | - |
| OneTaskManyWaits | new (loop) | 96.2 / 106.1 / 172.1 | 175.51 / 176.36 / 176.87 | 100,003 | 91,245 to 95,109 | 2 to 2 |
| OnAJournalFile | old (lock) | 155.4 / 168.2 / 309.3 | 1.59 / 1.60 / 1.63 | - | - | - |
| OnAJournalFile | new (loop) | 162.4 / 170.2 / 455.1 | 1.65 / 1.72 / 1.74 | 322 | 2 to 2 | 153 to 161 |

Both tables were measured twice. Wall time moved a lot between the two, which is the other load on the machine. The medians of the earlier run, old then new, in ms:

| scenario | 16 running: old / new | 1 running: old / new |
|---|---|---|
| BeginAndEndWaiting | 107.3 / 48.9 | 47.8 / 35.2 |
| WarningsBeyondKept | 266.3 / 80.8 | 57.2 / 51.3 |
| FrequentProgress | 21.1 / 7.4 | 20.6 / 7.4 |
| LargeGraph | 76.8 / 21.9 | 21.8 / 22.4 |
| OneTaskManyWaits | 110.1 / 124.2 | 91.5 / 99.2 |
| OnAJournalFile | 180.5 / 194.3 | 159.0 / 163.4 |

Allocation barely moved between the two runs: the medians agree within 2%, except the old engine on a journal at 16 running tasks (1.75 MB, then 1.83 MB).

### Reading them

- **Allocation, the trustworthy number.** The loop allocates 5 to 9% less on the waiting and warning scenarios and 1 to 3% more on the large graph. On a journal it is 5% less at 16 running tasks and 8% more at 1. On one task waiting 50,000 times, which is not a `RunBenchmarks` scenario, it is 17% less. A post is a 32-byte struct in a queue segment. Part of the saving is the reused effect list and not the design.
- **Wall time with nothing contending (1 running task).** No difference worth defending on waits, the large graph or a journal: the two runs disagree about which engine is ahead on waits, and the journal is the disk. The loop is 10 to 17% ahead on warnings in both runs. The loop pays one thread-pool hop each time a post finds it asleep, about once per task here (153 to 2,049 wake-ups, one for each task of each scenario), and that is what the lock does not pay.
- **Wall time with bodies fighting (16 running tasks).** The old engine's median is two to four times the loop's on waits and warnings, with a spread from 30 to 145 ms on one scenario. The loop's time is about what it is with 1 running task. See the caveat above: this is what a lock costs when 16 threads do nothing but take it.
- **Progress.** 400,000 reports take about 22 ms under the lock and 7.4 ms as slot writes: about 55 ns against 18 ns a report. A slot write still takes a lock, the task's own, which nothing else holds for long.
- **Where the time goes.** One task that waits 50,000 times costs about a microsecond per call in both engines. The work is the event, its snapshot and its line, not the lock or the post. Moving it from the body's thread to the loop saves nothing.
- **The inbox is not bounded.** Under the lock that task could not get ahead of the engine. Under the loop it posts 100,000 inputs faster than the loop applies them, and 89,000 to 95,000 are queued at once. With 16 tasks each adding 250 warnings the peak is about 1,600, which is the kept warnings of the running tasks.

## C. What the design left out or got wrong

Each item says what was ambiguous or missing and what the prototype did. "Invented" marks a mechanism the rules did not have.

### The write and the step

1. **A step is cut wherever a write is, and a settle that starts a dependent has at least two.** The start of a task is a journal entry too. The rule that the core decides a start only once it knows the settle landed puts the start's entry in a later write: the settle (flushed), then the starts (not flushed), then perhaps the run's end. ADR 0015's "the first version writes once for each step" does not hold. The run's start is a write too, so `RunStarted` is published only after it.
2. **A failed write of a *start* entry has no rule.** The rules cover a failed settle. The prototype made starts tentative: it takes the slots, asks for the write, and only on success moves the tasks and starts the bodies. On failure it gives the slots back and nothing starts (tested). The cheaper alternative starts the body first and writes its entry after, as the old engine in effect does. Someone has to choose.
3. **The core needs explicit "what follows this write" state** (invented: five kinds of continuation and the lists they need). While it is set, `Apply` accepts only `Written` and throws on anything else. The loop guarantees that by handling the result before it reads the inbox.
4. **How the core knows there is no journal.** The prototype tells the core at construction, and the core asks for no Record at all: it runs the continuation in the same call. The same switch is thrown when a write fails. So the code after a write is entered two ways, from `Written` or directly, and must not assume a fresh `Apply`. A writer that always succeeds would be simpler in the core and cost a round trip per step.
5. **A step's events are no longer published together.** An event that is not recorded and comes before the write (`GraceEnded`) is published before it, and the cancellations it announces after it. A reporter hears "grace period over", then waits a flush.
6. **The time of an input is when the loop takes it, not when it happened.** The sketch reads the clock in the loop. A warning's time and a wait's `WaitingSince` are therefore late by however long the input sat in the inbox, which under load is not small (see the 90,000 queued inputs). Under the lock the time was read at the call. Reading it on the poster's thread instead would give times that can run backwards against the order of events.

### The loop and the host

7. **The sketch applies an already-cancelled token one step late.** It takes `Input.Start` before it reads the inbox, so the stop that `UnsafeRegister` posted synchronously is applied after the start step, by which time tasks are running. The rule "held by the core, applied before any starts" could not happen as sketched. The prototype posts the start to the inbox after registering, so the stop is ahead of it and the core holds it (tested). With the handle of ADR 0016 a drain cannot precede the start at all, so the token is the only case, and passing `IsCancellationRequested` with the start, as the old engine does, would also do.
8. **Waking the loop needs an awaiter of the inbox's own** (invented). A `TaskCompletionSource` or a `Channel` would resume the loop either inline on the poster's thread, which breaks "a post runs no engine code", or on the thread pool, which escapes a manual host. The prototype's awaiter hands the continuation to `host.RunLoop`. The loop's first turn must go through the host too.
9. **Every other await in the loop has the same problem.** The sketch awaits the journal write and `reporters.CompleteAsync`. Each would resume wherever its completion ran. The prototype made the write synchronous on the loop's turn, and turned "the reporters are done" and "the reporter wait ran out" into inputs (invented), so that the loop only ever awaits its inbox. An asynchronous write would need a host-aware resumption.
10. **The host has five jobs, not three** (two invented):
    - **Signalling the tasks' token.** `tasksToken.Cancel()` as sketched runs every callback a body registered, and the continuations they complete, on the loop. An app needs `CancelAsync` (the token reads as cancelled at once, the callbacks run on the pool). A manual host needs the inline `Cancel` on the pumping thread. Dropping a reporter cancels a token in the same way.
    - **Saying how a completion the loop makes resumes its awaiter.** This covers a save's answer and the run's result: `RunContinuationsAsynchronously` in an app, inline under a manual host.
11. **Timers belong to the clock, not to the host.** A manual host is only as manual as the clock. See A.
12. **The loop must keep reading its inbox while it waits for reporters, and the inbox must answer after the loop has gone** (invented). As sketched, nothing reads the inbox once the loop leaves its `while`, so a save made after the run ended would never be answered and its body would wait for ever. The prototype reads the inbox during the reporter wait, and a closed inbox refuses a save on the thread that posts it (both tested).
13. **How reporter delivery is pumped is not in the rules** (invented). Each reporter has a queue and a "scheduled" flag. A publish that finds the flag clear asks the host to run that reporter's delivery, which drains the queue and clears the flag. Under the manual host, deliveries sit in a queue of their own with a context of their own, so a reporter that awaits comes back to the reporter queue and not to the bodies'. "Every reporter is done" is a callback that posts to the inbox (item 9). Only synchronous reporters and one that never returns were exercised.
14. **The closed set of effects lacks an order for closing the journal** (invented: a `CloseJournal` effect before the publish of `RunEnded`). Without it a reporter that hears the run ended can try to continue the journal before the loop has let go of it.
15. **A fault in the loop needs a rule.** If the core throws, nothing completes the result and nothing answers saves. The prototype faults the result and closes the inbox.

### Sampling

16. **A sample needs three mechanisms to be consistent** (all invented, in `TaskSlot`):
    - **A lock of the task's own**, so a count is not read with another report's total, and so a context stays safe from several threads.
    - **A version**, so the sampler skips a task that reported nothing.
    - **A freeze at settle and at letting go.** Without it a reader shows progress later than the settled task has, and a body the run let go of keeps changing a cancelled task.
17. **The kept limit moves to the task's side, and is then split.** To post only the kept warnings the context must count them. Detail lines have one limit of 100 fed from three places: `AddDetail`, `Fail`, and the type and stack trace the settle adds for an unexpected failure. The first two are counted on the task's side and the third by the core.
18. **A count beyond the kept can be sampled before the kept have been applied.** A snapshot could say 0 warnings listed and 30 omitted. The prototype shows the count only once 100 are listed (tested).
19. **A group's warning count cannot be sampled by a reader.** The roll-up is the loop's. A reader of the current snapshot sees the task at 130 warnings and its group at 100 until the next event. Not fixed.
20. **"Sampled when the event is published" makes the core impure.** Events are decided before a write and published after it. Sampled at the decision, a reader's current snapshot taken during the write would show later progress than the event that follows. The prototype samples in the continuation after the write, so the reducer reads the slots of the running tasks. It is deterministic given the slots, but it is not a pure function of its inputs.
21. **A reader of the current snapshot builds a snapshot at each read** when progress has changed, from the last published one plus the slots of the running tasks, which the loop has to publish beside it.
22. **An input that is not an event leaves the current snapshot out of date, and only the loop can rebuild it** (invented: a `Stale` effect, and a rebuild when the loop next finds its inbox empty). The old engine rebuilt on read, which a reader may no longer do. So a message shows once the loop has caught up, not once it has been applied.
23. **Before the loop's first turn the handle has a snapshot with no start time.** `Start` returns before anything has happened, so `Current` must already answer.

### The task's side

24. **Four things a task reads synchronously today are run state.**
    - **`Checkpoint`.** It needs a cell the loop writes when it answers a save (invented).
    - **`StatusOf`.** It must read the published snapshot. Not built.
    - **`BeginWaiting`.** It returns a scope that does nothing when the task has settled. Now it always posts and the core ignores it.
    - **"Everything reported shows in `Current` from the moment the call returns".** This sentence in `TaskContext`'s documentation is no longer true (tested). It is what the harness's clock depends on (see A).
25. **A save with no journal is no longer immediate.** It is a trip through the inbox, because only the core knows whether the task is still running. `Checkpoint` reads the new text after the await, not after the call.
26. **A save's cancellation token.** "Cancels the save only if signalled before the save begins to write" now has a gap: the save sits in the inbox first. The prototype honours the token only at the call.
27. **A save whose write fails is answered before the stop it causes.** The effects are: answer the save, signal the tasks, start the grace period, publish the stop. Whether the task sees its token signalled when the save throws is unspecified, and it differs between hosts. The exception carries "the snapshot as of the failed save", and the prototype gives it the last published one.
28. **A save made after an earlier write failed** is refused with that earlier failure, as the old engine does (tested). The rules say nothing.

### The end

29. **`RunEnded` cannot carry "unrecorded, with the error".** It has an outcome or an error, not both. `RunResult` needs the failure too. The prototype returns it beside the outcome.
30. **When only the end's write fails, is the app told?** The outcome is kept. The prototype still returns the failure. The old engine showed it only in the journal.
31. **A failed write after the app's own stop** publishes no second stop, so the snapshot says the stop was by the app while the run is unrecorded (tested).

### The inbox

32. **Nothing bounds it.** Kept warnings and detail are bounded per task. Waits and messages are not. See B for the measurement. The lock gave back-pressure for free. The message is never an event and only its latest value matters, so it could be a sample, which ADR 0015 considered and rejected for a different reason.

## Unfinished

- The prototype was not run against the repo's own test suite. Its behaviour is checked by its 35 tests and by the comparison's event counts only.
- The inbox's wake-up was not stress-tested on its own. The comparison ran several hundred runs on the thread pool without a hang, which is evidence and not proof.
- Reporter delivery was exercised only with synchronous reporters and one that never returns.
- Not built: continuing a journal, `StatusOf`, `ConsoleStop`, the one-call `Runner.RunAsync`, an asynchronous journal write, and applying exactly one input per pump.
- Item 19 is known and not fixed.
- The comparison is not BenchmarkDotNet and the machine was not quiet.
