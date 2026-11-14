# Getting started

This page builds a small console app that runs two tasks, shows them on the terminal while they run, can be stopped with Ctrl+C, and carries on where it stopped the next time it starts. It takes about ten minutes.

The code on this page is the whole of the `Basic` sample, [`samples/Dagline.Samples.Basic`](https://github.com/cquillen/dagline/tree/main/samples/Dagline.Samples.Basic), which CI builds and runs on every change.

## Install the packages

Make a console app and add both packages:

```shell
dotnet new console -o Basic
cd Basic
dotnet add package Dagline
dotnet add package Dagline.Spectre
```

`Dagline` builds and runs the graph. `Dagline.Spectre` draws the run on the terminal and hooks Ctrl+C. The core package has no console or UI dependency, so an app that never shows a terminal can leave the second one out.

Replace `Program.cs` with the code below, in the order it is shown.

## Build the graph

```csharp
using Dagline;
using Dagline.Spectre;

// The graph: one group that holds two tasks. The second requires the first, so it runs only once the first has succeeded.
var builder = new GraphBuilder();
var shard = builder.Group("shard-1");

var load = shard.Task("load", async context =>
{
    context.SetStatusText("loading entities");
    await Task.Delay(TimeSpan.FromSeconds(3), context.TimeProvider, context.CancellationToken);
    context.SetMessage("loaded 40 entities");
});

shard.Task("store", async context =>
{
    context.SetStatusText("storing entities");
    for (var stored = 10; stored <= 40; stored += 10)
    {
        await Task.Delay(TimeSpan.FromSeconds(1), context.TimeProvider, context.CancellationToken);
        context.SetCount(stored, total: 40);
    }

    context.SetMessage("stored 40 entities");
}).Requires(load);

var graph = builder.Build();
```

The graph is a tree: the group `shard-1` holds the tasks `shard-1/load` and `shard-1/store`. A group runs no code. It is there so that the tree matches the shape of the work. `Requires(load)` is a dependency: `store` starts only once `load` has succeeded, and would be blocked if `load` failed.

A task body is an async function that receives a <xref:Dagline.TaskContext>. Through it the task reports progress, with `SetStatusText` and `SetCount`, and the message of its outcome. The bodies wait on the context's clock and pass the context's cancellation token, so that a stop ends them at once. [Defining a graph](defining-a-graph.md) and [Writing tasks](writing-tasks.md) cover both halves.

## Choose a journal

```csharp
// The journal: continue the newest one that is not finished, so that a run you stopped carries on where it left off.
// When there is none, start a new one. The journals are kept beside the program.
var directory = Path.Combine(AppContext.BaseDirectory, "journals");
var journals = await Journals.ListAsync(directory);
var open = journals.LastOrDefault(j => !j.Finished && j.Ending is not JournalEnding.InUse);

var journal = open is null
    ? Journal.New(directory, "basic sample")
    : Journal.Continue(directory, open.Id);
```

A journal is a file that records what each run did, so that a later run can continue it: tasks that succeeded are kept and do not run again. The first time the app runs there is no journal in the directory, so `Journal.New` starts one, with a description that tells it apart from others. A journal is *finished* when its latest run was complete, so a journal you stopped is not, and the next run continues it with `Journal.Continue`.

Dagline never picks a journal for you. These few lines are this app's policy, and [Journals](journals.md#the-app-always-chooses) says why the choice is yours.

## Hook Ctrl+C

```csharp
// Ctrl+C: the first press stops the run, the second stops waiting for the running tasks to end.
using var stop = ConsoleStop.Hook();
```

<xref:Dagline.Spectre.ConsoleStop.Hook*> gives a <xref:Dagline.StopSignal> hooked to the console's cancel key. The first Ctrl+C asks the run to stop: it starts nothing more and waits for the running tasks to end, for at most 30 seconds. A second Ctrl+C ends that wait at once. Both are swallowed, so the run winds down and your app still gets its result. A third Ctrl+C is not swallowed, and ends the process: the way out of an app that hangs. `using` unhooks the key when the app ends.

## Run it

```csharp
// The run: the display on a terminal, or plain text when output is redirected.
var result = await Runner.RunAsync(graph, new RunOptions
{
    Journal = journal,
    Reporters = [SpectreReporter.Auto()],
}, stop);
```

<xref:Dagline.Runner.RunAsync*> runs the graph and returns when the run has ended. The journal choice is the one option you must set. A reporter shows the run while it happens, and <xref:Dagline.Spectre.SpectreReporter.Auto*> gives the right one for where the app runs: the display on a terminal, and timestamped lines of plain text when output is redirected to a file or a build log.

> [!WARNING]
> While the display is up, your app's own logging must go to a file and not to the console: see [The terminal display](terminal-ui.md#your-apps-own-output).

## Map the run outcome to an exit code

```csharp
// The process exits with 0 when every task succeeded, 1 when one did not, and 2 when the run was stopped.
return result.Outcome.ToExitCode();
```

The run outcome says how the run ended: complete, incomplete or stopped. Dagline never sets your exit code: <xref:Dagline.RunOutcomeExtensions.ToExitCode*> is a default mapping that a script calling your app can rely on, and you are free to choose your own.

## Watch, stop and continue

Run the app with `dotnet run`. The display takes the whole terminal and redraws four times a second. About five seconds in, while `store` is halfway, it shows:

```text
Dagline  basic sample  journal 20261111T041200Z-a3f9c2 · run 1           50%  elapsed 5s
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
✓ 1  ● 1                                                                        cap 1/16
── Groups ──────────────────────────────────────────────────────────────────────────────
● shard-1 ✓● 1/2
── Working (1) ─────────────────────────────────────────────────────────────────────────
● shard-1/store  ━━━━━━━━──────── 20 of 40  storing entities                          2s
```

The header names the journal and the run of it, how much of the run has settled and how long it has taken, then a bar and a count per status. Below it, one row per group with a cell per task, and the tasks that are working now with their progress. [The terminal display](terminal-ui.md) explains every pane.

Now press Ctrl+C while `store` is running. A banner under the header says the run is stopping. `store` ends at once, because its delay was passed the token, and the display gives way to the summary on the ordinary screen, where it stays in your scrollback:

```text
────────────────────────────────────────────────────────────────────────────────────────
Run stopped  5s · journal 20261111T041200Z-a3f9c2 · run 1
✓ 1 succeeded  ■ 1 cancelled

Cancelled (1)
■ shard-1/store

A run that continues journal 20261111T041200Z-a3f9c2 has 1 task left to run.
```

The app exits with 2. Run it again. The policy finds the journal unfinished and continues it, so `load` is an *earlier success*: it does not run, and its tick in the groups row is drawn dimmer. Only `store` runs:

```text
Dagline  basic sample  journal 20261111T041200Z-a3f9c2 · run 2           50%  elapsed 1s
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
✓ 1 (1 earlier)  ● 1                                                            cap 1/16
```

This time let it finish. The summary reads `Run complete` with `✓ 2 succeeded (1 earlier)`, and the app exits with 0. The journal is now finished, so the next run starts a new one.

Last, redirect the output: `dotnet run > run.txt`. Standard output is not a terminal, so `SpectreReporter.Auto` prints the run as plain text instead, one timestamped line per thing that happened, which is what a log file or a build server wants.

## Next

[Concepts](concepts.md) walks through the ideas behind what you just used: the graph, statuses, the run outcome, stopping, journals and reporters.

For a full example, the `Batch` sample, [`samples/Dagline.Samples.Batch`](https://github.com/cquillen/dagline/tree/main/samples/Dagline.Samples.Batch), is a small complete app that runs a batch of about 1,500 tasks over fake external systems, with faults to inject, continuing journals and subsets. [Its README](https://github.com/cquillen/dagline/blob/main/samples/Dagline.Samples.Batch/README.md) has the things to try.
