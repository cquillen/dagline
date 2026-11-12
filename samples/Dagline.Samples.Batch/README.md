# Dagline.Samples.Batch

A small, complete command-line app that runs a long batch with Dagline: ten shards of 75 tenants, a `load` and a `validate` task for each tenant, a `jobs` group gated on every shard that triggers one job per shard in a scheduler and waits for it, and a `report` task after the jobs. That is about 1,500 tasks and two and a half minutes at the default size. Shard, tenant, entity and job are stand-ins for whatever your own work is split into and starts elsewhere: the entity store and the job scheduler are fakes in [`Fakes.cs`](Fakes.cs), and the scheduler keeps its jobs as files, so they carry on while the app is down. The program is [`Program.cs`](Program.cs), the task bodies are in [`BatchTasks.cs`](BatchTasks.cs), and [`Dagline.Samples.Batch.Tests`](../Dagline.Samples.Batch.Tests) tests those bodies with `Dagline.Testing`. The sample never prompts.

Run every command below from the root of the repository.

## Options

```text
dotnet run --project samples/Dagline.Samples.Batch -- [options]

  --shard 1,3      only these shards (default: all 10)
  --tenants <n>    tenants per shard (default: 75)
  --new            start a new journal even if an unfinished one matches
  --fault <name>   validate | poll | job; repeatable
  --fast           fake delays and wait intervals a thousand times shorter
  --plain          force the plain-text reporter
  --ascii          ASCII glyphs
  --dir <path>     journals and the fake scheduler's files (default: ./batch-data)
```

The faults:

- `validate`: the first tenant of the first shard has bad entities, so its `validate` fails.
- `poll`: the first status call of the first job throws.
- `job`: the last job ends in error.

A run without `--fault` is the operator having fixed things. Before each run the sample prints one line saying whether it starts a new journal or continues the newest unfinished one for the same shards.

## Exit codes

| Code | Meaning |
| ---- | ------- |
| 0 | Every task succeeded. |
| 1 | The run ended with a task that did not succeed. |
| 2 | The run was stopped. |
| 64 | A bad option, a graph that fails to build, options the run refuses, or a journal that is in use or cannot be opened. The message is on standard error. |
| 70 | A journal write failed. The message is on standard error. |

## Things to try

### 1. A clean run

```shell
dotnet run --project samples/Dagline.Samples.Batch
```

Look for the validations filling the shard rows, at most four per shard, and no job starting until the last validation has passed. Then the jobs run three at a time, each waiting on its job id. About 30 tenants always warn, so the summary at the end lists their warnings. The run exits with 0. Add `--fast` to see the whole run in a few seconds, or `--tenants 10` for a smaller one at full speed.

### 2. A failed validation, then the fix

```shell
dotnet run --project samples/Dagline.Samples.Batch -- --fast --fault validate
dotnet run --project samples/Dagline.Samples.Batch -- --fast
```

The first run starts a new journal. `shard-1/tenant-1-001/validate` fails with its bad entities as detail lines, all ten jobs are blocked by it, and `report` still runs and says `0 of 10 jobs succeeded`. It exits with 1.

The second run says `Continuing journal ...`. Every validation that passed is an earlier success, the failed one runs again and passes, the jobs run, and the earlier success of `report` is set aside, so it runs again and says `10 of 10 jobs succeeded`. It exits with 0.

### 3. Ctrl+C during the jobs, then again

```shell
dotnet run --project samples/Dagline.Samples.Batch -- --new --tenants 10
```

Wait until jobs show in the waiting pane, then press Ctrl+C once. The display shows the stop, the waiting job tasks are cancelled, and the run exits with 2. The fake jobs keep running in the scheduler's files. Run the same command without `--new`:

```shell
dotnet run --project samples/Dagline.Samples.Batch -- --tenants 10
```

It continues the journal, the validations are earlier successes, and the cancelled job tasks wait on the same job ids as before instead of triggering new jobs; one that finished while the app was down succeeds at its first check. The scheduler refuses a second trigger of a job, so exit code 0 here means every job task re-attached.

### 4. Two operators on different shards

In one terminal:

```shell
dotnet run --project samples/Dagline.Samples.Batch -- --shard 1,2 --tenants 20
```

And while it runs, in a second terminal:

```shell
dotnet run --project samples/Dagline.Samples.Batch -- --shard 3 --tenants 20
```

Each run has a journal of its own, described as `shards 1,2` and `shards 3`, and each display counts only its own shards: the gate before the jobs holds only that run's validations, and its `report` counts only its own jobs. Run either command again to continue its journal, or let both finish; both exit with 0.

## Where it is explained

- [Waiting on external systems](https://cquillen.github.io/dagline/articles/waiting-on-external-systems.html): the job task's trigger-or-re-attach pattern, clearing a checkpoint, and the window the sample leaves open.
- [Journals](https://cquillen.github.io/dagline/articles/journals.html): choosing between a new journal and an unfinished one, and what is kept after a crash.
- [Running part of the work](https://cquillen.github.io/dagline/articles/running-part-of-the-work.html): the shard filter, the gate on a group, and one journal per subset.
- [Testing your tasks](https://cquillen.github.io/dagline/articles/testing-your-tasks.html): the harness the sample's tests use.
