# Defining a graph

A graph is a tree of nodes plus the dependencies between them. A node is a task, which runs your code, or a group, which holds tasks and other groups. You build a graph with plain statements, and it does not change once it is built.

## The builder

<xref:Dagline.GraphBuilder> collects nodes. Each call that adds a node returns a handle, and you declare dependencies on handles:

```csharp
using Dagline;

var builder = new GraphBuilder();
var prepare = builder.Task("prepare", PrepareAsync);
var publish = builder.Task("publish", PublishAsync).Requires(prepare);
var graph = builder.Build();
```

A task is a segment and a body. The body is an async delegate over a <xref:Dagline.TaskContext>, so a method, a lambda and a method group all work.

A dependency is declared on a handle and never on a string, so a misspelt upstream does not compile.

<xref:Dagline.GraphBuilder.Build*> returns a <xref:Dagline.Graph>. A graph is immutable and can be run any number of times.

## Groups are scopes

<xref:Dagline.GraphBuilder.Group*> adds a group and returns its handle. The handle is also the scope for the group's children: call `Group` and `Task` on it to add nodes beneath it. Fan-out over a list is an ordinary loop, and a tree is nested loops, with no lambdas:

```csharp
var builder = new GraphBuilder();

foreach (var shard in shards)
{
    var shardGroup = builder.Group(shard.Key);

    foreach (var tenant in shard.Tenants)
    {
        var tenantGroup = shardGroup.Group(tenant.Key);
        var load = tenantGroup.Task("load", context => LoadAsync(tenant, context));
        tenantGroup.Task("verify", context => VerifyAsync(tenant, context)).Requires(load);
    }
}
```

Groups nest to any depth, and tasks may sit at the root beside them. A task is always a leaf: only a group has children.

A group may stay empty. When a list comes back with nothing in it, the loop adds no children and the graph still builds.

Nesting never orders work. Two tasks in one group run at the same time unless a dependency says otherwise.

