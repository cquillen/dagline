# Journals

A run of several hours gets stopped, fails part of the way, or loses its process. Without a record, the next run starts from nothing: every task that had succeeded runs again. A **journal** is that record. It holds a first run and every run that continues it, so that a later run keeps what already succeeded and does only what is left.

## Three ways to start

Every run makes one of three choices, through <xref:Dagline.RunOptions.Journal>:

```csharp
// A new journal in a directory you name. A later run can continue it.
new RunOptions { Journal = Journal.New("journals/export", "all shards") };

// Continue a given journal by its id.
new RunOptions { Journal = Journal.Continue("journals/export", "20261111T041200Z-a3f9c2") };

// No journal: nothing is recorded and the run cannot be continued.
new RunOptions { Journal = Journal.None };
```

A choice only describes what the run will do. Nothing touches the disk until <xref:Dagline.Runner.RunAsync*>, which creates or opens the journal before the run exists.

### A new journal

<xref:Dagline.Journal.New*> takes a directory and an optional description. The run creates the directory if it is missing, and the journal is one file in it. Its id is generated from the UTC time on the run's clock, a hyphen and six random lowercase hex digits, as in `20261111T041200Z-a3f9c2`, so ids sort by age, can be typed, and never collide between operators. The file is `<id>.journal`.

The description is free text that tells journals in the same directory apart, such as the part of the work this one covers. It is fixed when the journal is created. An empty one counts as none.

The id of a new journal is known once the run has started: every snapshot carries it in <xref:Dagline.Snapshot.Journal>, with the description and the run's number, counting from 1.

```csharp
var result = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New("journals/export", "all shards") });

var journal = result.Snapshot.Journal!;
Console.WriteLine($"journal {journal.Id} run {journal.Run}: {journal.Description}");
```

### Continuing a journal

<xref:Dagline.Journal.Continue*> takes the directory and an id. The run that continues a journal is its next run: its snapshot carries the same id and description, and a run number one higher.

An id that is not in the id format is refused at once with `ArgumentException`, so an id can never name a file outside the directory. To check an id an operator typed before you use it, call <xref:Dagline.Journals.IsValidId*>, which says whether text is in the id format and never throws:

```csharp
if (!Journals.IsValidId(typedId))
{
    Console.Error.WriteLine($"'{typedId}' is not a journal id: it looks like 20261111T041200Z-a3f9c2.");
    return 64;
}
```

An id the directory has no journal for makes `RunAsync` throw <xref:Dagline.JournalNotFoundException> before the run exists: no task starts, no event is published and no reporter is called.

## The app always chooses

Dagline never picks a journal for you, not even when exactly one is unfinished. Several operators often work side by side on different parts of the same work, and an operator may leave a journal behind and start over. Whichever journal Dagline guessed, a wrong guess would either repeat hours of finished work or continue work nobody meant to. So the choice is your app's: a prompt, a command-line flag or a rule of your own.

There is no default directory either, so continuing never depends on where the command was launched. Give each kind of graph a directory of its own: a journal continued with an unrelated graph is not an error, and every task would simply run as new.

### A policy with ListAsync

<xref:Dagline.Journals.ListAsync*> summarises every journal in a directory, oldest first, without needing a graph, so a policy for choosing one is a few lines. This one continues the newest journal that is not finished and that no other operator is running, and starts a new one otherwise:

```csharp
var listing = await Journals.ListAsync("journals/export");
var open = listing.Journals.LastOrDefault(j => !j.IsFinished && j.Ending is not JournalEnding.InUse);

var journal = open is null
    ? Journal.New("journals/export", "all shards")
    : Journal.Continue("journals/export", open.Id);
```

