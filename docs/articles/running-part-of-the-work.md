# Running part of the work

Operators often run part of the work: one shard, a handful of tenants, or several operators each taking a part of their own side by side. Dagline has no setting for this. Your app builds a smaller graph from shorter lists, and that graph is the whole of the run. Dagline never knows that a graph is a subset of anything, so no rule anywhere is specific to subsets.

## Build the graph from shorter lists

Your graph-building code already loops over lists it fetched before the build. Filter those lists with the app's own options, and the same code builds the part the operator asked for:

```csharp
var shards = (await FetchShardsAsync()).Where(shard => options.Shards.Contains(shard.Name)).ToList();

var builder = new GraphBuilder();
var validate = builder.Group("validate");
var trigger = builder.Group("trigger");
foreach (var shard in shards)
{
    validate.Task(shard.Name, context => ValidateAsync(shard, context));
    trigger.Task(shard.Name, context => TriggerAsync(shard, context));
}

trigger.Requires(validate);
builder.Task("summary", SummariseAsync).After(trigger);
var graph = builder.Build();
```

The options that choose a subset are yours, and you thread them through this code. The snapshot, a group's counts, the summary and the run outcome all cover the graph the run was given, and nothing outside it. One set of limit sizes serves every subset, since a size for a limit the graph does not name is ignored.

## A gate is a dependency on a group

Above, every task under `trigger` requires the group `validate`, so nothing is triggered until every validation in the graph has passed. Because the dependency is on a group, it shrinks with the lists: in a run of shard 3 alone, the validations of shard 3 gate the triggers of shard 3. Write a gate that must hold in every subset this way, on a group and not on each task.

A group that the filter emptied counts as met at once, as any empty group does.

## A dependency on a dropped node is a build error

There is no optional dependency that disappears when its upstream is not in the graph, because that would remove a gate without a word. If the filter dropped an upstream, the code that declares the dependency has no handle for it. Either keep the upstream in the graph or do not declare the dependency. A handle kept from another build is refused by <xref:Dagline.GraphBuilder.Build*> as a handle from another builder, with a <xref:Dagline.GraphException> before any task runs, as described in [Defining a graph](defining-a-graph.md#build-errors).

## One journal per subset

Nothing about a subset is recorded in a journal, and continuing a journal with a different subset is not an error: Dagline cannot tell a subset from a list that changed. The [description](journals.md#a-new-journal) of a journal, fixed when it is created, is the only label, so give each subset its own journal and say in the description which part it covers:

```csharp
var journal = options.Continue is { } id
    ? Journal.Continue("journals/export", id)
    : Journal.New("journals/export", $"shards {string.Join(", ", options.Shards)}");
```

Operators working on different parts then each have a journal of their own, and can tell them apart when they list the directory.

## A task in two subsets runs in each journal

Dagline does not coordinate across processes. A task that is in two operators' subsets runs once in each of their journals. If it starts external work, each run starts that work. A checkpoint guards against starting it twice within one journal and cannot be seen from another. Make such a task safe to run in every journal that has it, or leave it out of subset graphs.

## A journal can be finished with work left in it

A run's outcome covers only its own graph. Suppose a run of every shard ends incomplete, with failures in shards 3 and 4. A run of shard 3 alone then continues that journal, its failure is repaired, and the run is complete. The journal is now finished, although shard 4's failures are still in it: they are journal paths the smaller graph did not have, which the run leaves untouched.

The run reports them. <xref:Dagline.Snapshot.JournalPathsNotInGraph> counts the journal paths that are not a task of this run's graph, and <xref:Dagline.Snapshot.JournalPathsNotInGraphUnsucceeded> those of them that have not succeeded. Here the second count is the work shard 4 left behind. An app whose policy is to continue an unfinished journal and otherwise start a new one would pass this journal by, so show that count to the operator. If shard 4 later returns to the graph, continuing the journal runs exactly what is left of it.

The *after* rule reads against the graph of the run too. `summary` above is *after* the group `trigger`. Continued with shard 3 alone, its earlier success is kept when every trigger of shard 3 is an earlier success. Continued with every shard, `trigger` holds tasks that have not succeeded, so the success is set aside and `summary` runs again. See [Journals](journals.md#an-after-task-runs-again-when-what-it-is-after-runs-again).
