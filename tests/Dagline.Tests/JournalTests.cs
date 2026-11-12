using Dagline.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace Dagline.Tests;

public class JournalTests
{
    private static readonly DateTimeOffset _created = new(2026, 11, 11, 4, 12, 0, TimeSpan.Zero);

    [Test]
    public async Task A_run_on_a_new_journal_creates_its_directory_and_a_file_and_lock_file_named_by_its_id()
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
        Directory.GetFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal)
            .ShouldBe([$"{info.Id}.journal", $"{info.Id}.lock"]);
    }

    [Test]
    public async Task Two_new_journals_created_at_the_same_instant_get_different_ids()
    {
        using var temporary = new TemporaryDirectory();
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask);
        var graph = builder.Build();

        var first = await Runner.RunAsync(
            graph,
            new RunOptions { Journal = Journal.New(temporary.Path), TimeProvider = new FakeTimeProvider(_created) });
        var second = await Runner.RunAsync(
            graph,
            new RunOptions { Journal = Journal.New(temporary.Path), TimeProvider = new FakeTimeProvider(_created) });

        second.Snapshot.Journal!.Id.ShouldNotBe(first.Snapshot.Journal!.Id);
        Directory.GetFiles(temporary.Path, "*.journal").Length.ShouldBe(2);
    }

    [Test]
    public async Task Continuing_an_id_that_is_not_there_throws_and_publishes_no_event()
    {
        using var temporary = new TemporaryDirectory();
        var builder = new GraphBuilder();
        builder.Task("load", _ => Task.CompletedTask);
        var graph = builder.Build();
        var reporter = new RecordingReporter();

        await Should.ThrowAsync<JournalNotFoundException>(() => Runner.RunAsync(
            graph,
            new RunOptions { Journal = Journal.Continue(temporary.Path, "20261111T041200Z-a3f9c2"), Reporters = [reporter] }));
        await Should.ThrowAsync<JournalNotFoundException>(() => Runner.RunAsync(
            graph,
            new RunOptions { Journal = Journal.Continue(temporary.Combine("missing"), "20261111T041200Z-a3f9c2"), Reporters = [reporter] }));

        reporter.Events.ShouldBeEmpty();
    }

    [Test]
    [Arguments("20261111T041200Z-A3F9C2")]
    [Arguments("20261111T041200Z-a3f9c")]
    [Arguments("2026-11-11T041200Z-a3f9c2")]
    [Arguments("../20261111T041200Z-a3f9c2")]
    [Arguments("20261111T041200Z-a3f9c2\n")]
    [Arguments("")]
    public void An_id_not_in_the_id_format_is_an_argument_error(string id)
    {
        Should.Throw<ArgumentException>(() => Journal.Continue("journals", id)).ParamName.ShouldBe("id");
    }

    [Test]
    public void A_journal_needs_a_directory()
    {
        Should.Throw<ArgumentNullException>(() => Journal.New(null!));
        Should.Throw<ArgumentException>(() => Journal.New(" "));
        Should.Throw<ArgumentException>(() => Journal.Continue("", "20261111T041200Z-a3f9c2"));
        Should.Throw<ArgumentNullException>(() => Journal.Continue("journals", null!));
    }

    [Test]
    public async Task A_settle_is_in_the_file_before_a_dependent_starts()
    {
        using var temporary = new TemporaryDirectory();
        using var copies = new TemporaryDirectory();
        var copying = true;
        var builder = new GraphBuilder();
        var load = builder.Task("load", _ => Task.CompletedTask);
        var publish = builder.Task("publish", _ =>
        {
            // Only in the first run: the run that continues the copy holds it.
            if (copying)
            {
                copies.CopyIn(Directory.GetFiles(temporary.Path, "*.journal").Single());
            }

            return Task.CompletedTask;
        }).Requires(load);
        var graph = builder.Build();
        var first = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path) });
        copying = false;
        var reporter = new RecordingReporter();

        var fromTheCopy = await Runner.RunAsync(
            graph,
            new RunOptions { Journal = Journal.Continue(copies.Path, first.Snapshot.Journal!.Id), Reporters = [reporter] });

        fromTheCopy.Outcome.ShouldBe(RunOutcome.Complete);
        fromTheCopy.Snapshot[load.Path].IsEarlierSuccess.ShouldBeTrue();
        fromTheCopy.Snapshot[publish.Path].IsEarlierSuccess.ShouldBeFalse();
        fromTheCopy.Snapshot.EarlierSuccesses.ShouldBe(1);
        reporter.Events.OfType<TaskStatusChanged>().Where(changed => changed.To == NodeStatus.Succeeded)
            .Select(changed => changed.Node.Path).ShouldBe([publish.Path]);
    }
}
