using Dagline.TestSupport;

namespace Dagline.Tests;

public class JournalDeletingTests
{
    [Test]
    public async Task Deleting_a_journal_removes_it_and_its_lock_file()
    {
        using var temporary = new TemporaryDirectory();
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask);
        var graph = builder.Build();
        var kept = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path) });
        var deleted = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path) });

        await Journals.DeleteAsync(temporary.Path, deleted.Snapshot.Journal!.Id);

        var id = kept.Snapshot.Journal!.Id;
        Directory.GetFiles(temporary.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal)
            .ShouldBe([$"{id}.journal", $"{id}.lock"]);
        (await Journals.ListAsync(temporary.Path)).Journals.ShouldHaveSingleItem().Id.ShouldBe(id);
    }

    [Test]
    public async Task Deleting_a_journal_a_run_holds_is_refused()
    {
        using var temporary = new TemporaryDirectory();
        var load = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("load", load.Body);
        var holder = new RecordingReporter();
        var holding = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.New(temporary.Path), Reporters = [holder] });
        await load.Started;
        var id = (await holder.Source).Current.Journal!.Id;

        var thrown = await Should.ThrowAsync<JournalInUseException>(() => Journals.DeleteAsync(temporary.Path, id));

        (thrown.Id, thrown.Directory).ShouldBe((id, temporary.Path));
        load.Release();
        (await holding).Outcome.ShouldBe(RunOutcome.Complete);
        (await Journals.ListAsync(temporary.Path)).Journals.ShouldHaveSingleItem().Ending.ShouldBe(JournalEnding.Complete);
    }

    [Test]
    public async Task Deleting_a_journal_that_is_not_there_throws()
    {
        using var temporary = new TemporaryDirectory();

        var thrown = await Should.ThrowAsync<JournalNotFoundException>(() => Journals.DeleteAsync(temporary.Path, "20261111T041200Z-a3f9c2"));
        (thrown.Id, thrown.Directory).ShouldBe(("20261111T041200Z-a3f9c2", temporary.Path));
        await Should.ThrowAsync<JournalNotFoundException>(
            () => Journals.DeleteAsync(temporary.Combine("missing"), "20261111T041200Z-a3f9c2"));

        Directory.GetFiles(temporary.Path).ShouldBeEmpty();
    }

    [Test]
    public async Task A_failed_deletion_is_reported_through_the_returned_task()
    {
        using var temporary = new TemporaryDirectory();

        var deleting = Journals.DeleteAsync(temporary.Path, "20261111T041200Z-a3f9c2");

        await Should.ThrowAsync<JournalNotFoundException>(deleting);
    }

    [Test]
    public async Task A_deletion_cancelled_before_it_begins_is_reported_through_the_returned_task()
    {
        using var temporary = new TemporaryDirectory();

        var deleting = Journals.DeleteAsync(temporary.Path, "20261111T041200Z-a3f9c2", new CancellationToken(canceled: true));

        await Should.ThrowAsync<OperationCanceledException>(deleting);
        deleting.IsCanceled.ShouldBeTrue();
    }

    [Test]
    public void An_id_not_in_the_id_format_is_refused_by_the_call_itself()
    {
        Should.Throw<ArgumentException>(() => { _ = Journals.DeleteAsync("journals", "../20261111T041200Z-a3f9c2"); })
            .ParamName.ShouldBe("id");
    }

    [Test]
    [Arguments("../20261111T041200Z-a3f9c2")]
    [Arguments("20261111T041200Z-A3F9C2")]
    [Arguments("")]
    public async Task Deleting_an_id_not_in_the_id_format_is_an_argument_error(string id)
    {
        (await Should.ThrowAsync<ArgumentException>(() => Journals.DeleteAsync("journals", id))).ParamName.ShouldBe("id");
    }

    [Test]
    public async Task Listing_and_deleting_need_a_directory()
    {
        await Should.ThrowAsync<ArgumentNullException>(() => Journals.ListAsync(null!));
        await Should.ThrowAsync<ArgumentException>(() => Journals.ListAsync(" "));
        await Should.ThrowAsync<ArgumentException>(() => Journals.DeleteAsync("", "20261111T041200Z-a3f9c2"));
        await Should.ThrowAsync<ArgumentNullException>(() => Journals.DeleteAsync("journals", null!));
    }
}
