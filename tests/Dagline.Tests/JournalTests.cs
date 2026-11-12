using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace Dagline.Tests;

public class JournalTests
{
    private static readonly DateTimeOffset _created = new(2026, 11, 11, 4, 12, 0, TimeSpan.Zero);

    [Test]
    public async Task A_run_on_a_new_journal_creates_its_directory_and_one_file_named_by_its_id()
    {
        using var temporary = new TemporaryDirectory();
        var directory = temporary.Combine("journals");
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask);

        var journal = Journal.New(directory, "all shards");
        Directory.Exists(directory).ShouldBeFalse();
        var result = await Runner.RunAsync(
            builder.Build(),
            new RunOptions { Journal = journal, TimeProvider = new FakeTimeProvider(_created) });

        var info = result.Snapshot.Journal.ShouldNotBeNull();
        info.Id.ShouldMatch("^20261111T041200Z-[0-9a-f]{6}$");
        info.Description.ShouldBe("all shards");
        info.Run.ShouldBe(1);
        Directory.GetFiles(directory).Select(Path.GetFileName).ShouldBe([$"{info.Id}.journal"]);
    }
}
