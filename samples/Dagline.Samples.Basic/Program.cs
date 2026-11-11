using Dagline;

// The graph: one group that holds two tasks. The second requires the first, so it runs only once the first has succeeded.
var builder = new GraphBuilder();
var shard = builder.Group("shard-1");

var load = shard.Task("load", async context =>
{
    context.Status("loading entities");
    await Task.Delay(TimeSpan.FromMilliseconds(300), context.TimeProvider);
    context.Message("loaded 40 entities");
});

shard.Task("store", async context =>
{
    for (var stored = 10; stored <= 40; stored += 10)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(100), context.TimeProvider);
        context.Count(stored, total: 40);
    }

    context.Message("stored 40 entities");
}).Requires(load);

var graph = builder.Build();

// The run: no journal, and the plain-text reporter printing to standard output while it happens.
var result = await Runner.RunAsync(graph, new RunOptions
{
    Journal = Journal.None,
    Reporters = [new PlainTextReporter(Console.Out)],
});

// The process exits with 0 when every task succeeded and with 1 when one did not.
return result.Outcome.ToExitCode();