To add more children to a group later, keep its handle. Calling `Group` again with the same segment does not reopen the group: it adds a duplicate path, which is a [build error](#build-errors).

## Paths and segments

Every node has a path: the segment of each group that holds it, from the root down, then its own segment, joined by `/`. The task added as `load` in the group `tenant-a` in the group `shard-3` has the path `shard-3/tenant-a/load`.

The path is the identity of the node. The same path means the same work in every run, so choose every segment yourself from something stable and unique among its siblings, such as a key your data already has, and not from a position in a list. The examples here use `shard.Key` and `tenant.Key` for such a key.

A display name is rarely a good segment: it can hold a space or another character a segment may not, two of them can be the same, and it can change. Keep it for the [title](#titles).

A segment is one or more of the characters `A-Z`, `a-z`, `0-9`, `.`, `_` and `-`. Two children of one parent may not share a segment, nor have segments that differ only by case. A segment that breaks these rules is a [build error](#build-errors).

<xref:Dagline.Node.Path> on a handle gives the path as a <xref:Dagline.NodePath>. `NodePath.Parse` reads one from text, and `ToString` writes it back.

## Titles

A segment is an identity, so it is short and stable. When people need a readable name as well, give the node a title:

```csharp
var shardGroup = builder.Group("shard-3").Title("Shard 3 (west)");
shardGroup.Task("load", LoadAsync).Title("Load entities");
```

In a loop, take the segment from the key and the title from the name:

```csharp
foreach (var shard in shards)
{
    var shardGroup = builder.Group(shard.Key).Title(shard.Name);
    shardGroup.Task("load", context => LoadAsync(shard, context));
}
```

A title may hold any text and need not be unique. It is never part of the path, so changing a title does not change which work a path means. A snapshot carries it as <xref:Dagline.NodeSnapshot.Title>, which is null for a node with no title.

## Dependencies

A dependency is a directed link from a dependent node to an upstream node. It orders work and carries no data. There are two kinds:

| Kind | Declared with | Met when |
| --- | --- | --- |
| Requires | `Requires` | The upstream has succeeded. |
| After | `After` | The upstream has settled, whatever the outcome. |

Use *requires* for work that needs its upstream to have worked. Use *after* for work that must run whatever happened upstream, such as cleanup or a summary:

```csharp
var load = builder.Task("load", LoadAsync);
var publish = builder.Task("publish", PublishAsync).Requires(load);
builder.Task("report", ReportAsync).After(publish);
```

A task with no dependency is ready as soon as the run starts. A task with several is ready once all of them are met.

### Against a group

The upstream of a dependency may be a group. It then stands for every task beneath that group, at any depth:

```csharp
var loads = builder.Group("loads");
foreach (var shard in shards)
{
    loads.Group(shard.Key).Task("load", context => LoadAsync(shard, context));
}

builder.Task("publish", PublishAsync).Requires(loads);
```

`publish` becomes ready once every task beneath `loads` has succeeded. You never list the members by hand, and the dependency follows the list as it grows or shrinks.

A dependency on an empty group is met at once.

### On a group

A dependency may also be declared on a group. It then applies to every task beneath that group, at any depth, whether the task was added before or after the declaration:

```csharp
var schema = builder.Task("schema", MigrateSchemaAsync);
var loads = builder.Group("loads").Requires(schema);
loads.Task("entities", LoadEntitiesAsync);
loads.Task("regions", LoadRegionsAsync);
```

Neither `loads/entities` nor `loads/regions` becomes ready until `schema` has succeeded.

A snapshot shows a dependency on the node it was declared on. Here it is on `loads`, and not on the two tasks.

### A collection of handles

`Requires` and `After` also take a collection of handles, and declare one dependency for each. An empty collection declares nothing, so it is always valid.

This is how to write a gate: a task that waits for a set of groups chosen at run time.

```csharp
var builder = new GraphBuilder();
List<GroupNode> shardGroups = [];

foreach (var shard in shards.Where(shard => shard.IsEnabled))
{
    var shardGroup = builder.Group(shard.Key);
    shardGroup.Task("load", context => LoadAsync(shard, context));
    shardGroups.Add(shardGroup);
}

builder.Task("publish", PublishAsync).Requires(shardGroups);
builder.Task("report", ReportAsync).After(shardGroups);
```

The gate shrinks with the list. When the filter leaves no shard, `publish` and `report` have no dependency and are ready at the start.

### The same upstream twice

Declaring a dependency twice is the same as declaring it once.

A task can end up with both kinds on one upstream, for example *after* from a group that holds it and *requires* of its own. It is then treated as *requires*. Declaring on a group and again on one of its members is never an error.

### When an upstream fails

The two kinds differ in what a failure upstream does:

- A task that *requires* a failed task is **blocked**: it will not run in this run. That happens at once, and it cascades: a task that requires a blocked task is blocked too.
- A task that runs *after* a failed or blocked task still runs, once that upstream has settled.

A task that requires a group is blocked as soon as one task beneath the group fails or is blocked, without waiting for the rest of the group. A task with both kinds on one upstream is treated as *requires*, so it is blocked.

Every blocked task records the path of the failed task that started the cascade. [Running and stopping](running-and-stopping.md#when-a-task-fails) covers what a failure leaves in the snapshot.

## Limits

A limit is a named ceiling on how many of the tasks that hold it may be running at once. Name one to bound heavy work where it is declared, such as the tasks that write to one database:

```csharp
foreach (var shard in shards)
{
    builder.Task($"load-{shard.Key}", context => LoadAsync(shard, context)).Limit("database");
}
```

The graph only names a limit. Its size is an option of the run, so tuning can change from one run to the next while the graph stays the same work. [Sizing the max running tasks and limits](running-and-stopping.md#sizing-the-max-running-tasks-and-limits) covers the sizes, and a run that gives no size for a limit the graph names is refused before any task starts.

A limit name is any text that is not empty. Names are compared exactly, so `database` and `Database` are two limits. Call `Limit` again to name another limit on the same node. Naming one limit twice is the same as naming it once.

### On a group

A limit named on a group applies to every task beneath that group, at any depth, whether the task was added before or after the limit was named:

```csharp
var loads = builder.Group("loads").Limit("database");
loads.Task("entities", LoadEntitiesAsync);
loads.Task("archive", LoadArchiveAsync).Limit("files");
```

Each task beneath `loads` holds a slot of `database` of its own. The group holds none, because a group never runs code.

A task's limits are its own plus those of every group that holds it. Here `loads/entities` holds `database`, and `loads/archive` holds `database` and `files`.

A snapshot shows a limit on the node it was named on, as <xref:Dagline.NodeSnapshot.Limits>. Here `database` is on `loads`, and not on the two tasks.

### Slots

A limit of size 3 has three slots. A task takes one slot of each of its limits when it starts and gives them back when it settles. It holds them for all of that time, including while it waits on another system.

A ready task starts only when every one of its limits has a free slot. It takes all its slots at once or takes none, so it never holds one slot while it waits for another. A ready task that does not fit is *held*: it stays ready, the tasks behind it are still tried, and it is tried again whenever a task settles.

## Declaration order

Declaration order is a depth-first walk of the tree: each group comes before its children, and the children of each group are in the order they were added to that group. What was added elsewhere in between does not matter:

```csharp
var builder = new GraphBuilder();
var a = builder.Group("a");
a.Task("x", XAsync);
builder.Task("b", BAsync);
a.Task("y", YAsync);
```

The order is `a`, `a/x`, `a/y`, `b`.

Dagline uses this one order everywhere:

- A <xref:Dagline.Graph> is a list of its node handles in declaration order, and <xref:Dagline.Graph.TryGetNode*> finds one by path.
- A snapshot lists its nodes in declaration order.
- Tasks that become ready at the same moment do so in declaration order, and ready tasks start in it.

Declaration order is not a priority. It never makes a task wait: only a dependency, a full limit or reaching the max running tasks does.

## Build errors

<xref:Dagline.GraphBuilder.Build*> checks the whole graph before it builds anything. A graph that cannot run is refused there, before any task starts.

Adding a node or declaring a dependency never throws because of what the graph then looks like. A handle always comes back, even for a segment that is invalid or already taken, so the code that builds the graph runs to its end. `Build` then reports every problem at once, in one <xref:Dagline.GraphException>:

```csharp
try
{
    return builder.Build();
}
catch (GraphException exception)
{
    foreach (var problem in exception.Problems)
    {
        Console.Error.WriteLine(problem.Message);
    }

    return null;
}
```

Each <xref:Dagline.GraphProblem> has a one-line `Message` that names every node involved by its path, and the `Path` of the node the problem is about. The exception's own message lists every problem, one to a line, so an app that lets it escape still prints them all.

| Problem | Example message |
| --- | --- |
| An invalid segment | `'shard 1' is not a valid segment for a node at the root: a segment is one or more of A-Z a-z 0-9 . _ -` |
| A duplicate path | `'shard-1/load' was added 2 times: a path is one node.` |
| Siblings that differ only by case | `'shard-1/load' and 'shard-1/Load' differ only by case: siblings must differ by more than case.` |
| A handle from another builder | `'publish' requires 'load', a handle from another builder.` |
| A node that depends on itself | `'load' requires itself.` |
| A dependency on an ancestor or a descendant | `'shard-1/load' requires 'shard-1', its own ancestor.` |
| A cycle | `These dependencies form a cycle: 'load' requires 'verify', 'verify' runs after 'load'.` |

The messages are written for people, and their wording may change between releases. Do not match on it.

### Problems with nodes

- **An invalid segment.** A segment is one or more of `A-Z`, `a-z`, `0-9`, `.`, `_` and `-`. An empty segment, a space and a `/` are all invalid. The node has no valid path, so the problem's `Path` is null and the message names the group the node was added to. The nodes beneath a group with an invalid segment are not reported again.
- **A duplicate path.** Two children of one parent were added with the same segment. That includes a task and a group on one path, and a group added twice: calling `Group` again does not reopen a group, so keep its handle to add more children. A path added several times is one problem.
- **Siblings that differ only by case.** `load` and `Load` under one parent are refused, so that paths stay distinct wherever they are used as identities, including on a file system that ignores case. The same two segments under different parents are fine.

### Problems with dependencies

- **A handle from another builder.** A dependency joins two nodes of one graph. An upstream that a different `GraphBuilder` returned is refused.
- **A node that depends on itself.**
- **A dependency between a node and its own ancestor or descendant.** A task cannot depend on a group that holds it, because the group stands for the task itself among others. A group cannot depend on a node beneath it, because that node would inherit a dependency on itself.

### Cycles

A cycle is a chain of dependencies that leads back to where it started, so that none of its tasks could ever become ready. Both kinds of dependency count. The message spells out the whole cycle, one dependency after another, starting from the one declared first, so you can see which one to remove.

A group stands for whatever is beneath it, and a dependency declared on a group applies to everything beneath it. A cycle can therefore pass through a group:

```csharp
var loads = builder.Group("loads");
var entities = loads.Task("entities", LoadEntitiesAsync);
var publish = builder.Task("publish", PublishAsync).Requires(loads);
entities.After(publish);
```

`Build` reports the cycle as `'publish' requires 'loads', 'loads/entities' runs after 'publish'`: `publish` waits for everything beneath `loads`, and `loads/entities` is beneath it.

A cycle through a group is reported even while the group is empty. The dependency is on the group, not on a list of the members it has today.

Where several cycles share dependencies, `Build` reports at least one of them and not always each of them. Once those are fixed, the next call to `Build` reports any that are left.

### A builder is single use

Once `Build` has returned a graph, that graph cannot change. The builder and every handle it made throw `InvalidOperationException` from any call that would change it: `Group`, `Task`, `Title`, `Limit`, `Requires`, `After`, and a second `Build`. The handles stay valid to read.

A null argument is a different kind of mistake. It throws `ArgumentNullException` at the call, before and after `Build`.

## Groups in a run

A group never runs code, so its status is derived from the tasks beneath it at any depth.

While any of those tasks has not settled, the group is:

- **running** if any task is running,
- else **ready** if any task is ready,
- else **pending**.

Once all of them have settled, the group is settled too. It is:

- **failed** if any task failed,
- else **cancelled** if any task was cancelled,
- else **blocked** if any task is blocked,
- else **succeeded**.

An empty group is succeeded from the start of the run.

A snapshot gives each group its status and <xref:Dagline.NodeSnapshot.Counts>: how many of the tasks beneath it have each status, with their total, the settled, and the waiting and working among the running. A group also answers for the tasks beneath it with <xref:Dagline.NodeSnapshot.WarningCount>, <xref:Dagline.NodeSnapshot.IsWaiting> and <xref:Dagline.NodeSnapshot.IsWorking>: see [Reporters](reporters.md#counts). When the last task beneath a group settles, reporters receive a <xref:Dagline.GroupSettled> event with the group's status and counts. Nested groups that settle together are reported deepest first. A group that was settled from the start gets no event.
