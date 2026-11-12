namespace Dagline.Samples.Batch;

/// <summary>
/// The sample's task bodies. Each is a named method that takes its fakes and the context, so that a test can run it
/// alone. Every delay is on the context's clock, through the fakes.
/// </summary>
public static class BatchTasks
{
    /// <summary>Fetches a tenant's entities and leaves a message with the count.</summary>
    public static async Task Load(string tenant, EntityStore store, TaskContext context)
    {
        context.Status("fetching entities");
        var entities = await store.FetchAsync(tenant, context.TimeProvider, context.CancellationToken);
        context.Message($"{entities.Count} entities");
    }

    /// <summary>Counts through a tenant's entities, warns for tenants seeded to warn, and fails on bad entities.</summary>
    public static async Task Validate(string tenant, EntityStore store, TaskContext context)
    {
        context.Status("validating entities");
        var entities = store.EntitiesOf(tenant);

        var validated = 0;
        foreach (var batch in entities.Chunk(500))
        {
            await store.CheckAsync(tenant, batch, context.TimeProvider, context.CancellationToken);
            validated += batch.Length;
            context.Count(validated, entities.Count);
        }

        if (EntityStore.WarningFor(tenant) is { } warning)
        {
            context.Warn(warning);
        }

        var bad = entities.Where(entity => entity.IsBad).Select(entity => $"entity {entity.Number}: a required field is empty").ToArray();
        if (bad.Length > 0)
        {
            throw context.Fail($"{bad.Length} of {entities.Count} entities are bad", bad);
        }

        context.Message($"{entities.Count} entities valid");
    }

    /// <summary>
    /// Triggers the named job, or re-attaches to the one the checkpoint names, then waits for it to finish. A job that
    /// ended in error clears the checkpoint, so that a later run triggers a fresh one.
    /// </summary>
    public static async Task Job(string name, FakeScheduler scheduler, TimeSpan interval, TaskContext context)
    {
        var job = context.Checkpoint;
        if (job == "")
        {
            // The window between the trigger and the save is left open: a process that dies in it triggers again on
            // the next run. "The window between the start and the save" in the Waiting on external systems article
            // shows how to close it.
            job = await scheduler.TriggerAsync(name, context.CancellationToken);

            // No token: a stop under way must not cancel the save, or the next run triggers a second job.
            await context.SaveCheckpointAsync(job);
        }

        context.Status($"job {job}");
        var status = JobStatus.Running;
        await context.WaitUntil(
            interval,
            async token =>
            {
                status = await scheduler.GetStatusAsync(job, token);
                return status is not JobStatus.Running;
            },
            $"job {job} to finish");

        if (status is JobStatus.EndedInError)
        {
            var log = await scheduler.GetLogAsync(job, CancellationToken.None);
            await context.ClearCheckpointAsync(CancellationToken.None);
            throw context.Fail($"job {job} ended in error", [.. log]);
        }

        context.Message($"job {job} done");
    }

    /// <summary>Reads the status of each job task and writes how many succeeded.</summary>
    public static Task Report(IReadOnlyList<Node> jobTasks, TaskContext context)
    {
        var succeeded = jobTasks.Count(jobTask => context.StatusOf(jobTask) is NodeStatus.Succeeded);
        context.Message($"{succeeded} of {jobTasks.Count} job tasks succeeded");
        return Task.CompletedTask;
    }
}
