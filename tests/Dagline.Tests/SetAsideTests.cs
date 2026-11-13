using Dagline.TestSupport;

namespace Dagline.Tests;

public class SetAsideTests
{
    [Test]
    public async Task An_after_task_that_succeeded_is_set_aside_and_runs_again_when_a_task_it_is_after_did_not_succeed()
    {
        using var temporary = new TemporaryDirectory();
        var fail = true;
        var summaries = 0;
        var builder = new GraphBuilder();
        var check = builder.Task("check", context => fail ? throw context.Fail("bad data") : Task.CompletedTask);
        var summary = builder.Task("summary", _ =>
        {
            summaries++;
            return Task.CompletedTask;
        }).After(check);
        var graph = builder.Build();

        var first = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path) });
        fail = false;
        var reporter = new RecordingReporter();
        var second = await Runner.RunAsync(
            graph,
            new RunOptions { Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id), Reporters = [reporter] });

        first.Snapshot[summary.Path].Status.ShouldBe(NodeStatus.Succeeded);
        reporter.Snapshots[0][summary.Path].Status.ShouldBe(NodeStatus.Pending);
        reporter.Snapshots[0].SetAside.ShouldBe([summary.Path]);
        reporter.Snapshots[0].Counts.EarlierSuccesses.ShouldBe(0);
        summaries.ShouldBe(2);
        second.Outcome.ShouldBe(RunOutcome.Complete);
        second.Snapshot.SetAside.ShouldBe([summary.Path]);
        second.Snapshot[summary.Path].IsEarlierSuccess.ShouldBeFalse();
    }

    [Test]
    public async Task An_after_task_keeps_its_earlier_success_when_everything_it_is_after_is_one_an_empty_group_included()
    {
        using var temporary = new TemporaryDirectory();
        var fail = true;
        var builder = new GraphBuilder();
        var load = builder.Task("load", _ => Task.CompletedTask);
        var empty = builder.Group("empty");
        builder.Task("check", context => fail ? throw context.Fail("bad data") : Task.CompletedTask);
        var summary = builder.Task("summary", _ => Task.CompletedTask).After(load).After(empty);
        var graph = builder.Build();

        var first = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path) });
        fail = false;
        var second = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id) });

        second.Snapshot[summary.Path].IsEarlierSuccess.ShouldBeTrue();
        second.Snapshot.SetAside.ShouldBeEmpty();
        second.Snapshot.Counts.EarlierSuccesses.ShouldBe(2);
    }

    [Test]
    public async Task A_requires_dependency_never_sets_a_success_aside_even_when_the_upstream_gained_a_task_that_has_not_run()
    {
        using var temporary = new TemporaryDirectory();
        var triggers = 0;
        var smaller = new GraphBuilder();
        var validate = smaller.Group("validate");
        validate.Task("shard-1", _ => Task.CompletedTask);
        smaller.Task("trigger", _ =>
        {
            triggers++;
            return Task.CompletedTask;
        }).Requires(validate);
        var first = await Runner.RunAsync(smaller.Build(), new RunOptions { Journal = Journal.New(temporary.Path) });

        var grown = new GraphBuilder();
        var grownValidate = grown.Group("validate");
        grownValidate.Task("shard-1", _ => Task.CompletedTask);
        var added = grownValidate.Task("shard-2", _ => Task.CompletedTask);
        var trigger = grown.Task("trigger", _ =>
        {
            triggers++;
            return Task.CompletedTask;
        }).Requires(grownValidate);
        var second = await Runner.RunAsync(
            grown.Build(),
            new RunOptions { Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id) });

        triggers.ShouldBe(1);
        second.Snapshot[trigger.Path].IsEarlierSuccess.ShouldBeTrue();
        second.Snapshot[added.Path].Started.ShouldNotBeNull();
        second.Snapshot.SetAside.ShouldBeEmpty();
        second.Outcome.ShouldBe(RunOutcome.Complete);
    }

    [Test]
    public async Task A_set_aside_spreads_to_a_task_that_is_after_the_task_set_aside_and_not_to_one_that_requires_it()
    {
        using var temporary = new TemporaryDirectory();
        var fail = true;
        var ran = new List<string>();
        var builder = new GraphBuilder();
        var check = builder.Task("check", context => fail ? throw context.Fail("bad data") : Task.CompletedTask);
        var summary = builder.Task("summary", _ => Ran("summary")).After(check);
        var cleanup = builder.Task("cleanup", _ => Ran("cleanup")).After(summary);
        var publish = builder.Task("publish", _ => Ran("publish")).Requires(summary);
        var graph = builder.Build();

        var first = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path) });
        fail = false;
        ran.Clear();
        var second = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id) });

        first.Snapshot.Counts[NodeStatus.Succeeded].ShouldBe(3);
        second.Snapshot.SetAside.ShouldBe([summary.Path, cleanup.Path]);
        ran.ShouldBe(["summary", "cleanup"]);
        second.Snapshot[publish.Path].IsEarlierSuccess.ShouldBeTrue();
        second.Snapshot[check.Path].IsEarlierSuccess.ShouldBeFalse();

        Task Ran(string name)
        {
            lock (ran)
            {
                ran.Add(name);
            }

            return Task.CompletedTask;
        }
    }

    [Test]
    public async Task A_task_set_aside_in_a_run_stopped_before_it_ran_again_is_still_not_an_earlier_success_in_the_next_run()
    {
        using var temporary = new TemporaryDirectory();
        var fail = true;
        var stop = new StopSignal();
        var succeedsOnceStopped = new StoppedBody(onStop: _ => { });
        var summaries = 0;
        var builder = new GraphBuilder();
        var check = builder.Task("check", context =>
        {
            if (fail)
            {
                throw context.Fail("bad data");
            }

            stop.Request();
            return succeedsOnceStopped.Body(context);
        });
        var summary = builder.Task("summary", _ =>
        {
            summaries++;
            return Task.CompletedTask;
        }).After(check);
        var graph = builder.Build();

        var first = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path) });
        var id = first.Snapshot.Journal!.Id;
        fail = false;
        var second = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.Continue(temporary.Path, id) }, stop);
        var third = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.Continue(temporary.Path, id) });

        second.Outcome.ShouldBe(RunOutcome.Stopped);
        second.Snapshot.SetAside.ShouldBe([summary.Path]);
        second.Snapshot[summary.Path].Status.ShouldBe(NodeStatus.Ready);
        third.Snapshot[check.Path].IsEarlierSuccess.ShouldBeTrue();
        third.Snapshot[summary.Path].IsEarlierSuccess.ShouldBeFalse();
        third.Snapshot.SetAside.ShouldBeEmpty();
        summaries.ShouldBe(2);
        third.Outcome.ShouldBe(RunOutcome.Complete);
    }

    [Test]
    public async Task A_runs_set_asides_are_recorded_after_its_start_and_before_its_first_task_starts()
    {
        using var temporary = new TemporaryDirectory();
        var fail = true;
        var builder = new GraphBuilder();
        var check = builder.Task("check", context => fail ? throw context.Fail("bad data") : Task.CompletedTask);
        builder.Task("summary", _ => Task.CompletedTask).After(check);
        var graph = builder.Build();
        var first = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path) });
        fail = false;
        await Runner.RunAsync(graph, new RunOptions { Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id) });

        List<JournalSummary> listed = [];
        foreach (var cut in JournalCutter.Cut(Directory.GetFiles(temporary.Path, "*.journal").Single()).Where(cut => !cut.InsideALine))
        {
            using var copy = new TemporaryDirectory();
            cut.WriteTo(copy.Path);
            listed.Add((await Journals.ListAsync(copy.Path)).Single());
        }

        var secondRunStarted = listed.FindIndex(summary => summary.Runs == 2);
        var (started, setAside, checkStarted) = (listed[secondRunStarted], listed[secondRunStarted + 1], listed[secondRunStarted + 2]);
        started.Counts[NodeStatus.Pending].ShouldBe(0);
        started.Counts[NodeStatus.Succeeded].ShouldBe(1);
        setAside.Counts[NodeStatus.Pending].ShouldBe(1);
        setAside.Counts[NodeStatus.Running].ShouldBe(0);
        checkStarted.Counts[NodeStatus.Running].ShouldBe(1);
    }

    [Test]
    public async Task Set_asides_are_on_disk_before_any_task_starts()
    {
        using var temporary = new TemporaryDirectory();
        using var copies = new TemporaryDirectory();
        var fail = true;
        var builder = new GraphBuilder();
        var check = builder.Task("check", context =>
        {
            if (fail)
            {
                throw context.Fail("bad data");
            }

            copies.CopyIn(Directory.GetFiles(temporary.Path, "*.journal").Single());
            return Task.CompletedTask;
        });
        builder.Task("summary", _ => Task.CompletedTask).After(check);
        var graph = builder.Build();
        var first = await Runner.RunAsync(graph, new RunOptions { Journal = Journal.New(temporary.Path) });
        fail = false;
        await Runner.RunAsync(graph, new RunOptions { Journal = Journal.Continue(temporary.Path, first.Snapshot.Journal!.Id) });

        var summaryAlone = new GraphBuilder();
        var summary = summaryAlone.Task("summary", _ => Task.CompletedTask);
        var fromTheCopy = await Runner.RunAsync(
            summaryAlone.Build(),
            new RunOptions { Journal = Journal.Continue(copies.Path, first.Snapshot.Journal.Id) });

        fromTheCopy.Snapshot[summary.Path].IsEarlierSuccess.ShouldBeFalse();
        fromTheCopy.Snapshot[summary.Path].Status.ShouldBe(NodeStatus.Succeeded);
    }
}
