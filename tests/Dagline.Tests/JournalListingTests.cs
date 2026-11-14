using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace Dagline.Tests;

public class JournalListingTests
{
    private const int _repeats = 50;
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

        listed.Unreadable.ShouldBeEmpty();
        var summary = listed.Journals.ShouldHaveSingleItem();
        summary.Id.ShouldBe(run.Snapshot.Journal!.Id);
        summary.Created.ShouldBe(_created);
        summary.Description.ShouldBe("all shards");
        summary.Runs.ShouldBe(1);
        var latestRun = summary.LatestRun.ShouldNotBeNull();
        (latestRun.Started, latestRun.Ended, latestRun.User, latestRun.Machine)
            .ShouldBe((_created, _created.AddSeconds(5), Environment.UserName, Environment.MachineName));
        summary.Ending.ShouldBe(JournalEnding.Complete);
        summary.IsFinished.ShouldBeTrue();
        summary.Counts[NodeStatus.Succeeded].ShouldBe(2);
        summary.Counts.Total.ShouldBe(2);
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
        var summary = (await Journals.ListAsync(temporary.Path)).Journals.ShouldHaveSingleItem();

        summary.Ending.ShouldBe(JournalEnding.Incomplete);
        summary.IsFinished.ShouldBeFalse();
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
        var summary = (await Journals.ListAsync(temporary.Path)).Journals.ShouldHaveSingleItem();

