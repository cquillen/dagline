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

An id that is not in the id format is refused at once with `ArgumentException`, so an id can never name a file outside the directory. An id the directory has no journal for makes `RunAsync` throw <xref:Dagline.JournalNotFoundException> before the run exists: no task starts, no event is published and no reporter is called.

## The app always chooses

Dagline never picks a journal for you, not even when exactly one is unfinished. Several operators often work side by side on different parts of the same work, and an operator may leave a journal behind and start over. Whichever journal Dagline guessed, a wrong guess would either repeat hours of finished work or continue work nobody meant to. So the choice is your app's: a prompt, a command-line flag or a rule of your own.

There is no default directory either, so continuing never depends on where the command was launched. Give each kind of graph a directory of its own: a journal continued with an unrelated graph is not an error, and every task would simply run as new.

## What a continuing run keeps and runs again

A task whose latest settle in the journal is a success is an **earlier success**:

- It is succeeded from the start of the run, with the message, detail lines and warnings it succeeded with and the time it succeeded. It has no start time.
- It does not run, it never changes status, and no event is published for it. It is in the run's first snapshot, where <xref:Dagline.NodeSnapshot.IsEarlierSuccess> is true.
- A dependency of either kind on it is met from the start.

Every other task starts pending and runs: one that failed, was blocked or was cancelled, one the earlier run never reached, and one that is new to the graph. It gets nothing from earlier runs except its [checkpoint](writing-tasks.md#checkpoints), if it saved one and has not succeeded since.

Groups follow from their tasks. A group made only of earlier successes is settled from the start and gets no event. A group that mixes earlier successes with tasks that settle in this run gets one <xref:Dagline.GroupSettled> when its last task settles, and <xref:Dagline.GroupSettled.EarlierSuccesses> says how many of its tasks were earlier successes.

Earlier successes count as succeeded everywhere a count is given, and in the run outcome. <xref:Dagline.Snapshot.EarlierSuccesses> and <xref:Dagline.NodeSnapshot.EarlierSuccesses> count them apart, so a reporter can show how much this run did. A continuing run with nothing left to do publishes only `RunStarted` and `RunEnded` and is complete.

A journal whose latest run was complete can be continued like any other. With an unchanged graph the run is complete at once, and if the graph gained a path, that task runs.

Each run chooses its own cap and limit sizes. A run that continues a journal may use different ones from the runs before it.

## The file

A journal is one UTF-8 text file with one JSON object per line, appended to and never rewritten. It records each run's start, each task's start, each task's settle with its whole outcome, each [checkpoint](writing-tasks.md#checkpoints) saved or cleared, and each run's end. Progress, waiting and the status of a group are never recorded.

Every settle reaches the disk before any task that depends on it starts and before its event is published, and a run's end reaches the disk before `RunAsync` returns. A checkpoint reaches the disk before the save returns. A failure and every task it blocks share one write.

The file is readable text, so you can look into it with any tool when something went wrong. Its format is not a stable contract, and may change between early releases. Anyone who can read the directory can read the file, so a task's outcome and its checkpoint must never hold secrets.