The result is a <xref:Dagline.JournalListing> with two lists: `Journals`, a summary of each journal that could be read, oldest first, and `Unreadable`, each journal file that could not be read (see [Journals that cannot be read](#journals-that-cannot-be-read)). A directory that does not exist gives two empty lists. Each <xref:Dagline.JournalSummary> carries enough to show an operator who must choose:

- `Id`, `Created` and `Description`: which journal it is.
- `Runs`: how many runs it has had, and `LatestRun`, a <xref:Dagline.JournalRunSummary>: when the latest started and ended, and the user and machine that ran it. `LatestRun` is null only when the journal's first run was never recorded.
- `Ending`: how the latest run ended (see [Endings](#endings)), and `IsFinished`: true only when the latest run was complete.
- `Counts`: how many tasks the journal records in each status, by the last status recorded for each path, as a <xref:Dagline.TaskCounts> read just as a snapshot's counts are, as in `summary.Counts[NodeStatus.Failed]` or `summary.Counts.Total`. A task whose success was [set aside](#an-after-task-runs-again-when-what-it-is-after-runs-again) since counts as pending, and one that started and never settled, as when its process was killed, counts as running. Paths that are no longer in your graph count too, since the listing has no graph. A journal records no waiting and no run is under way, so `Waiting` and `EarlierSuccesses` are always zero: every recorded success counts among the succeeded.

The listing reads each journal with the same code that a continuing run uses, so what it shows is what a run that continues the journal would find.

### Journals that cannot be read

One bad file never stops a listing. A journal in a format version this Dagline does not know, as one a later Dagline wrote may be, a file named like a journal that is not one or was overwritten, and a journal whose file or lock file the process cannot read, each go into <xref:Dagline.JournalListing.Unreadable> as an <xref:Dagline.UnreadableJournal>, with the file's `Path` and the `Exception` that says why: a <xref:Dagline.JournalFormatException> or a <xref:Dagline.JournalAccessException>. The other journals are listed as usual, so you can still continue or delete them. No journal is ever left out unseen, so show the unreadable ones to the operator:

```csharp
foreach (var unreadable in listing.Unreadable)
{
    Console.Error.WriteLine($"Passing over {unreadable.Path}: {unreadable.Exception.Message}");
}
```

Only a failure to read the directory itself makes `ListAsync` throw, with `JournalAccessException`.

The `Batch` sample's policy also matches the description, so each subset continues its own journal, and it does not skip a journal that another run holds, so that run is refused. [Its README](https://github.com/cquillen/dagline/blob/main/samples/Dagline.Samples.Batch/README.md#2-a-failed-validation-then-the-fix) shows a failed run and the run that continues it.

## What a continuing run keeps and runs again

A task whose latest settle in the journal is a success is an **earlier success**:

- It is succeeded from the start of the run, with the message, detail lines and warnings it succeeded with and the time it succeeded. It has no start time.
- It does not run, it never changes status, and no event is published for it. It is in the run's first snapshot, where <xref:Dagline.NodeSnapshot.IsEarlierSuccess> is true.
- A dependency of either kind on it is met from the start.

Every other task starts pending and runs: one that failed, was blocked or was cancelled, one the earlier run never reached, and one that is new to the graph. It gets nothing from earlier runs except its [checkpoint](writing-tasks.md#checkpoints), if it saved one and has not succeeded since.

Groups follow from their tasks. A group made only of earlier successes is settled from the start and gets no event. A group that mixes earlier successes with tasks that settle in this run gets one <xref:Dagline.GroupSettled> when its last task settles, and `EarlierSuccesses` on its <xref:Dagline.GroupSettled.Counts> says how many of its tasks were earlier successes.

Earlier successes count as succeeded everywhere a count is given, and in the run outcome. <xref:Dagline.TaskCounts.EarlierSuccesses>, on the run's <xref:Dagline.Snapshot.Counts> and on each node's <xref:Dagline.NodeSnapshot.Counts>, counts them apart, so a reporter can show how much this run did. A continuing run with nothing left to do publishes only `RunStarted` and `RunEnded` and is complete.

A journal whose latest run was complete can be continued like any other. With an unchanged graph the run is complete at once, and if the graph gained a path, that task runs.

Each run chooses its own max running tasks and limit sizes. A run that continues a journal may use different ones from the runs before it.

### An *after* task runs again when what it is after runs again

One rule decides whether an earlier success is kept, and it depends on the kind of dependency:

- A task that is ***after*** something keeps its earlier success only if everything it is after is itself an earlier success in this run. Against a group that means every task beneath it at any depth, and an empty group qualifies. Otherwise the success is **set aside**: the task starts pending and runs again once its dependencies are met.
- A task that only ***requires*** something never loses its earlier success because of a change upstream, not even when a group it requires gained a task that has not run.

An *after* task exists to act on how its upstreams ended, such as a summary or a cleanup, so it acts again when they end again and is never stale. A *requires* task is usually one that did real work, perhaps in an external system, and running it again unasked would do that work twice. If you want finished work to run again, start a new journal.

```csharp
var check = builder.Task("check", CheckAsync);
var summary = builder.Task("summary", SummariseAsync).After(check);    // set aside when check runs again
var publish = builder.Task("publish", PublishAsync).Requires(summary); // keeps its success
```

The rule is applied in dependency order. A task set aside is not an earlier success, so a task that is *after* it is set aside too, while a task that *requires* it keeps its own earlier success.

The run lists the paths it set aside in <xref:Dagline.Snapshot.SetAside>, in declaration order. A set-aside is written to the journal with the start of the run, on disk before any task starts. From then on the task has no success on record, in later runs too, until it succeeds again: if this run is stopped before the task runs, the next run does not bring back the stale success, even though everything the task is after is an earlier success by then.

Whether a group counts as an earlier success is judged by the graph of this run. A task *after* the group `shards` keeps its success when the journal is continued with the same shards, and loses it when `shards` gained a shard whose tasks have not succeeded.

## A graph that differs from its journal

The lists that shape a graph are fetched before every build, so the graph a continuing run is given will often differ from the one the journal was written with. None of the differences is an error. The only link between a graph and a journal is the path:

- **A path in the graph and not in the journal** is an ordinary pending task.
- **A path in the journal and not in the graph** is left untouched and is not part of the run. It has no node in the snapshot, no event and no effect on the run outcome. If a later run's graph has the path again, what the journal holds for it applies again, its earlier success included.
- **A path that is a task in one and a group in the other** counts as not in the graph. A rename is a removal and an addition.

The run counts what it left untouched, so that a reporter can show it:

- <xref:Dagline.Snapshot.JournalPathsNotInGraph> is how many paths the journal holds that are not a task of this run's graph.
- <xref:Dagline.Snapshot.JournalPathsNotInGraphUnsucceeded> is how many of those have not succeeded: the journal holds no success for the path, or its success was set aside.

The second count matters when a journal is continued with a smaller graph. The run outcome covers only the run's graph, so a run that completes can still leave work in the journal that never succeeded. That count is the signal. [Running part of the work](running-part-of-the-work.md) shows how this comes about.
## Endings

<xref:Dagline.JournalSummary.Ending> says how a journal's latest run ended:

| Ending | Meaning |
| --- | --- |
| `Complete` | Every task succeeded. The journal is finished. |
| `Incomplete` | A task failed or was blocked. |
| `Stopped` | The run was asked to stop before every task succeeded. |
| `InUse` | A run holds the journal right now. Continuing or deleting it throws `JournalInUseException`. |
| `Died` | The latest run recorded no end and nothing holds the journal: its process was killed, its machine went down, a journal write failed, or the journal's first run was never recorded. |

A journal that died can be continued like any other. Its successes are kept, and a task that was running when it died runs again.

## Holding a journal

A run holds its journal for its whole life through a lock file beside it, `<id>.lock`, and lets go of it when the run ends, however it ends. A second run on the same journal, in the same process or another, throws <xref:Dagline.JournalInUseException> before it exists, so two runs never write to one file. The lock is enforced by the operating system on Windows and is advisory elsewhere, which is enough because only Dagline needs to respect it.

The lock file stays behind after a run, and one left by a process that was killed is just as harmless: nothing holds it, so it stops no run, listing or deletion.

## Deleting a journal

<xref:Dagline.Journals.DeleteAsync*> removes a journal and its lock file:

```csharp
await Journals.DeleteAsync("journals/export", "20261111T041200Z-a3f9c2");
```

Its returned task faults with `JournalInUseException` while a run holds the journal, so it can never pull a file from under a run, and with <xref:Dagline.JournalNotFoundException> when the journal is not there. An id not in the id format is refused by the call itself, with `ArgumentException`.

Dagline never deletes or expires a journal itself. How long a record is kept is your app's policy: delete a journal once it is finished, after a number of days, or never. A finished journal can still hold work that never succeeded, in paths a smaller graph did not have, and deleting it gives that work up: check its `Counts` first if your app runs subsets (see [A journal can be finished with work left in it](running-part-of-the-work.md#a-journal-can-be-finished-with-work-left-in-it)).

## When a journal cannot be used

`RunAsync` checks a journal before the run exists, so each of these is thrown at the call, with no task started, no event published and no reporter called. The checks run in this order: the options, the lock, opening the journal, reading it.

- <xref:Dagline.JournalInUseException>: another run holds the journal. Wait for it to end, or choose another.
- <xref:Dagline.JournalNotFoundException>: the directory has no journal with the id.
- <xref:Dagline.JournalAccessException>: the journal, its lock file or its directory cannot be opened, created or read, for example for lack of permission. Its inner exception is the file system's own.
- <xref:Dagline.JournalFormatException>: the file is not a journal, a line other than the last cannot be read, or it was written in a format version this Dagline does not know. `Version` carries the version when the header gives one.

`JournalInUseException`, `JournalNotFoundException` and `JournalAccessException` carry the journal's `Id` and `Directory`, so a message can name the journal without parsing text. A `JournalAccessException` for the directory itself, which could not be created or listed, has no journal to name: its `Id` is null. `JournalFormatException` names the file in its message. `ListAsync` does not throw `JournalFormatException`, nor `JournalAccessException` for one journal's files: it lists that journal as [unreadable](#journals-that-cannot-be-read). All of them derive from <xref:Dagline.DaglineException>.

## The file

A journal is one UTF-8 text file with one JSON object per line, appended to and never rewritten. It records each run's start and the earlier successes it set aside, each task's start, each task's settle with its whole outcome, each [checkpoint](writing-tasks.md#checkpoints) saved or cleared, and each run's end. Progress, waiting and the status of a group are never recorded.

Every settle reaches the disk before any task that depends on it starts and before its event is published, and a run's end reaches the disk before `RunAsync` returns. A checkpoint reaches the disk before the save returns. A failure and every task it blocks share one write. A new journal's first line and its first run's start reach the disk before any task starts, and on Unix systems so does the journal's entry in its directory, and the entry of each directory Dagline created for it, so the file itself survives a crash.

The file is readable text, so you can look into it with any tool when something went wrong. Its first line names its format version. A later Dagline reads every format version an earlier release wrote, and an older one refuses a newer version with `JournalFormatException`: see [Compatibility](compatibility.md#journals). The layout of the lines is not an interface for your code: read a journal through `ListAsync` and continue it through `Journal.Continue`. Anyone who can read the directory can read the file, so a task's outcome and its checkpoint must never hold secrets.

## After a crash

When the process is killed, the machine crashes or the power goes, the journal keeps every success and every checkpoint whose entry reached the disk, which by the flushes above is every success that any later work started from and every checkpoint save that returned. Continue it as you would any other: the successes are earlier successes, and every other task runs, a task that was running when the process died included, with its checkpoint handed back.

The process may die in the middle of a write, leaving the last line of the file cut short. A journal opened to continue drops that line and trims the file back to the end of its last whole line before the run appends anything, so the file reads cleanly in every later run. The listing drops it too, without changing the file. A run that died recorded no end, so the listing shows its journal as `Died`.

This promise holds as far as the storage device honours a flush. A disk or controller that acknowledges a write it has only cached can still lose it in a power cut.

## When a journal write fails

If the journal can no longer be written during a run, because the disk is full or the file is gone, Dagline stops the run itself, ends it with a <xref:Dagline.JournalWriteException>, and writes nothing more to the journal. The journal then has no end recorded, lists as `Died`, and can be continued once the cause is fixed. [Running and stopping](running-and-stopping.md#when-a-journal-write-fails) describes what the run does.
