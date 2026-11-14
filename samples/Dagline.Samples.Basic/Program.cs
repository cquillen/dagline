using Dagline;
using Dagline.Spectre;

// The graph: one group that holds two tasks. The second requires the first, so it runs only once the first has succeeded.
var builder = new GraphBuilder();
var shard = builder.Group("shard-1");

var load = shard.Task("load", async context =>
{
    context.Status("loading entities");
    await Task.Delay(TimeSpan.FromSeconds(3), context.TimeProvider, context.CancellationToken);
    context.Message("loaded 40 entities");
});

shard.Task("store", async context =>
{
    context.Status("storing entities");
    for (var stored = 10; stored <= 40; stored += 10)
    {
        await Task.Delay(TimeSpan.FromSeconds(1), context.TimeProvider, context.CancellationToken);
        context.Count(stored, total: 40);
    }

    context.Message("stored 40 entities");
}).Requires(load);

var graph = builder.Build();

// The journal: continue the newest one that is not finished, so that a run you stopped carries on where it left off.
// When there is none, start a new one. The journals are kept beside the program.
var directory = Path.Combine(AppContext.BaseDirectory, "journals");
var journals = await Journals.ListAsync(directory);
var open = journals.LastOrDefault(j => !j.Finished && j.Ending is not JournalEnding.InUse);

var journal = open is null
    ? Journal.New(directory, "basic sample")
    : Journal.Continue(directory, open.Id);

// Ctrl+C: the first press stops the run, the second stops waiting for the running tasks to end.
using var stop = ConsoleStop.Hook();

// The run: the display on a terminal, or plain text when output is redirected.
var result = await Runner.RunAsync(graph, new RunOptions
{
    Journal = journal,
    Reporters = [SpectreReporter.Create()],
}, stop);

// The process exits with 0 when every task succeeded, 1 when one did not, and 2 when the run was stopped.
return result.Outcome.ToExitCode();
