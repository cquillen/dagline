using Dagline.TestSupport;

namespace Dagline.Tests;

/// <summary>
/// The anchor of the journal format promise: a later Dagline reads every journal format an earlier release wrote. The journal in
/// <c>CompatibilityJournals</c> is in format version 1, exactly as the first alpha wrote it for the graph below, save that its user
/// and machine are generic. It is never regenerated: a change that breaks these tests breaks journals that operators hold.
/// </summary>
public class JournalCompatibilityTests
{
    private const string _id = "20261111T041200Z-fa4fa4";
    private static readonly DateTimeOffset _created = new(2026, 11, 11, 4, 12, 0, TimeSpan.Zero);

    [Test]
    public async Task A_version_1_journal_is_listed_with_each_field_it_recorded()
    {
        using var temporary = CopyOfVersion1Journal();

        var summary = (await Journals.ListAsync(temporary.Path)).Journals.ShouldHaveSingleItem();

        summary.Id.ShouldBe(_id);
        summary.Created.ShouldBe(_created);
        summary.Description.ShouldBe("all shards");
        summary.Runs.ShouldBe(1);
        var latestRun = summary.LatestRun.ShouldNotBeNull();
        (latestRun.Started, latestRun.Ended, latestRun.User, latestRun.Machine)
            .ShouldBe((_created, _created.AddSeconds(5), "operator", "build-host"));
        summary.Ending.ShouldBe(JournalEnding.Incomplete);
        summary.IsFinished.ShouldBeFalse();
        summary.Counts[NodeStatus.Succeeded].ShouldBe(1);
        summary.Counts[NodeStatus.Failed].ShouldBe(1);
        summary.Counts[NodeStatus.Blocked].ShouldBe(1);
        summary.Counts.Total.ShouldBe(3);
    }

    [Test]
    public async Task A_version_1_journal_is_continued_with_its_earlier_success_and_its_checkpoint()
    {
        using var temporary = CopyOfVersion1Journal();
        var loads = 0;
        string? exportCheckpoint = null;
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-1");
        var load = shard.Task("load", _ =>
        {
            loads++;
            return Task.CompletedTask;
        });
        var export = shard.Task("export", context =>
        {
            exportCheckpoint = context.Checkpoint;
            return Task.CompletedTask;
        }).Requires(load);
        var report = builder.Task("report", _ => Task.CompletedTask).Requires(export);

        var run = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.Continue(temporary.Path, _id) });

        run.Outcome.ShouldBe(RunOutcome.Complete);
        (run.Snapshot.Journal!.Id, run.Snapshot.Journal.Description, run.Snapshot.Journal.Run).ShouldBe((_id, "all shards", 2));
        loads.ShouldBe(0);
        var loaded = run.Snapshot[load.Path];
        loaded.IsEarlierSuccess.ShouldBeTrue();
        loaded.Message.ShouldBe("loaded 3 files");
        loaded.Detail.ShouldBe(["file-1", "file-2", "file-3"]);
        loaded.Warnings.ShouldBe(["file-2 was empty"]);
        exportCheckpoint.ShouldBe("job-17");
        run.Snapshot[export.Path].Status.ShouldBe(NodeStatus.Succeeded);
        run.Snapshot[report.Path].Status.ShouldBe(NodeStatus.Succeeded);
    }

    /// <summary>A directory of its own holding a copy of the checked-in journal, so that continuing it never changes the original.</summary>
    private static TemporaryDirectory CopyOfVersion1Journal()
    {
        var temporary = new TemporaryDirectory();
        temporary.CopyIn(Path.Combine(AppContext.BaseDirectory, "CompatibilityJournals", $"{_id}.journal"));
        return temporary;
    }
}
