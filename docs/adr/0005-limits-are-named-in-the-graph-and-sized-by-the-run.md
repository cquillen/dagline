# Limits are named in the graph and sized by the run

A task names the limits it needs, and so can a group, which gives every task beneath it a slot of its own to hold. The graph never says how big a limit is. Sizes, and the cap, are options of the run, because they are tuning that may differ between a run and its resume while the graph stays the same work.

A task holds exactly one slot of each of its limits, its own and its ancestors', from the moment it starts until it settles. That includes the time it spends waiting on an external system, which may be hours. Releasing a slot during a wait was the alternative. It was not taken because the task would then have to queue a second time to get its slot back before carrying on, and there is one place where a task queues: ready. A consumer with many long waits sets a high cap and puts limits on the heavy work.

## Considered options

- **Sizes declared in the graph**, next to the names. Rejected because a size is not part of what the work is, and changing one would then mean changing the graph.
- **Slots released while a task waits.** Rejected for the reason above.
- **Weighted slots**, where one task takes several. Rejected as more than the first release needs.

## Consequences

- A limit that the graph names and the run gives no size is an error before any task starts. Every size, and the cap, must be at least 1.
- A size given for a limit that no task names is allowed and ignored, so one set of sizes can serve several graphs.
- The cap behaves as a limit every task holds. Its default is a fixed 16, not derived from the processor count, since the work is mostly waiting on other systems.
- A task takes all its slots at once or none, so it never holds one slot while it queues for another.
- Whenever a task settles, the ready tasks are tried in declaration order, and each one whose limits all have room starts. One that does not fit is skipped and never holds up those behind it. There are no priorities and no fairness rules, so a task that needs two busy limits can be passed over repeatedly.
