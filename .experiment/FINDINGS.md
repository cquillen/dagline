# Eager against deferred snapshot take: findings

Research only. This branch is never merged. It is kept as a reference for the engine redesign's spec (ADR 0015) and is deleted
once that spec is implemented.

## Question

Does the engine's deferred snapshot take still pay for itself? Today `RunSteps.Publish` closes a small `SnapshotChange` for each
event and `Run.Publish` takes each event's snapshot later, in order. The variant on this branch builds each event's snapshot when
the event is decided and deletes the close, queue and take protocol.

## Verdict

Eager taking still costs time and memory, but only where one step decides many events. Bytes allocated cannot see the cost: the
variant allocates 1 to 3% less everywhere, because both builds construct the same snapshots. The cost is retention: a step with
many events holds every event's snapshot alive until the step is carried out.

- 20,000 tasks made ready in one step (`ManyReadyAtOnce`): mean 763 ms baseline against 1,026 ms variant (+34%). Gen1 goes from
  20-22 to 26-27 per run and Gen2 from 2-3 to 4-5.
- `HeldByOneSmallLimit` (10,000 tasks): pooled median 131.5 ms against 170.3 ms (+29%), with Gen1 roughly doubled.
- `LargeGraph`, `HeldByLimits`, `WarningsBeyondKept`, `FrequentProgress`: no measurable difference.
- `BeginAndEndWaiting`: the baseline is bimodal across processes (about 51 ms or about 124 ms) and the variant only ran in the
  slow mode. The cause was not found.

What this means for the redesign: a snapshot must not be built for every event of a step before the step is carried out. Either
the loop builds each event's snapshot as it publishes that event, or the core's effect list stays lazy about snapshots, as the
deferred take is today.

## Numbers

Measured at f23c356. BenchmarkDotNet 0.15.8, .NET 10.0.15, Release, `--warmupCount 8 --iterationCount 20`, concurrent workstation
GC, 4 cores. Baseline and variant ran back to back in alternating order, each run its own process, while another process was
using the machine. Times are the median of all iterations pooled across runs, with the interquartile range.

| Scenario | Runs per build | Baseline time (ms) | Variant time (ms) | Baseline allocated (KB) | Variant allocated (KB) |
|---|---|---|---|---|---|
| `LargeGraph` | 2 | 80.6 (79.0-82.2) | 80.0 (78.3-82.9) | 31,455-31,550 | 31,044-31,201 |
| `HeldByLimits` | 2 | 30.6 (29.9-31.5) | 31.1 (30.2-31.7) | 17,392-17,407 | 16,789-16,867 |
| `HeldByOneSmallLimit` | 7 | 131.5 (115.7-154.0) | 170.3 (162.8-180.9) | 125,379-126,832 | 122,434-123,217 |
| `BeginAndEndWaiting` | 7 | 82.3 (51.1-124.1), bimodal | 128.5 (124.5-133.1) | 44,834-45,478 | 43,858-44,192 |
| `WarningsBeyondKept` | 4 | 270.4 (252.5-282.1) | 269.6 (261.1-279.2) | 101,718-106,604 | 103,187-103,210 |
| `FrequentProgress` | 7 | 14.6 (13.5-16.0) | 15.3 (14.3-17.1) | 32.5-32.7 | 31.9-32.1 |
| `ManyReadyAtOnce` | 4 | 771 (742-788) | 1,066 (948-1,090) | 300,652-300,722 | 293,224-295,242 |

Heap held by the decided first step of the 20,000-task graph (`RunSteps.Start` driven directly, full collection before and
after, three processes per build): 17.8 MB baseline against 76.0 MB variant, for 20,017 events. That is about 2.9 KB more per
event at 20,200 nodes.

A whole run of the same graph through `Runner.RunAsync` (12 runs per build): peak heap over start 37-43 MB against 96-101 MB,
Gen2 collections 0-1 against 2 per run, GC pause 83-126 ms against 146-210 ms.

Caveats: `LargeGraph` and `HeldByLimits` have two runs per build. The retention harness ran the scenario about three times faster
than BenchmarkDotNet did, for a reason not found, so BenchmarkDotNet is the primary source for time. Not measured: a run on a
journal file, the terminal display, server GC.

## Tests

618 of 618 pass on the baseline and on the variant. `SlotTryTests.StepThrough` lost a loop that called the deleted
`RunSteps.SnapshotAfter`; no assertion changed.

## What the variant deletes

`src/Dagline` is +55 -119 lines over five files. `SnapshotChange.cs` goes. `SnapshotTaker.Close` and `Take(SnapshotChange)` merge
into one `Take`. `RunSteps.SnapshotAfter` and `ChangeSinceLastEvent` go, and `StepEvent` holds its `Snapshot`. The rule that every
event's snapshot is taken in order before the next input disappears.

## Reproduce

From Git Bash at the worktree root:

- `bash .experiment/switch.sh baseline|variant` flips `src/` and `tests/` between the two states (`variant.patch`).
- `bash .experiment/bench.sh <state> <label> "*ManyReadyAtOnce*"` runs BenchmarkDotNet into `.experiment/bdn/<label>`.
- `bash .experiment/measure.sh <state>` runs the retention measurements.
- `python .experiment/pool.py .experiment` recomputes the pooled medians from `bdn/*.log`.

Raw results are in `bdn/A1..A7` (baseline), `bdn/B1..B7` (variant), `measurements.txt`, `baseline-test.log` and `variant-test.log`.
