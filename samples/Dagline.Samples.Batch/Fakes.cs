using System.Text.Json;

namespace Dagline.Samples.Batch;

/// <summary>One entity of a tenant, as the fake store holds it.</summary>
public sealed record Entity(int Number, bool IsBad);

/// <summary>
/// A fake entity store, held in memory. Everything about a tenant is seeded by its name, so the same tenant always has
/// the same entities, the same warning and the same time to validate. Every delay is on the clock the caller passes.
/// </summary>
public sealed class EntityStore(double timeScale)
{
    /// <summary>Tenants whose entities include bad ones. None unless the app says so.</summary>
    public IReadOnlySet<string> TenantsWithBadEntities { get; init; } = new HashSet<string>();

    /// <summary>Fetches a tenant's entities.</summary>
    public async Task<IReadOnlyList<Entity>> FetchAsync(string tenant, TimeProvider clock, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(0.2 * timeScale), clock, cancellationToken);

        var count = Seeded(tenant).EntityCount;
        var hasBad = TenantsWithBadEntities.Contains(tenant);
        return [.. Enumerable.Range(1, count).Select(number => new Entity(number, hasBad && number % 997 == 0))];
    }

    /// <summary>Checks a batch of a tenant's entities. The whole list takes the tenant's time to validate.</summary>
    public async Task CheckAsync(string tenant, IReadOnlyCollection<Entity> batch, TimeProvider clock, CancellationToken cancellationToken)
    {
        var seeded = Seeded(tenant);
        var time = seeded.ValidationTime * timeScale * batch.Count / seeded.EntityCount;
        await Task.Delay(time, clock, cancellationToken);
    }

    /// <summary>The warning a tenant always gets, or <see langword="null"/> for most tenants.</summary>
    public static string? WarningFor(string tenant) =>
        Seeded(tenant).Warns ? "some entities have no owner; they were kept as they are" : null;

    private static (int EntityCount, TimeSpan ValidationTime, bool Warns) Seeded(string tenant)
    {
        var random = new Random(Seed.Of(tenant));
        var entityCount = random.Next(1_000, 5_001);
        var validationTime = TimeSpan.FromMilliseconds(random.Next(500, 3_001));
        var warns = random.Next(25) == 0;
        return (entityCount, validationTime, warns);
    }
}

/// <summary>The state of a job in the fake scheduler.</summary>
public enum JobStatus
{
    /// <summary>Triggered and not yet finished.</summary>
    Running,

    /// <summary>Finished well.</summary>
    Done,

    /// <summary>Finished in error.</summary>
    EndedInError,
}

/// <summary>
/// A fake job scheduler. It keeps one small file per job in its directory, so two processes running different shards
/// never write the same file. A job finishes a set time after its trigger on the scheduler's clock, so it keeps running
/// while the app is down.
/// </summary>
public sealed class FakeScheduler(string directory, TimeProvider clock, double timeScale = 1)
{
    private int _triggers;

    /// <summary>Names of jobs that end in error. None unless the app says so.</summary>
    public IReadOnlySet<string> JobsThatEndInError { get; init; } = new HashSet<string>();

    /// <summary>How many triggers this scheduler has accepted.</summary>
    public int Triggers => Volatile.Read(ref _triggers);

    /// <summary>
    /// Triggers the job of this name and returns its id. Throws when the job is running or done; a job that ended in
    /// error may be triggered again.
    /// </summary>
    public async Task<string> TriggerAsync(string name, CancellationToken cancellationToken)
    {
        var existing = await ReadAsync(name, cancellationToken);
        if (existing is not null && StatusOf(existing) is not JobStatus.EndedInError)
        {
            throw new InvalidOperationException($"job {name} was already triggered as {existing.Id} and has not ended in error");
        }

        var now = clock.GetUtcNow();
        var job = new JobFile(
            $"{name}-{Random.Shared.Next(0x1000000):x6}",
            now,
            now + (Seeded(name) * timeScale),
            JobsThatEndInError.Contains(name));

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(PathOf(name), JsonSerializer.Serialize(job), cancellationToken);
        Interlocked.Increment(ref _triggers);
        return job.Id;
    }

    /// <summary>The status of a job, by the id its trigger returned.</summary>
    public async Task<JobStatus> GetStatusAsync(string id, CancellationToken cancellationToken) =>
        StatusOf(await FindAsync(id, cancellationToken));

    /// <summary>A few lines of a job's log, by the id its trigger returned.</summary>
    public async Task<IReadOnlyList<string>> GetLogAsync(string id, CancellationToken cancellationToken)
    {
        var job = await FindAsync(id, cancellationToken);
        return StatusOf(job) switch
        {
            JobStatus.Running => [$"{job.Triggered:u} triggered", "processing"],
            JobStatus.Done => [$"{job.Triggered:u} triggered", "processing", $"{job.Finishes:u} done"],
            _ => [$"{job.Triggered:u} triggered", "processing", "step 3 of 5: input file not found", $"{job.Finishes:u} ended in error"],
        };
    }

    /// <summary>Forgets the jobs of these names, so each can be triggered afresh. Touches no other job.</summary>
    public void Forget(IEnumerable<string> names)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var name in names)
        {
            File.Delete(PathOf(name));
        }
    }

    private JobStatus StatusOf(JobFile job) =>
        clock.GetUtcNow() < job.Finishes ? JobStatus.Running
        : job.EndsInError ? JobStatus.EndedInError
        : JobStatus.Done;

    private async Task<JobFile> FindAsync(string id, CancellationToken cancellationToken)
    {
        var name = id[..id.LastIndexOf('-')];
        var job = await ReadAsync(name, cancellationToken);
        return job is not null && job.Id == id ? job : throw new InvalidOperationException($"no job {id}");
    }

    private async Task<JobFile?> ReadAsync(string name, CancellationToken cancellationToken)
    {
        var path = PathOf(name);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<JobFile>(await File.ReadAllTextAsync(path, cancellationToken))
            : null;
    }

    private string PathOf(string name) => Path.Combine(directory, $"{name}.json");

    private static TimeSpan Seeded(string name) => TimeSpan.FromSeconds(new Random(Seed.Of(name)).Next(20, 41));

    private sealed record JobFile(string Id, DateTimeOffset Triggered, DateTimeOffset Finishes, bool EndsInError);
}

/// <summary>A seed for the fakes' random numbers, the same for a name in every process.</summary>
internal static class Seed
{
    // string.GetHashCode differs between processes, so the seed is a hash of our own.
    public static int Of(string name)
    {
        var seed = 0;
        foreach (var c in name)
        {
            seed = unchecked((seed * 31) + c);
        }

        return seed;
    }
}
