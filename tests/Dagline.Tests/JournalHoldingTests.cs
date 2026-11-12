using Dagline.TestSupport;

namespace Dagline.Tests;

public class JournalHoldingTests
{
    [Test]
    public async Task A_second_run_on_a_journal_a_paused_run_holds_throws_before_any_run_exists()
    {
        using var temporary = new TemporaryDirectory();
        var load = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("load", load.Body);
        var graph = builder.Build();
        var holder = new RecordingReporter();
        var holding = Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path), Reporters = [holder] });
        await load.Started;
        var id = (await holder.Source).Current.Journal!.Id;
        var reporter = new RecordingReporter();

        await Should.ThrowAsync<JournalInUseException>(() => Runner.RunAsync(
            graph,
            new RunOptions { Journal = Journal.Continue(temporary.Path, id), Reporters = [reporter] }));

        reporter.Events.ShouldBeEmpty();
        load.Release();
        await holding;
    }

    [Test]
    public async Task The_options_are_checked_before_the_lock()
    {
        using var temporary = new TemporaryDirectory();
        var load = new PausedBody();
        var builder = new GraphBuilder();
        builder.Task("load", load.Body);
        var graph = builder.Build();
        var holder = new RecordingReporter();
        var holding = Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path), Reporters = [holder] });
        await load.Started;
        var id = (await holder.Source).Current.Journal!.Id;

        await Should.ThrowAsync<RunConfigurationException>(() => Runner.RunAsync(
            graph,
            new RunOptions { Journal = Journal.Continue(temporary.Path, id), Cap = 0 }));

        load.Release();
        await holding;
    }

    [Test]
    [Arguments(RunOutcome.Complete)]
    [Arguments(RunOutcome.Incomplete)]
    [Arguments(RunOutcome.Stopped)]
    public async Task A_run_lets_go_of_its_journal_when_it_ends_however_it_ends(RunOutcome outcome)
    {
        using var temporary = new TemporaryDirectory();
        var stop = new StopSignal();
        var builder = new GraphBuilder();
        builder.Task("load", _ => outcome switch
        {
            RunOutcome.Incomplete => throw new InvalidOperationException("load failed"),
            _ => Task.CompletedTask,
        });
        var graph = builder.Build();
        if (outcome is RunOutcome.Stopped)
        {
            stop.Request();
        }

        var first = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path) }, stop);
        first.Outcome.ShouldBe(outcome);
        var id = first.Snapshot.Journal!.Id;

        var second = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.Continue(temporary.Path, id) });
        second.Snapshot.Journal!.Run.ShouldBe(2);
        await Journals.DeleteAsync(temporary.Path, id);
    }

    [Test]
    public async Task A_lock_file_left_behind_without_a_holder_stops_nothing()
    {
        using var temporary = new TemporaryDirectory();
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask);
        var graph = builder.Build();
        var first = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path) });
        var id = first.Snapshot.Journal!.Id;
        File.Exists(temporary.Combine($"{id}.lock")).ShouldBeTrue();

        await Runner.RunAsync(graph, new RunOptions { Journal = Journal.Continue(temporary.Path, id) });
        var listed = await Journals.ListAsync(temporary.Path);
        await Journals.DeleteAsync(temporary.Path, id);

        listed.Single().Ending.ShouldBe(JournalEnding.Complete);
        Directory.GetFiles(temporary.Path).ShouldBeEmpty();
    }

    [Test]
    public async Task A_journal_that_cannot_be_created_throws_before_any_run_exists()
    {
        using var temporary = new TemporaryDirectory();
        var notADirectory = temporary.Combine("file");
        await File.WriteAllTextAsync(notADirectory, "not a directory");
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask);
        var reporter = new RecordingReporter();

        await Should.ThrowAsync<JournalOpenException>(() => Runner.RunAsync(
            builder.Build(),
            new RunOptions { Journal = Journal.New(notADirectory), Reporters = [reporter] }));

        reporter.Events.ShouldBeEmpty();
    }

    [Test]
    public async Task A_file_with_an_unknown_version_throws_carrying_the_version()
    {
        using var temporary = new TemporaryDirectory();
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask);
        var graph = builder.Build();
        var first = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path) });
        var id = first.Snapshot.Journal!.Id;
        var file = temporary.Combine($"{id}.journal");
        await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file)).Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal));
        var reporter = new RecordingReporter();

        var thrown = await Should.ThrowAsync<JournalFormatException>(() => Runner.RunAsync(
            graph,
            new RunOptions { Journal = Journal.Continue(temporary.Path, id), Reporters = [reporter] }));

        thrown.Version.ShouldBe(2);
        reporter.Events.ShouldBeEmpty();
    }

    [Test]
    public async Task A_file_that_is_not_a_journal_throws_with_no_version()
    {
        using var temporary = new TemporaryDirectory();
        const string id = "20261111T041200Z-a3f9c2";
        await File.WriteAllTextAsync(temporary.Combine($"{id}.journal"), "a shopping list\n");
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask);
        var reporter = new RecordingReporter();

        var thrown = await Should.ThrowAsync<JournalFormatException>(() => Runner.RunAsync(
            builder.Build(),
            new RunOptions { Journal = Journal.Continue(temporary.Path, id), Reporters = [reporter] }));

        thrown.Version.ShouldBeNull();
        reporter.Events.ShouldBeEmpty();
    }

    [Test]
    public async Task A_journal_with_an_unreadable_line_before_the_last_throws_carrying_the_version()
    {
        using var temporary = new TemporaryDirectory();
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask);
        var graph = builder.Build();
        var first = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path) });
        var id = first.Snapshot.Journal!.Id;
        var file = temporary.Combine($"{id}.journal");
        var lines = (await File.ReadAllTextAsync(file)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[1] = "{ not an entry";
        await File.WriteAllTextAsync(file, string.Join('\n', lines) + "\n");

        var thrown = await Should.ThrowAsync<JournalFormatException>(() => Runner.RunAsync(
            graph,
            new RunOptions { Journal = Journal.Continue(temporary.Path, id) }));

        thrown.Version.ShouldBe(1);
    }
}
