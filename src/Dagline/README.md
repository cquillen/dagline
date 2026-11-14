# Dagline

A lightweight, in-process .NET library for running directed acyclic graphs (DAGs) of tasks in command-line apps. You define tasks and their dependencies in C#, and Dagline runs them in dependency order inside your app: no scheduler service, no database, no web server.

This is the core engine. It has no console or UI dependency. For the live terminal display, add [`Dagline.Spectre`](https://www.nuget.org/packages/Dagline.Spectre) as well.

```shell
dotnet add package Dagline
```

## Example

```csharp
using Dagline;

var builder = new GraphBuilder();
var shard = builder.Group("shard-1");

var load = shard.Task("load", async context =>
{
    await Task.Delay(TimeSpan.FromSeconds(3), context.TimeProvider, context.CancellationToken);
    context.SetMessage("loaded 40 entities");
});

shard.Task("store", async context =>
{
    await Task.Delay(TimeSpan.FromSeconds(1), context.TimeProvider, context.CancellationToken);
    context.SetMessage("stored 40 entities");
}).Requires(load);

var graph = builder.Build();

using var stop = ConsoleStop.Hook();

var result = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.None }, stop);

return result.Outcome.ToExitCode();
```

`store` requires `load`, so it starts only once `load` has succeeded. `ConsoleStop.Hook()` turns Ctrl+C and SIGTERM into a stop, and `ToExitCode()` maps the run outcome to 0 (complete), 1 (incomplete) or 2 (stopped). A [journal](https://cquillen.github.io/dagline/articles/journals.html) in place of `Journal.None` lets a later run carry on where a stopped one left off.

## Learn more

- [Getting started](https://cquillen.github.io/dagline/articles/getting-started.html): a whole app, with the terminal display and a journal, in ten minutes
- [Documentation and API reference](https://cquillen.github.io/dagline/)
- [Compatibility](https://cquillen.github.io/dagline/articles/compatibility.html): what stays stable from one release to the next
- [Changelog](https://github.com/cquillen/dagline/blob/main/CHANGELOG.md)
- [Source](https://github.com/cquillen/dagline)
