# Only an *after* dependency sets aside an earlier success

When a run continues a journal, a task the journal records as succeeded is an earlier success: it has the status succeeded from the start, with its whole outcome (message, detail lines, warnings and the time it succeeded), and it does not run again. Every other task starts as pending. That covers a task that failed, was blocked or was cancelled, one that was never reached, and one that was started but never settled because the process died. The only thing such a task gets from earlier runs is its checkpoint. Its earlier failure stays in the journal as history and plays no part in the new run.

There is one exception. A task that is *after* something keeps its earlier success only if everything it is after is itself an earlier success. Otherwise the continuing run sets the success aside and the task runs again once its dependencies are met. A *requires* dependency never sets an earlier success aside.

Two cases show why the kinds differ:

- **After.** `summary` is *after* `shard-3`. A task under `shard-3` failed, so `summary` ran and succeeded with that failure in view. In the continuing run the failed task runs again, and what `summary` produced is stale. An *after* task exists to act on how its upstreams ended, so it has to act again when they end again.
- **Requires.** `shard-3/trigger` *requires* `shard-3/validate`, and both succeeded. Before the next run the list that shapes the graph gains an entry, so `validate` has a new task beneath it. If `trigger` ran again it would start its external work a second time, because its checkpoint was discarded when it succeeded (ADR 0006). Dagline running finished work again unasked is the more dangerous surprise.

A set-aside is written to the journal at the start of the run that makes it. From then on the task counts as not succeeded, for later runs and for the listing, until it succeeds again. Without that entry a stale success would come back: in a first run a task fails and `summary` succeeds; a second run repairs the task and is stopped before `summary` runs again; in a third run everything `summary` is after is an earlier success, and the first run's `summary` would be kept.

The rule is applied in dependency order. A task whose success was set aside is not an earlier success for the tasks that are *after* it, so they are set aside too. A task that *requires* it keeps its own earlier success.

Path equality is the only identity between a graph and a journal (ADR 0001), and the two will differ, because the lists that shape a graph are fetched before every build:

- **A path in the graph and not in the journal** is an ordinary pending task.
- **A path in the journal and not in the graph** stays in the journal untouched, checkpoint included. It is not part of the run, is not displayed and does not count towards the run outcome. If the path returns in a later run, its earlier success or its checkpoint applies again.
- **A path that was a task and is now a group, or the reverse,** is treated as not in the graph.
- **A rename cannot be seen.** It is a removal and an addition.

None of these is an error. The run reports how many journal paths it did not find in the graph, so that a reporter can show it.

## Considered options

- **Setting aside every earlier success downstream of anything that runs again, through *requires* as well.** Rejected because a change in a list would then start finished external work a second time, with its checkpoint gone.
- **Never setting an earlier success aside.** Rejected because an *after* task that summarises or cleans up would stay stale for the life of the journal.
- **Deciding afresh in every run without recording the set-aside.** Rejected because of the three-run case above.
- **A per-task policy to run in every run**, for a check that the world has not changed. Left out of the first release. Such a check can be made by the app before it starts the run, and under the rule above the policy could only ever be a gate, since the tasks that require it would keep their earlier successes.
- **Treating a difference between graph and journal as an error.** Rejected because the difference is ordinary.

## Consequences

- An author who wants finished work to run again after its upstreams changed starts a new journal.
- Whether a journal is finished is judged against the graph of its latest run. Paths that run did not find do not count.
- A task that was running when the process died is indistinguishable, to the next run, from one that was never reached, apart from its checkpoint.
- The warnings of an earlier success are shown again in every continuing run, so the summary of the last run covers the whole journal.
- A checkpoint whose path has left the graph is kept until the journal is deleted.
