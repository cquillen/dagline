using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace Dagline.Tests;

public class JournalListingTests
{
    private static readonly DateTimeOffset _created = new(2026, 11, 11, 4, 12, 0, TimeSpan.Zero);

    [Test]
    public async Task A_journal_whose_run_ended_complete_is_listed_with_each_field()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new FakeTimeProvider(_created);
        var builder = new GraphBuilder();
        builder.Task("load", _ =>
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            return Task.CompletedTask;
        });
        builder.Task("report", _ => Task.CompletedTask);

        var run = await Runner.RunAsync(
            builder.Build(),
            new RunOptions { Journal = Journal.New(temporary.Path, "all shards"), TimeProvider = clock });
        var listed = await Journals.ListAsync(temporary.Path);

        var summary = listed.ShouldHaveSingleItem();
        summary.Id.ShouldBe(run.Snapshot.Journal!.Id);
        summary.Created.ShouldBe(_created);
        summary.Description.ShouldBe("all shards");
        summary.Runs.ShouldBe(1);
        summary.LatestRun.ShouldBe(new JournalRunSummary(_created, _created.AddSeconds(5), Environment.UserName, Environment.MachineName));
        summary.Ending.ShouldBe(JournalEnding.Complete);
        summary.Finished.ShouldBeTrue();
        summary.Counts[NodeStatus.Succeeded].ShouldBe(2);
        summary.Counts.Values.Sum().ShouldBe(2);
    }

    [Test]
    public async Task A_journal_whose_run_ended_incomplete_counts_each_settle()
    {
        using var temporary = new TemporaryDirectory();
        var builder = new GraphBuilder();
        var load = builder.Task("load", _ => throw new InvalidOperationException("load failed"));
        builder.Task("publish", _ => Task.CompletedTask).Requires(load);
        builder.Task("report", _ => Task.CompletedTask);

        await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.New(temporary.Path) });
        var summary = (await Journals.ListAsync(temporary.Path)).ShouldHaveSingleItem();

        summary.Ending.ShouldBe(JournalEnding.Incomplete);
        summary.Finished.ShouldBeFalse();
        summary.Description.ShouldBeNull();
        summary.Counts[NodeStatus.Failed].ShouldBe(1);
        summary.Counts[NodeStatus.Blocked].ShouldBe(1);
        summary.Counts[NodeStatus.Succeeded].ShouldBe(1);
    }

    [Test]
    public async Task A_journal_whose_run_was_stopped_counts_only_the_paths_it_has_entries_for()
    {
        using var temporary = new TemporaryDirectory();
        var stop = new StopSignal();
        stop.Request();
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask);

        await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.New(temporary.Path) }, stop);
        var summary = (await Journals.ListAsync(temporary.Path)).ShouldHaveSingleItem();

        summary.Ending.ShouldBe(JournalEnding.Stopped);
        summary.Finished.ShouldBeFalse();
        summary.Counts.Values.Sum().ShouldBe(0);
        summary.Counts.Keys.ShouldBe(Enum.GetValues<NodeStatus>(), ignoreOrder: true);
    }

    [Test]
    public async Task A_journal_a_run_holds_is_listed_in_use()
    {
        using var temporary = new TemporaryDirectory();
        var load = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("load", load.Body);
        var holding = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.New(temporary.Path) });
        await load.Started;

        var summary = (await Journals.ListAsync(temporary.Path)).ShouldHaveSingleItem();

        summary.Ending.ShouldBe(JournalEnding.InUse);
        summary.Finished.ShouldBeFalse();
        summary.LatestRun.ShouldNotBeNull().Ended.ShouldBeNull();
        summary.Counts[NodeStatus.Running].ShouldBe(1);
        load.Release();
        await holding;
    }

    [Test]
    public async Task A_journal_cut_before_its_run_ended_is_listed_died_with_a_task_started_and_never_settled_as_running()
    {
        using var temporary = new TemporaryDirectory();
        using var copies = new TemporaryDirectory();
        var load = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("load", load.Body);
        builder.Task("report", _ => Task.CompletedTask);
        var holding = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.New(temporary.Path) });
        await load.Started;
        copies.CopyIn(Directory.GetFiles(temporary.Path, "*.journal").Single());
        load.Release();
        await holding;

        var summary = (await Journals.ListAsync(copies.Path)).ShouldHaveSingleItem();

        summary.Ending.ShouldBe(JournalEnding.Died);
        summary.Finished.ShouldBeFalse();
        summary.Runs.ShouldBe(1);
        summary.LatestRun.ShouldNotBeNull().Ended.ShouldBeNull();
        summary.Counts[NodeStatus.Running].ShouldBe(1);
    }

    [Test]
    public async Task A_journal_whose_first_run_was_never_recorded_is_listed_died_with_no_latest_run()
    {
        using var temporary = new TemporaryDirectory();
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask);
        var run = await Runner.RunAsync(
            builder.Build(),
            new RunOptions { Journal = Journal.New(temporary.Path, "all shards"), TimeProvider = new FakeTimeProvider(_created) });
        var file = temporary.Combine($"{run.Snapshot.Journal!.Id}.journal");
        await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file)).Split('\n')[0] + "\n");

        var summary = (await Journals.ListAsync(temporary.Path)).ShouldHaveSingleItem();

        summary.Ending.ShouldBe(JournalEnding.Died);
        summary.LatestRun.ShouldBeNull();
        summary.Runs.ShouldBe(0);
        summary.Created.ShouldBe(_created);
        summary.Description.ShouldBe("all shards");
        summary.Finished.ShouldBeFalse();
        summary.Counts.Values.Sum().ShouldBe(0);
    }

    [Test]
    public async Task A_journal_with_several_runs_is_listed_by_its_latest_run_and_the_last_status_of_each_path()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new FakeTimeProvider(_created);
        var loads = 0;
        var builder = new GraphBuilder();
        builder.Task("load", _ => ++loads == 1 ? throw new InvalidOperationException("load failed") : Task.CompletedTask);
        builder.Task("report", _ => Task.CompletedTask);
        var graph = builder.Build();

        var first = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path), TimeProvider = clock });
        var afterFirst = (await Journals.ListAsync(temporary.Path)).ShouldHaveSingleItem();
        clock.Advance(TimeSpan.FromHours(1));
        await Runner.RunAsync(
            graph,
            new RunOptions { Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id), TimeProvider = clock });
        var afterSecond = (await Journals.ListAsync(temporary.Path)).ShouldHaveSingleItem();

        afterFirst.Finished.ShouldBeFalse();
        afterSecond.Runs.ShouldBe(2);
        afterSecond.Created.ShouldBe(_created);
        afterSecond.LatestRun.ShouldNotBeNull().Started.ShouldBe(_created.AddHours(1));
        afterSecond.Ending.ShouldBe(JournalEnding.Complete);
        afterSecond.Finished.ShouldBeTrue();
        afterSecond.Counts[NodeStatus.Succeeded].ShouldBe(2);
        afterSecond.Counts[NodeStatus.Failed].ShouldBe(0);
    }

    [Test]
    public async Task A_journal_is_finished_only_while_its_latest_run_was_complete()
    {
        using var temporary = new TemporaryDirectory();
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask);
        var first = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.New(temporary.Path) });
        builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask);
        builder.Task("added", _ => throw new InvalidOperationException("added failed"));

        await Runner.RunAsync(
            builder.Build(),
            new RunOptions { Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id) });
        var summary = (await Journals.ListAsync(temporary.Path)).ShouldHaveSingleItem();

        summary.Ending.ShouldBe(JournalEnding.Incomplete);
        summary.Finished.ShouldBeFalse();
    }

    [Test]
    public async Task Journals_are_listed_oldest_first_and_other_files_are_left_out()
    {
        using var temporary = new TemporaryDirectory();
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask);
        var graph = builder.Build();
        var later = await Runner.RunAsync(
            graph,
            new RunOptions { Journal = Journal.New(temporary.Path), TimeProvider = new FakeTimeProvider(_created.AddDays(1)) });
        var earlier = await Runner.RunAsync(
            graph,
            new RunOptions { Journal = Journal.New(temporary.Path), TimeProvider = new FakeTimeProvider(_created) });
        await File.WriteAllTextAsync(temporary.Combine("notes.txt"), "not a journal");
        await File.WriteAllTextAsync(temporary.Combine("notes.journal"), "not a journal either");

        var listed = await Journals.ListAsync(temporary.Path);

        listed.Select(summary => summary.Id).ShouldBe([earlier.Snapshot.Journal!.Id, later.Snapshot.Journal!.Id]);
    }

    [Test]
    public async Task A_directory_that_does_not_exist_lists_no_journals()
    {
        using var temporary = new TemporaryDirectory();

        var listed = await Journals.ListAsync(temporary.Combine("missing"));

        listed.ShouldBeEmpty();
        Directory.Exists(temporary.Combine("missing")).ShouldBeFalse();
    }

    [Test]
    public async Task A_journal_in_an_unknown_format_makes_the_listing_throw_naming_it()
    {
        using var temporary = new TemporaryDirectory();
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask);
        var run = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.New(temporary.Path) });
        var id = run.Snapshot.Journal!.Id;
        var file = temporary.Combine($"{id}.journal");
        await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file)).Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal));

        var thrown = await Should.ThrowAsync<JournalFormatException>(() => Journals.ListAsync(temporary.Path));

        thrown.Version.ShouldBe(2);
        thrown.Message.ShouldContain(id);
    }
}
