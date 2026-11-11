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
    var shardGroup = builder.Group(shard.Name);

    foreach (var tenant in shard.Tenants)
    {
        var tenantGroup = shardGroup.Group(tenant.Name);
        var load = tenantGroup.Task("load", context => LoadAsync(tenant, context));
        tenantGroup.Task("verify", context => VerifyAsync(tenant, context)).Requires(load);
    }
}
```

Groups nest to any depth, and tasks may sit at the root beside them. A task is always a leaf: only a group has children.

A group may stay empty. When a list comes back with nothing in it, the loop adds no children and the graph still builds.

Nesting never orders work. Two tasks in one group run at the same time unless a dependency says otherwise.

To add more children to a group later, keep its handle. Calling `Group` again with the same segment does not reopen the group.

## Paths and segments

Every node has a path: the segment of each group that holds it, from the root down, then its own segment, joined by `/`. The task added as `load` in the group `tenant-a` in the group `shard-3` has the path `shard-3/tenant-a/load`.

The path is the identity of the node. The same path means the same work in every run, so choose every segment yourself from something stable, such as a name or a key, and not from a position in a list.

A segment is one or more of the characters `A-Z`, `a-z`, `0-9`, `.`, `_` and `-`. Two children of one parent may not share a segment, nor have segments that differ only by case.

<xref:Dagline.Node.Path> on a handle gives the path as a <xref:Dagline.NodePath>. `NodePath.Parse` reads one from text, and `ToString` writes it back.

## Titles

A segment is an identity, so it is short and stable. When people need a readable name as well, give the node a title:

```csharp
var shardGroup = builder.Group("shard-3").Title("Shard 3 (west)");
shardGroup.Task("load", LoadAsync).Title("Load entities");
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
    loads.Group(shard.Name).Task("load", context => LoadAsync(shard, context));
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
    var shardGroup = builder.Group(shard.Name);
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

Declaration order is not a priority. It never makes a task wait: only a dependency does.

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

A snapshot gives each group its status and <xref:Dagline.NodeSnapshot.Counts>: how many of the tasks beneath it have each status. When the last task beneath a group settles, reporters receive a <xref:Dagline.GroupSettled> event with the group's status and counts. Nested groups that settle together are reported deepest first. A group that was settled from the start gets no event.