        summary.Ending.ShouldBe(JournalEnding.Stopped);
        summary.IsFinished.ShouldBeFalse();
        summary.Counts.Total.ShouldBe(0);
        Enum.GetValues<NodeStatus>().ShouldAllBe(status => summary.Counts[status] == 0);
    }

    [Test]
    [NotInParallel(DiskHeavy.Key)]
    public async Task A_stopped_run_records_a_task_that_succeeds_in_the_grace_period_and_one_it_lets_go_of_as_cancelled()
    {
        for (var repeat = 0; repeat < _repeats; repeat++)
        {
            using var temporary = new TemporaryDirectory();
            var stop = new StopSignal();
            var finish = new StoppedBody(onStop: _ => { });
            var hang = new PausedBody();
            var watched = new WatchedReporter(new RecordingReporter());
            var builder = new GraphBuilder();
            var finishing = builder.Task("finish", finish.Body);
            builder.Task("hang", hang.Body);
            var graph = builder.Build();

            var running = Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path), Reporters = [watched] }, stop);
            await finish.Started;
            await hang.Started;
            stop.Request();
            await watched.Handled("finish: running -> succeeded");
            stop.Request();
            var first = await running;
            hang.Release();
            var summary = (await Journals.ListAsync(temporary.Path)).Journals.ShouldHaveSingleItem();
            var next = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id) });

            summary.Ending.ShouldBe(JournalEnding.Stopped);
            summary.Counts[NodeStatus.Succeeded].ShouldBe(1);
            summary.Counts[NodeStatus.Cancelled].ShouldBe(1);
            next.Snapshot[finishing.Path].IsEarlierSuccess.ShouldBeTrue();
        }
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

        var summary = (await Journals.ListAsync(temporary.Path)).Journals.ShouldHaveSingleItem();

        summary.Ending.ShouldBe(JournalEnding.InUse);
        summary.IsFinished.ShouldBeFalse();
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
        var report = builder.Task("report", _ => Task.CompletedTask);
        builder.Task("load", load.Body).After(report);
        var holding = Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.New(temporary.Path) });
        await load.Started;
        copies.CopyIn(Directory.GetFiles(temporary.Path, "*.journal").Single());
        load.Release();
        await holding;

        var summary = (await Journals.ListAsync(copies.Path)).Journals.ShouldHaveSingleItem();

        summary.Ending.ShouldBe(JournalEnding.Died);
        summary.IsFinished.ShouldBeFalse();
        summary.Runs.ShouldBe(1);
        summary.LatestRun.ShouldNotBeNull().Ended.ShouldBeNull();
        summary.Counts[NodeStatus.Running].ShouldBe(1);
        summary.Counts[NodeStatus.Succeeded].ShouldBe(1);
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

        var summary = (await Journals.ListAsync(temporary.Path)).Journals.ShouldHaveSingleItem();

        summary.Ending.ShouldBe(JournalEnding.Died);
        summary.LatestRun.ShouldBeNull();
        summary.Runs.ShouldBe(0);
        summary.Created.ShouldBe(_created);
        summary.Description.ShouldBe("all shards");
        summary.IsFinished.ShouldBeFalse();
        summary.Counts.Total.ShouldBe(0);
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
        var afterFirst = (await Journals.ListAsync(temporary.Path)).Journals.ShouldHaveSingleItem();
        clock.Advance(TimeSpan.FromHours(1));
        await Runner.RunAsync(
            graph,
            new RunOptions { Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id), TimeProvider = clock });
        var afterSecond = (await Journals.ListAsync(temporary.Path)).Journals.ShouldHaveSingleItem();

        afterFirst.IsFinished.ShouldBeFalse();
        afterSecond.Runs.ShouldBe(2);
        afterSecond.Created.ShouldBe(_created);
        afterSecond.LatestRun.ShouldNotBeNull().Started.ShouldBe(_created.AddHours(1));
        afterSecond.Ending.ShouldBe(JournalEnding.Complete);
        afterSecond.IsFinished.ShouldBeTrue();
        afterSecond.Counts[NodeStatus.Succeeded].ShouldBe(2);
        afterSecond.Counts[NodeStatus.Failed].ShouldBe(0);
    }

    [Test]
    public async Task A_listing_counts_no_earlier_successes_and_no_waiting_tasks()
    {
        using var temporary = new TemporaryDirectory();
        var load = new PausedBody();
        var firstRun = true;
        var builder = new GraphBuilder();
        builder.Task("report", _ => Task.CompletedTask);
        builder.Task("load", async context =>
        {
            if (firstRun)
            {
                firstRun = false;
                throw new InvalidOperationException("load failed");
            }

            using var waiting = context.BeginWaiting("for the export job");
            await load.Body(context);
        });
        var graph = builder.Build();
        var first = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path) });
        var continuing = Runner.RunAsync(
            graph,
            new RunOptions { Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id) });
        await load.Started;

        var summary = (await Journals.ListAsync(temporary.Path)).Journals.ShouldHaveSingleItem();
        load.Release();
        var second = await continuing;

        second.Snapshot.Counts.EarlierSuccesses.ShouldBe(1);
        summary.Counts[NodeStatus.Running].ShouldBe(1);
        summary.Counts[NodeStatus.Succeeded].ShouldBe(1);
        summary.Counts.EarlierSuccesses.ShouldBe(0);
        summary.Counts.Waiting.ShouldBe(0);
        summary.Counts.Working.ShouldBe(1);
    }

    [Test]
    public async Task A_path_set_aside_since_its_success_counts_as_pending()
    {
        using var temporary = new TemporaryDirectory();
        var builder = new GraphBuilder();
        var check = builder.Task("check", context => throw context.Fail("bad data"));
        builder.Task("summary", _ => Task.CompletedTask).After(check);
        var graph = builder.Build();
        var first = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path) });
        var stop = new StopSignal();
        stop.Request();

        await Runner.RunAsync(graph, new RunOptions { Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id) }, stop);
        var summary = (await Journals.ListAsync(temporary.Path)).Journals.ShouldHaveSingleItem();

        summary.Ending.ShouldBe(JournalEnding.Stopped);
        summary.Counts[NodeStatus.Failed].ShouldBe(1);
        summary.Counts[NodeStatus.Pending].ShouldBe(1);
        summary.Counts[NodeStatus.Succeeded].ShouldBe(0);
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
        var summary = (await Journals.ListAsync(temporary.Path)).Journals.ShouldHaveSingleItem();

        summary.Ending.ShouldBe(JournalEnding.Incomplete);
        summary.IsFinished.ShouldBeFalse();
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

        listed.Unreadable.ShouldBeEmpty();
        listed.Journals.Select(summary => summary.Id).ShouldBe([earlier.Snapshot.Journal!.Id, later.Snapshot.Journal!.Id]);
    }

    [Test]
    public async Task A_directory_that_does_not_exist_lists_no_journals()
    {
        using var temporary = new TemporaryDirectory();

        var listed = await Journals.ListAsync(temporary.Combine("missing"));

        listed.Journals.ShouldBeEmpty();
        listed.Unreadable.ShouldBeEmpty();
        Directory.Exists(temporary.Combine("missing")).ShouldBeFalse();
    }

    [Test]
    public async Task A_listing_cancelled_before_it_begins_is_cancelled_even_with_no_journals_to_read()
    {
        using var temporary = new TemporaryDirectory();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => Journals.ListAsync(temporary.Path, cancelled.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => Journals.ListAsync(temporary.Combine("missing"), cancelled.Token));
    }

    [Test]
    public async Task A_journal_in_an_unknown_format_is_listed_unreadable_beside_a_healthy_one()
    {
        using var temporary = new TemporaryDirectory();
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask);
        var graph = builder.Build();
        var healthy = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path) });
        var later = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path) });
        var file = temporary.Combine($"{later.Snapshot.Journal!.Id}.journal");
        await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file)).Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal));

        var listed = await Journals.ListAsync(temporary.Path);

        listed.Journals.ShouldHaveSingleItem().Id.ShouldBe(healthy.Snapshot.Journal!.Id);
        var unreadable = listed.Unreadable.ShouldHaveSingleItem();
        unreadable.FilePath.ShouldBe(file);
        var thrown = unreadable.Exception.ShouldBeOfType<JournalFormatException>();
        thrown.Version.ShouldBe(2);
        thrown.Message.ShouldContain(file);
    }

    [Test]
    public async Task Files_that_are_not_journals_or_were_cut_short_or_overwritten_are_listed_unreadable_in_order_of_their_path()
    {
        using var temporary = new TemporaryDirectory();
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask);
        var healthy = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.New(temporary.Path) });
        var healthyFile = temporary.Combine($"{healthy.Snapshot.Journal!.Id}.journal");
        var cutShort = temporary.Combine("20261111T041200Z-000001.journal");
        var overwritten = temporary.Combine("20261111T041200Z-000002.journal");
        var notAJournal = temporary.Combine("20261111T041200Z-000003.journal");
        await File.WriteAllTextAsync(cutShort, (await File.ReadAllTextAsync(healthyFile))[..10]);
        await File.WriteAllBytesAsync(overwritten, [0, 0, 0, 0, (byte)'\n', 0, 0, (byte)'\n']);
        await File.WriteAllTextAsync(notAJournal, "a shopping list\n");

        var listed = await Journals.ListAsync(temporary.Path);

        listed.Journals.ShouldHaveSingleItem().Id.ShouldBe(healthy.Snapshot.Journal!.Id);
        listed.Unreadable.Select(unreadable => unreadable.FilePath).ShouldBe([cutShort, overwritten, notAJournal]);
        listed.Unreadable.ShouldAllBe(unreadable => unreadable.Exception is JournalFormatException);
    }

    [Test]
    public async Task A_journal_whose_lock_cannot_be_opened_is_listed_unreadable_carrying_its_id()
    {
        using var temporary = new TemporaryDirectory();
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask);
        var run = await Runner.RunAsync(builder.Build(), new RunOptions { Journal = Journal.New(temporary.Path) });
        var id = run.Snapshot.Journal!.Id;
        File.Delete(temporary.Combine($"{id}.lock"));
        Directory.CreateDirectory(temporary.Combine($"{id}.lock"));

        var listed = await Journals.ListAsync(temporary.Path);

        listed.Journals.ShouldBeEmpty();
        var unreadable = listed.Unreadable.ShouldHaveSingleItem();
        unreadable.FilePath.ShouldBe(temporary.Combine($"{id}.journal"));
        var thrown = unreadable.Exception.ShouldBeOfType<JournalAccessException>();
        (thrown.Id, thrown.Directory).ShouldBe((id, temporary.Path));
    }

    [Test]
    public async Task A_directory_that_cannot_be_read_makes_the_listing_throw_naming_it()
    {
        using var temporary = new TemporaryDirectory();
        var notADirectory = temporary.Combine("file");
        await File.WriteAllTextAsync(notADirectory, "not a directory");

        var thrown = await Should.ThrowAsync<JournalAccessException>(() => Journals.ListAsync(notADirectory));

        thrown.Directory.ShouldBe(notADirectory);
        thrown.Id.ShouldBeNull();
        thrown.Message.ShouldStartWith($"The journal directory '{notADirectory}' cannot be listed: ");
    }
}
